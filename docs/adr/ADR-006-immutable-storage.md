# ADR-006: immutable S3 recovery points

- Статус: **Accepted as V1 architecture; provider profiles require certification**
- Дата: **3 сентября 2026**
- Область: ransomware resilience, retention и S3-compatible storage

## Контекст

Restic шифрует и проверяет repository data, но credential с delete permissions может
уничтожить repository. S3 Object Lock защищает object versions, однако versioning,
retention mode, permissions, delete markers и lifecycle определяют реальную гарантию.

Restic также использует lock objects и maintenance/prune операции, поэтому нельзя считать
любой Object Lock bucket автоматически совместимым operational repository.

## Решение

1. Immutable protection является свойством deployment profile, а не checkbox.
2. Каждый verified recovery point получает version-aware Recovery Point Manifest.
3. Backup, restore, maintenance, replication и security identities разделяются.
4. Endpoint никогда не получает `DeleteObjectVersion` или governance bypass.
5. Direct Locked Repository допускается только после provider-specific compatibility test.
6. При отсутствии доказательства используется Operational Repository + Immutable Mirror.
7. Recovery tool восстанавливает exact versions из RPM и не доверяет current object view.
8. Минимум одна критичная copy располагается в независимом administrative domain.

## Default product posture

- Community/local: immutable local target не обещается без отдельного filesystem profile.
- SMB Pro: Governance mode допустим после предупреждения о bypass/admin risks.
- Enterprise: Compliance mode или эквивалентный WORM profile после legal/cost approval.
- Legal hold доступен только отдельной authorised role.

## Почему не обещаем «невозможно удалить»

Некоторые providers допускают прекращение account/project, после которого данные могут
быть потеряны независимо от object-level retention. Поэтому Fortiq обещает конкретный
проверенный recovery window в заданном administrative domain, а не абсолютную вечность.

## Restic compatibility gate

До завершения integration suite Fortiq не объявляет direct Object Lock bucket официально
поддерживаемым. Suite включает backup, concurrent lock, interrupted job, check, restore,
forget/prune, delete markers, version-specific recovery и lifecycle после expiry.

Если direct profile не проходит, это не отменяет restic как V1 engine: immutable guarantee
обеспечивается отдельным mirror profile.

## Последствия

Положительные:

- endpoint compromise не равен уничтожению recovery history;
- восстановление не зависит от current version/delete markers;
- provider differences становятся тестируемыми;
- immutability входит в Recovery Confidence как evidence.

Отрицательные:

- RPM и mirror materialization усложняют recovery tool;
- locked noncurrent versions повышают стоимость storage;
- prune не освобождает место немедленно;
- provider certification требует постоянных canary tests;
- Compliance mode делает ошибки retention операционно необратимыми до expiry.

## Источники

- [AWS: S3 Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html)
- [AWS: Object Lock considerations](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html)
- [AWS: delete markers](https://docs.aws.amazon.com/AmazonS3/latest/userguide/DeleteMarker.html)
- [Scaleway: Object Lock](https://www.scaleway.com/en/docs/object-storage/api-cli/object-lock/)
- [OVHcloud: Object Storage security and Object Lock](https://help.ovhcloud.com/csm/en-ie-documentation-public-cloud-storage-object-storage-s3-security?id=kb_browse_cat&kb_category=4bae79882c21fe144a4e082b79ed2f79&kb_id=574a8325551974502d4c6e78b7421938)
- [restic: append-only repository considerations](https://restic.readthedocs.io/en/stable/060_forget.html#security-considerations-in-append-only-mode)

