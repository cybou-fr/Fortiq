# План выпуска Fortiq Community 0.1.0-beta.1

Дата: 2026-09-06. Обновлено: 2026-09-07. Основные исправления реализованы; локальный кандидат собран. Чистая Windows и проверка разных UAC identities остаются открытыми. Фактические результаты и ограничения: [журнал реализации](audits/community-beta-20260907.md). Чек-листы ниже сохраняют исходные критерии приёмки; реализация кода не означает автоматического закрытия сквозных проверок.

## Основание и границы проверки

План основан на предоставленном отчёте и чтении текущего рабочего дерева. HEAD — `5dc298124dec51112ce0b7c14c57842f57be8143`, как в отчёте. Рабочее дерево содержит незакоммиченные изменения Desktop, service/IPC, recovery phrase tracking, health publication, recovery tool и тестов. Их нельзя считать проверенной частью main или перезаписывать при реализации этого плана.

Подтверждены статическим анализом: marker внутри bundle, обязательность device envelope для текущего Backup Now, отсутствие portable clear-lock, перезапись recurrence, прямой IPC для изменения Source Settings, отсутствие tag/version gate, HKCU autostart из installer и устаревшая строка README. Это не результат воспроизведения на чистой Windows: сборка, тесты, Setup и UAC в рамках этой проверки не запускались. Состояние удалённого main и GitHub Releases не проверялось. Численные оценки качества из отчёта не используются как критерий готовности.

Цель: надёжный Windows x64 Community Desktop release через Setup.exe и ZIP. Рекомендуемая граница beta: новая защита требует рабочего device-bound key; восстановление существующего repository по kit и mnemonic остаётся независимым. Attended backup по mnemonic переносится в следующую версию.

## Очередь работ

### B0 — Зафиксировать исходную точку

- [ ] Отделить имеющиеся изменения от новых исправлений в истории работы, не откатывая их. Проверить их согласованность, особенно provisioning и подтверждение recovery phrase.
- [ ] Записать исходный результат существующих тестов и documentation claims; явно перечислить пропущенные Windows/privilege lanes.
- [ ] После интеграции повторно проверить затронутые этим планом участки: текущие незакоммиченные изменения могут менять поведение.

Готово, когда известны проверяемый commit, исходные failures/skips и состав изменений. С этого момента — feature freeze для beta.

### B1 — P0: Setup → Install и повторное использование cache

Код: `src/Fortiq.Setup/Program.cs`, `src/Fortiq.Desktop/InstallationManager.cs`; существующие `BundleManifestTests`, `BundleIntegrityTests`, `LocalSourceInstallTests`.

- [ ] Вынести completion marker за bundle root; не добавлять исключение в validator.
- [ ] Обработать старый cache с внутренним `.complete`, прерванную распаковку и повторный запуск той же версии.
- [ ] Проверять cache перед запуском Desktop. Эталон должен происходить из embedded payload Setup: manifest, который можно изменить вместе с файлами cache, сам по себе не устанавливает доверие.
- [ ] Проверить расположение portable state: изменяемые schedules/receipts/credentials не должны попадать в immutable payload, ломать validation или удаляться при повторной распаковке.
- [ ] При восстановлении cache учитывать работающий Desktop и параллельный Setup: не удалять используемые файлы или пользовательское состояние.

Приёмка: настоящий собранный Setup распаковывает bundle, создаёт marker, запускает Desktop, установка проходит строгую validation. Повторный запуск проходит; отсутствующий/изменённый payload восстанавливается или выдаёт понятную ошибку до запуска. Посторонний payload-файл по-прежнему отвергается. Проверка validator отдельно не заменяет сквозной тест Setup → Install.

### B2 — P0: честный контракт без device key

Код: `ProtectRepositoryAdapter`, `ProtectRepositoryViewModel`, `MainWindow`, `Program`, `ServiceIpcHost`, `RepositoryProvisioner`, `UnattendedBackup`.

- [ ] Для Community protection потребовать успешное создание и применимость ключа нужной identity: portable user key, installed machine key для service.
- [ ] Ранняя UI-проверка объясняет ограничение, но authoritative provisioning также обрабатывает реальный отказ ключа; одной capability-проверки недостаточно.
- [ ] Не выдавать успех и не создавать неработающий protected source/schedule. Проверить rollback только созданных текущей попыткой ресурсов; существующие repository и kit сохранять.
- [ ] Для уже существующих источников без envelope показывать конкретное состояние и доступное восстановление, без обещания работоспособного Backup Now.
- [ ] Согласовать все сообщения и README; сохранить recovery kit + mnemonic flow без device key.

Приёмка: отсутствие ключа и отказ его создания в обоих режимах дают понятный результат без фиктивной защиты; рабочий ключ позволяет backup; recovery без device key восстанавливает данные. Изменения не теряют запись о неподтверждённой recovery phrase.

### B3 — P1, обязательный для beta: portable Stop → Clear lock → Backup

Код: `SourceSettingsAdapter`, composition в Desktop `Program`, `StaleLockRecovery`, `SourceSettingsWindow`; `StaleLockRecoveryTests`.

- [ ] Подключить локальный StaleLockRecovery с теми же engine, credentials, run registry и receipts, которые использует portable backup.
- [ ] Сохранить явное действие пользователя и предупреждение о другом компьютере: локальный registry не доказывает отсутствие удалённого владельца lock.
- [ ] Запретить clear-lock при активной локальной операции; дождаться завершения отменённого engine перед следующей операцией.

