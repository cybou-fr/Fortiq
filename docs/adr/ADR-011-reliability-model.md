# ADR-011: evidence-based health model

- Статус: **Accepted; numerical SLOs pending baseline**
- Дата: **3 сентября 2026**
- Область: health, retries, reconciliation и observability

## Контекст

Process uptime и отсутствие последних ошибок не доказывают восстановимость. Backup system
может быть online и регулярно сообщать success, но иметь недоступный ключ, повреждённый
repository или ни разу не проверенный restore.

Кроме того, единый retry policy опасен: повтор transient upload полезен, а повтор удаления,
неверного password или integrity failure может усилить инцидент.

## Решение

1. Health строится как иерархия независимых recovery evidence.
2. Итоговый status определяется худшим обязательным policy signal.
3. `Unknown` является отдельным состоянием и не трактуется как success.
4. Jobs используют durable state machine, idempotency и startup reconciliation.
5. Ошибки классифицируются до retry; destructive/integrity failures fail closed.
6. Retry имеет persistent budget, exponential backoff и full jitter.
7. Observability data минимизируются и отделяются от audit ledger.
8. SLO numbers публикуются только после измеримого baseline.
9. AI не участвует в расчёте health и SLI.

## Почему не единый процент

Среднее может скрыть абсолютный blocker: несколько свежих backups не компенсируют потерю
единственного unlock path. UI использует категорию и объяснимые factors; числовой score
появится только после validation против фактических restore outcomes.

## Почему backup продолжается при части control failures

Остановка защитной записи из-за недоступности control plane/audit anchor может увеличить
потерю данных. Поэтому safe backup продолжает работу в degraded mode, а операции,
ослабляющие protection, блокируются.

## Последствия

Положительные:

- зелёный status связан с recovery evidence;
- restart/crash становится проектируемым сценарием;
- retries не создают бесконтрольных штормов и повторных mutations;
- alerts отражают нарушенное обещание пользователю;
- SLO не маскирует security failure error budget-ом.

Отрицательные:

- больше persistent state и reconciliation logic;
- требуется taxonomy ошибок всех providers;
- полноценный baseline требует длительных failure/load tests;
- честное `Unknown` может повышать число видимых предупреждений на ранних версиях.

## Validation gate

- deterministic state-machine tests;
- crash/power-loss matrix;
- provider fault injection;
- production-like resource pressure runs;
- сравнение прогнозируемого RTO с измеренным restore;
- alert usability/on-call exercises;
- минимум один полный DR drill с недоступным control plane.

