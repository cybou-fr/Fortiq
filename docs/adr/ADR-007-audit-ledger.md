# ADR-007: tamper-evident audit ledger

- Статус: **Accepted as V1 baseline; signing suite pending**
- Дата: **3 сентября 2026**
- Область: audit events, compliance evidence и offline verification

## Контекст

Обычный текстовый лог легко изменить или удалить после компрометации endpoint. Только
локальная цифровая подпись также недостаточна: атакующий может откатить весь журнал или,
захватив активный ключ, подписывать будущие записи.

Fortiq требуется проверяемый журнал без записи backup contents и key material.

## Решение

1. Security-relevant события кодируются deterministic CBOR.
2. События образуют последовательную SHA-256 hash chain.
3. Ограниченные segments закрываются `COSE_Sign1` checkpoints.
4. Checkpoints регулярно публикуются во внешнем immutable/customer-owned anchor.
5. Open-source offline verifier проверяет encoding, chain, signatures и anchors.
6. Нарушение audit integrity включает degraded mode для опасных операций.
7. Audit schema хранит pseudonymous references вместо filenames/content по умолчанию.

## Почему COSE_Sign1

Audit и recovery formats уже используют CBOR. COSE предоставляет стандартизованную
структуру single-signer signature, protected headers и algorithm identifiers без создания
собственного signature container.

Выбор COSE не определяет автоматически подходящий algorithm, certificate profile или
trust anchor. Они остаются отдельным решением.

## Почему hash chain плюс anchor

- Hash chain обнаруживает изменение и перестановку записанных событий.
- Подписанный checkpoint связывает segment с signing identity.
- Внешний anchor обнаруживает локальный rollback/truncation до последнего anchor.

Ни один слой отдельно не обеспечивает все три свойства.

## Availability policy

Audit failure не должен автоматически останавливать создание новых backup, поскольку это
может усилить инцидент. Однако операции, способные уничтожить данные, ослабить retention
или отозвать recovery path, fail closed до reconciliation.

## Последствия

Положительные:

- audit evidence проверяется независимо от Fortiq;
- локальная подмена и truncation становятся обнаружимыми;
- compliance reports строятся из evidence, а не из маркетинговых утверждений;
- privacy surface ограничивается schema allowlist.

Отрицательные:

- внешнее anchoring требует дополнительной инфраструктуры;
- подписание не доказывает полноту instrumentation;
- key rotation и clock anomalies усложняют verifier;
- WORM audit retention может конфликтовать с ошибочной избыточной записью персональных
  данных, поэтому minimisation обязательна до записи.

## Security gates

- выбрать signing suite и key provider;
- формально определить deterministic event encoding;
- выполнить external review verifier и key rotation;
- доказать отсутствие secrets/paths во всех event producers;
- протестировать omission windows и anchor outage;
- проверить mapping с профильным специалистом по каждой целевой юрисдикции.

## Источники

- [GDPR, Regulation (EU) 2016/679, Article 32](https://eur-lex.europa.eu/eli/reg/2016/679/oj)
- [NIS2, Directive (EU) 2022/2555, Article 21](https://eur-lex.europa.eu/eli/dir/2022/2555/oj)
- [ENISA NIS2 Technical Implementation Guidance](https://www.enisa.europa.eu/publications/nis2-technical-implementation-guidance)
- [RFC 9052: COSE](https://www.rfc-editor.org/rfc/rfc9052.html)
- [RFC 8949: CBOR](https://www.rfc-editor.org/rfc/rfc8949.html)

