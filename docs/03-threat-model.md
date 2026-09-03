# Threat model и границы доверия

## Защищаемые активы

- содержимое и metadata резервных копий;
- Repository Master Key и его обёртки;
- recovery secret;
- device credentials и KMS credentials;
- целостность snapshots, retention policy и audit trail;
- доступность автономного восстановления.

## Предполагаемые противники

- malware или ransomware с правами пользователя;
- компрометированный администратор endpoint;
- атакующий, получивший backup storage credentials;
- недоверенный storage/cloud provider;
- сетевой атакующий;
- похититель устройства или recovery sheet;
- вредоносное содержимое файла, пытающееся воздействовать на AI.

## Требуемое поведение

| Сценарий | Требуемый результат |
|---|---|
| Украден выключенный ноутбук | Данные и reusable credentials не раскрываются |
| Endpoint заражён ransomware | Immutable snapshots остаются доступными |
| Потерян TPM-компьютер | Доступ восстанавливается независимым recovery method |
| Недоступен KMS | Система следует явной fail-open/fail-closed policy |
| Потеряна локальная БД | Репозиторий можно обнаружить и восстановить автономно |
| Скомпрометирован storage | Нарушение конфиденциальности или целостности обнаруживается/предотвращается |
| Украдена mnemonic | Дополнительная passphrase/quorum снижает риск раскрытия |
| Документ содержит prompt injection | AI не получает полномочий и не исполняет инструкцию |
| Fortiq прекращает работу | Open recovery tool продолжает читать поддерживаемый формат |

## Не обещаем без дополнительного контура

- защиту данных на уже разблокированном полностью скомпрометированном устройстве;
- абсолютную анонимность metadata;
- юридическое соответствие только фактом установки продукта;
- сохранность при одновременной потере всех key wrappers и recovery material;
- корректность application-consistent snapshot без участия соответствующего VSS writer.

## Security gates до production

- документированный cryptographic design review;
- внешний penetration test IPC, updater и restore flow;
- fuzzing парсеров repository metadata;
- тест восстановления после потери catalog/control-plane;
- ransomware simulation;
- supply-chain policy, SBOM и подписанные обновления;
- независимая проверка recovery CLI.

