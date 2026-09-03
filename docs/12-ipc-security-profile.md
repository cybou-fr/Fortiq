# IPC protocol и security profile

## Область

Документ определяет локальный IPC для:

- Desktop → Fortiq Service;
- Fortiq Service → Privileged Windows Broker;
- restic password helper → процесс-владелец key lease.

AI Broker использует отдельный read-only контракт и не имеет маршрута к privileged
операциям или key lease.

## Имена endpoints

```text
\\.\pipe\Fortiq\v1\service
\\.\pipe\Fortiq\v1\windows-broker
\\.\pipe\Fortiq\v1\secret\<operation-id>
```

`operation-id` — канонический UUID, не секрет. Произвольные пользовательские строки,
repository names и пути не включаются в имя pipe.

## Windows protection layers

Каждый server endpoint:

- создаётся с явным security descriptor, без reliance на default DACL;
- использует `PIPE_REJECT_REMOTE_CLIENTS`;
- первая server instance использует `FILE_FLAG_FIRST_PIPE_INSTANCE`;
- задаёт минимально необходимые read/write права;
- ограничивает число instances и размеры buffers;
- использует asynchronous/overlapped I/O с deadline и cancellation;
- после connect проверяет Windows identity peer до разбора команды.

`FILE_FLAG_FIRST_PIPE_INSTANCE` превращает попытку предварительного захвата имени pipe в
явный отказ запуска сервиса. Он не заменяет проверку server identity клиентом.

## Матрица identities

| Endpoint | Server | Разрешённый client | Принцип ACL |
|---|---|---|---|
| `service` | Fortiq Service SID | Fortiq Operators / authorised user | connect/read/write, без Everyone |
| `windows-broker` | Broker Service SID | только Fortiq Service SID | SYSTEM + точный service SID |
| `secret/<id>` | Service или Recover owner | ровно ожидаемая identity | one instance, one read |

Installer создаёт локальную группу `Fortiq Operators` только если она необходима
deployment profile. Членство в группе разрешает подключение к Service, но не означает
разрешение любой команды.

## Проверка клиента сервером

После подключения server:

1. читает минимальный `ClientHello`, чтобы identity соответствовала последнему pipe message;
2. вызывает `ImpersonateNamedPipeClient` и проверяет результат;
3. открывает thread token только для чтения identity/claims;
4. проверяет expected user/service SID, logon type, integrity level и запрещённые свойства;
5. немедленно вызывает `RevertToSelf` в `finally`;
6. при любой ошибке закрывает connection без выполнения команды.

Privileged operation никогда не выполняется во время impersonation. Impersonation служит
для аутентификации/авторизации peer; затем broker выполняет строго типизированную команду
под собственной identity.

Если impersonation не удалась, запрещено продолжать под privileged server identity.

`GetNamedPipeClientProcessId` используется как дополнительный signal для correlation,
process lifetime и audit. PID, executable path или code-signing status по отдельности не
считаются authentication factor из-за PID reuse и TOCTOU.

## Проверка сервера клиентом

Client не считает успешный `Connect` доказательством, что подключился к Fortiq:

1. получает server PID;
2. открывает process token с минимальными query rights;
3. проверяет ожидаемый service SID и integrity profile;
4. для release build проверяет installation/binary identity как дополнительный signal;
5. прекращает соединение при несовпадении.

Клиент открывает pipe с `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`, если server не
должен выполнять действия от имени клиента. Более высокий impersonation level требует
отдельного обоснования.

## Protocol framing

Transport использует message-oriented либо однозначное length-prefixed framing:

```text
FrameV1 {
  magic:       fixed 4 bytes,
  version:     uint16,
  flags:       uint16,
  length:      uint32,
  payload:     bounded bytes
}
```

- Максимальный frame: 1 MiB для control API и 64 KiB для secret helper.
- Streaming events разбиваются на отдельные frames.
- Compression отключена, чтобы исключить decompression bombs.
- Unknown critical message/field закрывает request с `UnsupportedProtocol`.
- Allocation выполняется только после проверки length.
- Parser имеет depth/collection/string limits.