Приёмка: реальный portable backup прерывается, оставшийся lock можно снять и выполнить следующий backup; активный локальный run блокирует unlock; ошибки и результат отражаются в UI/evidence. Также проверить отмену drill. Зависимость: B2, общий путь привилегий согласовать с B5.

### B4 — P1, обязательный для beta: сохранить семантику расписаний

Код: `SourceSettingsAdapter`, `SourceSettingsViewModel`, `SourceSettingsWindow`, `FileSystemScheduleStore`, DTO настроек/IPC.

- [ ] Передавать намерение изменения recurrence явно: отсутствие редактирования означает сохранить исходное значение.
- [ ] DailyAt редактируется с сохранением days/timezone. Поддерживаемые store, но не GUI варианты показываются как Custom schedule без подстановки 02:30.
- [ ] Проверить также drill recurrence: преобразование interval в целое число дней может незаметно изменить период. Не представимые GUI значения сохранять при изменении других настроек.
- [ ] Сохранить fail-closed чтение действительно неизвестных видов recurrence; не обещать поддержку произвольного custom JSON.

Приёмка: retention-only, pause-only и прочие независимые изменения сохраняют backup/drill recurrence, days и timezone; намеренное изменение DailyAt сохраняется. Проверить оба пути — service и portable — и неизвестные поля документа.

### B5 — P1, обязательный для beta: единый UAC для Source Settings

Код: `SourceSettingsAdapter`, `ServiceIpcClient`, `Program`, существующий elevated operation flow и IPC authorization.

- [ ] Чтение настроек оставить без elevation. Save, Stop protection и Clear lock: Operator выполняет напрямую, прочий пользователь — через краткоживущий elevated worker.
- [ ] Worker принимает только допустимую операцию и валидированные параметры существующего source; provision остаётся admin-only, неизвестные IPC-команды fail closed.
- [ ] Возвращать завершение/ошибку в исходное окно; отмена UAC сохраняет несохранённые настройки, не имитирует успех.

Приёмка: Operator без UAC, unelevated local admin с UAC, standard user с credentials другого admin, отказ UAC и недоступная service. Тестировать реальную identity, а не только mock authorization. Зависимость: окончательные DTO из B4.

### B6 — P1, обязательный для beta: tag совпадает с версией

Код: `.github/workflows/release.yml`, `scripts/Get-FortiqVersion.ps1`.

- [ ] В начале tagged release run сравнить `GITHUB_REF_NAME` с `v` + каноническая версия; mismatch завершает workflow до публикации.
- [ ] Сохранить workflow_dispatch для сборки без tag и без создания Release.

Приёмка: правильный tag проходит, неправильный останавливается; имена Setup/ZIP, manifest, версия приложения и release tag согласованы.

### B7 — P1: autostart принадлежит исходному пользователю

Код: `InstallationManager`, `WindowsAutostartController`, install UI/worker result flow.

- [ ] Настраивать HKCU autostart из исходного unelevated Desktop после успешной установки; использовать установленный executable.
- [ ] Определить поведение headless install и uninstall: не выдавать запись HKCU elevated identity за настройку другого пользователя.
- [ ] Ошибка autostart видна отдельно от результата установки service.

Приёмка: установка Alice с credentials Admin меняет autostart Alice, не Admin; обычная установка и отказ установки не создают ошибочных записей.

### B8 — Документация и проверка кандидата

- [ ] Исправить README о выборе отдельного файла/папки после проверки реального recovery flow; crash-resume описывать отдельно.
- [ ] Согласовать README-FIRST, recovery guide и release notes с B2, portable lock recovery и фактическими ограничениями beta.
- [ ] Выполнить `scripts/Test-DocumentationClaims.ps1`, релевантные regression tests, затем полный release test run и сборку Setup/ZIP.
- [ ] Прогнать installed pilot через `scripts/Test-InstalledPilot.ps1`; пропуски обязательных сценариев не считать успехом.
- [ ] Сохранить результаты на чистой Windows: Setup → Install → Protect → Backup → Prove → file/folder restore; portable эквивалент; cancellation/lock recovery; identity/UAC matrix; no-device-key recovery; повторная установка с сохранением состояния.
- [ ] На отдельной чистой машине восстановить только из kit, phrase и release tools; сравнить хеши восстановленных данных. Проверить service после перезагрузки.

Готово, когда evidence привязан к точному commit и хешам кандидата. Локальный bundle smoke test не заменяет чистую машину и другой Windows account.

## Порядок и решение о выпуске

Рекомендуемая последовательность небольших изменений: B0 → B1 → B2 → B3 → B4 → B5 → B6 → B7 → B8. B6 технически независим; B1/B2 не требуют завершения settings work. Для каждого исправления — целевой regression test, затем интеграционная проверка итогового кандидата.

Публичную beta выпускать после B1–B6 и B8. B7 включить в этот же цикл; если откладывать, явно ограничить обещание per-user autostart и зафиксировать известный дефект. Согласие отчёта выпустить после первых трёх исправлений слишком мягкое: тихая смена расписания и несогласованный release tag тоже должны быть устранены до первого релиза.

Release tag и публикация — отдельный следующий шаг после готового кандидата; данный документ ничего не публикует. В release notes фиксировать фактический статус подписи и известные ограничения.

## После beta

- Attended backup с вводом mnemonic: отдельный session/key lifecycle и проверка утечек, без сохранения phrase и передачи через командную строку.
- Небольшое GUI Setup с прогрессом распаковки.
- Дальнейшая автоматизация pristine-machine recovery lane и доработка update delivery согласно общему roadmap.
- Enterprise/fleet, AI, новые key envelopes и расширение платформ не входят в стабилизацию Community beta.
