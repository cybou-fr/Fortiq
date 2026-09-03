# ADR-009: recovery-first UX

- Статус: **Accepted**
- Дата: **3 сентября 2026**
- Область: Desktop information architecture и security workflows

## Контекст

Типичный backup UI оптимизирован под создание расписания и сообщение `backup successful`.
Это создаёт ложную уверенность: наличие записанного snapshot не доказывает доступность
ключа, целостность repository и фактическую восстановимость.

Сложная терминология трёх key modes также заставляет пользователя принимать
криптографическое решение, не понимая последствия потери устройства.

## Решение

Fortiq использует recovery-first UX:

1. Home показывает Recovery Confidence и последний verified restore.
2. Daily unlock и disaster-recovery method настраиваются отдельно.
3. Onboarding завершается test restore, а не только первым upload.
4. Один repository может иметь несколько unlock envelopes.
5. Restore по умолчанию выполняется в новый staging target.
6. Destructive operations используют plan → impact → approval → verification.
7. AI формирует предложения, но не security status и не действия.
8. Криптографические детали скрыты в progressive disclosure, не удалены из продукта.

## Статусная модель

`Protected` разрешён только при выполнении минимального набора evidence. Если evidence
неполны, UI использует `Attention required`, `At risk` или `Unknown`, а не оптимистичный
зелёный статус.

Recovery Confidence сначала показывается категориями, а не процентом. Числовая оценка
появится только после валидации формулы и понимания false-confidence risk.

## Последствия

Положительные:

- продукт дифференцируется фактическим восстановлением;
- пользователь понимает независимый recovery path;
- снижается риск опасного in-place restore;
- AI не размывает trust boundaries;
- технические evidence превращаются в понятные действия.

Отрицательные:

- onboarding дольше обычного backup wizard;
- первый restore-test расходует время и ресурсы;
- потребуется исследование терминологии в нескольких языках;
- честные статусы могут выглядеть менее «зелёными», чем у конкурентов.

Эти издержки принимаются, поскольку основное обещание Fortiq — восстановимость, а не
минимальное число кликов до первого upload.

## Validation gate

До фиксации публичного UI flow проводятся usability tests минимум с:

- владельцем небольшой компании без backup expertise;
- MSP/operator;
- enterprise security administrator;
- участником, выполняющим recovery на чистой машине только по recovery kit.