Payload может кодироваться Protobuf, но `.proto` является versioned wire contract, а не
прямой сериализацией внутренних domain types.

## Handshake и anti-replay

```text
ClientHello  { protocol range, client instance ID, request nonce }
ServerHello  { selected protocol, server instance ID, server nonce }
Request      { request ID, server nonce, command, deadline, payload }
Response     { request ID, result, audit correlation ID }
```

- Nonces создаются CSPRNG для каждого connection.
- Request обязан вернуть nonce текущего connection.
- Request ID уникален в пределах server boot/session и хранится в bounded replay cache.
- Старый request нельзя повторить на новом connection.
- Deadline задаётся абсолютным UTC временем и ограничивается server maximum.
- OS-authenticated pipe обеспечивает transport integrity; собственная криптография поверх
  pipe не добавляется без новой threat model.

Anti-replay не превращает повторяемую business operation в безопасную. Каждая команда
также имеет idempotency semantics и проверяет текущее domain state.

## Authorization model

Авторизация выполняется по `(peer identity, command, resource, state)`, а не только по
факту подключения.

Примеры:

| Команда | Desktop | Service | Broker |
|---|---:|---:|---:|
| ReadRepositoryStatus | authorised | — | — |
| ProposeRestore | authorised | — | — |
| ApproveRestore | operator/admin policy | — | — |
| CreateVssSnapshot | нет | authorised caller | fixed implementation |
| ReadUsnDelta | нет | authorised caller | bounded volume operation |
| DeleteSnapshot | отдельное подтверждение | policy checked | не поддерживается |
| AcquireEus | нет | internal only | не поддерживается |

Broker не принимает универсальные `Run`, `Open`, `ReadFile`, raw IOCTL или произвольные
command-line arguments. Для каждой операции существует отдельный request type и validator.

## Confused deputy protection

- Volume/source выбирается из ранее зарегистрированных stable identifiers.
- Все пути canonicalize/resolve; reparse behavior задан policy.
- Request содержит repository/job ID, который повторно загружается из доверенного state.
- Desktop не может передать storage credentials или destination executable broker-у.
- Broker возвращает opaque handle/reference, а не расширяет права на произвольный объект.
- Approval связывается с operation digest, identity, expiry и ожидаемым state version.
- Изменение плана после approval делает approval недействительным.

## Secret helper profile

`secret/<operation-id>` имеет отдельный минимальный протокол:

1. ровно один ожидаемый client process;
2. одна handshake попытка;
3. server проверяет identity и PID;
4. EUS выдаётся ровно один раз;
5. response не содержит metadata, кроме bytes секрета;
6. повторный read получает закрытый pipe;
7. timeout уничтожает lease и endpoint;
8. stdout helper содержит только `Base64UrlNoPadding(EUS)` и один newline;
9. stderr, telemetry и crash logs helper отключены/ограничены.

Operation ID не является capability secret. Без разрешённого Windows token подключение
должно завершиться на ACL/identity validation.

## Error handling и audit

Внешние ответы не различают детали, полезные для enumeration. Audit record содержит:

- connection/request IDs;
- verified SID и PID как audit attributes;
- command type и resource ID;
- authorization decision/reason code;
- protocol version, duration и outcome;
- никогда не содержит payload secrets или recovery material.

Последовательные authentication failures ограничиваются rate limit. Переполнение очереди,
frame или deadline приводит к fail-closed завершению конкретного connection.

## Обязательные security tests

- remote connection отвергается;
- Everyone/обычный неавторизованный user не открывает endpoint;
- поддельный процесс под другой identity отвергается;
- pre-created pipe заставляет service fail startup, а не подключаться к attacker endpoint;
- client отвергает pipe server с неверным service SID;
- повтор Request на новом connection отвергается;
- duplicate/oversized/truncated frame не вызывает excessive allocation;
- impersonation failure не приводит к privileged execution;
- `RevertToSelf` выполняется при success, exception и cancellation;
- изменённый operation после approval отвергается;
- secret helper допускает только одно чтение и закрывается по timeout;
- fuzzing framing и payload parser;
- multi-user/RDP sessions не пересекают repository access.
