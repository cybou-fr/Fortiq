# ADR-004: Windows Named Pipes для локального IPC

- Статус: **Accepted for Windows V1**
- Дата: **3 сентября 2026**
- Область: Desktop, Service, Windows Broker и secret helper

## Контекст

Компонентам Fortiq нужен локальный IPC с Windows-native identity и ACL. Особенно важна
граница между обычной службой и privileged VSS/USN broker. Transport должен работать без
локального TCP listener и поддерживать проверку service/user token.

## Решение

Использовать Windows Named Pipes с явными security descriptors и протоколом из
[IPC security profile](../12-ipc-security-profile.md).

Основные требования:

- local-only через `PIPE_REJECT_REMOTE_CLIENTS`;
- защита server name через `FILE_FLAG_FIRST_PIPE_INSTANCE`;
- service SID вместо доверия ко всему `LocalSystem`/`Administrators`;
- взаимная проверка peer token;
- `SECURITY_IDENTIFICATION` для ограничения impersonation;
- versioned bounded messages;
- connection nonce, request ID, deadline и idempotency;
- отдельный одноразовый endpoint для EUS.

## Почему не loopback HTTP/gRPC

Loopback TCP добавляет port lifecycle, firewall/proxy interactions и не даёт столь прямой
Windows ACL/token модели. gRPC framing можно использовать концептуально, но transport V1
остаётся Named Pipes. Решение может быть пересмотрено для Linux/macOS adapters.

## Почему ACL недостаточно

Ошибочная DACL, group membership и multi-user host могут расширить круг подключающихся.
Поэтому server дополнительно проверяет impersonation token, а каждая команда проходит
domain authorization. PID и имя executable являются только дополнительными signals.

## Почему app-level encryption не добавляется

Named Pipe является локальным securable object; доступ и identity обеспечиваются Windows.
Самодельный channel encryption не исправит неверную ACL или confused-deputy API, но
добавит key distribution и downgrade risks. Он появится только при новой threat model.

## Последствия

Положительные:

- нативная Windows identity и DACL;
- отсутствие listening TCP port;
- отдельные endpoints с минимальными правами;
- возможность проверить client/server PID и token.

Отрицательные:

- Windows-specific adapter и Win32 interop;
- необходимость тщательно управлять impersonation/revert;
- отдельный transport потребуется на Linux/macOS;
- стандартный high-level .NET API может не экспонировать все необходимые flags/checks,
  поэтому допустим узкий audited P/Invoke layer.

## Security gate

Перед release должны быть проверены фактические SDDL, service SID type, process tokens и
pipe flags на поддерживаемых Windows 10/11 и Windows Server profiles. Документированное
намерение без integration test не считается выполненной границей безопасности.

## Источники

- [Microsoft: Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- [Microsoft: CreateNamedPipe flags](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea)
- [Microsoft: GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
- [Microsoft: ImpersonateNamedPipeClient](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient)
- [Microsoft: Named Pipe client impersonation](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)
- [Microsoft: service SID types](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_sid_info)

