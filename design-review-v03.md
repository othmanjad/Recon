# Reconciliation Platform — Design Review v0.3
**Reviewed:** cliq-recon-design.md v0.2 + recon-platform-ddl.sql v0.1
**Method:** five independent review lenses, findings merged and de-duplicated
**Date:** 2026-09-14

| Lens | Focus |
|------|-------|
| A — DBA / Performance | 2M rows/day, partitioning, indexing, load path |
| B — .NET Architecture | contracts, restartability, concurrency, reproducibility |
| C — Reconciliation & Finance | matching semantics, aggregates, summaries, money |
| D — Security | injection surfaces, permissions, secrets |
| E — Operations | reruns, alerts, reporting limits, maintenance |

Severity: **BLOCKER** = you will hit this in development or in the first week of production · **HIGH** = costly to fix after Phase 2 · **MEDIUM** = fix before the relevant phase · **LOW** = hygiene.

---

## Summary of blockers

| # | Finding | Lens | Status |
|---|---------|------|--------|
| 1 | Aggregate-vs-summary reconciliation (run totals ↔ summary) not supported by the model | C | **Fixed** — new match mode + run aggregates + control-total scope |
| 2 | "Load into heap, then index" is impossible on a shared slot table — the doc claims it | A | **Fixed** — doc corrected, real load path defined |
| 3 | Reproducibility claim is false: rules are edited in place, `DefinitionVersion` alone proves nothing | B | **Fixed** — definition snapshot JSON per run |
| 4 | `Normalized` comparison at join time kills every index | A | **Fixed** — normalization moves to parse time |
| 5 | Per-pass `UPDATE` of `MatchStatus` on staging = 2M-row writes × N passes + filtered-index churn | A | **Fixed** — passes write only to `MatchResult`; one final update |
| 6 | `FilterExpression` is free SQL text — injection surface in the exact place the design promised none | D | **Fixed** — structured JSON filter, same compiler as rules |

---

## Findings in full

### C1 · BLOCKER · Reconciling a total against a summary
**You raised this.** The model only supported row ↔ row matching within one run. Real needs: (a) the sum of one reconciliation's matched rows against a JOPACC summary line; (b) 30 daily runs' fee totals against a monthly IPS report; (c) one settlement batch line against many transactions.

**Fix applied — three parts:**

1. **`MatchMode = Row | Aggregate` on `cfg.MatchRule`.** In Aggregate mode the compiler groups the left side by user-chosen `GroupByFields`, sums the `Amount`-role field and counts rows, then joins the grouped result to the right side. One summary line ↔ many transactions is now an ordinary rule.
2. **`ops.RunAggregate`** — every run persists its totals (per dataset, per grouping key: count, sum in minor units, matched/unmatched split). Going back to "run 4471, total of matched inward" is a primary-key read, not a rescan of 2M staged rows. This is also what survives after staging is archived.
3. **`cfg.ControlTotalDefinition` gains `Scope = Run | BusinessDate | Period` and `SourceType = Staging | MatchResult | RunAggregate | Dataset`.** A period-scoped check sums `RunAggregate` across the current runs of a date range and compares to a summary dataset row. **Current-run rule:** period aggregates use only runs where `IsCurrent = 1` (superseded reruns are excluded automatically).

### A1 · BLOCKER · The load path
The doc said "load into the heap first, then build indexes." That is impossible: `stg.StagingTransaction` is one shared table with a permanent clustered index; you cannot drop indexes for one dataset's load.

**Fix applied — real load path:**
- `SqlBulkCopy` with `TableLock`, `BatchSize ≈ 100 000`, and an **`ORDER` hint matching the clustered key `(DatasetId, TxDate, StagingId)`**. Sorted input into a clustered index is minimally logged under `BULK_LOGGED`/`SIMPLE` recovery and avoids page splits.
- Keep **at most 3–4 nonclustered indexes** on staging. Every extra index is a 2M-row maintenance cost per load.
- **Phase 1 must include a performance spike** at 2M synthetic rows measuring load + one pass. If the budget (load ≤ 2 min) is missed, the escalation path is: daily partitions + a persisted composite partition column `(DatasetId, TxDate)` + load into an identically-structured heap + `SWITCH PARTITION`. This is the canonical high-volume pattern; it is deferred because it is significantly more complex and may not be needed.

### B1 · BLOCKER · Reproducibility
Runs recorded `DefinitionVersion`, but `cfg.MatchRule`, `MatchCondition`, `ClassificationRule` are edited in place. Version 3 today is not version 3 last month. An auditor asking "what rule matched this in June" gets a wrong answer with a confident label.

**Fix applied:** `ops.ReconRun.DefinitionSnapshotJson` — the full effective definition (rules, conditions, classifications, control totals, field registry of both datasets, fee schedules in effect) serialized **at run start**. The compiler reads from the snapshot, not from live config. Simpler and more robust than row-versioning six tables, and it makes sandbox runs trivially comparable.

### A2 · BLOCKER · `Normalized` comparison
`UPPER(TRIM(REPLACE(...)))` in a join predicate is non-sargable; the optimizer scans both sides.

**Fix applied:** normalization is a **parse-time transform**. A field flagged `NormalizeForMatch = 1` in the registry gets a companion slot populated at load with the normalized value. Rules compare the companion field with `Exact`. `Normalized` is removed as a runtime comparison type. Same treatment for any `Contains` / `StartsWith` use in a primary pass: the UI **warns** that these are non-indexable and should only appear in late, small passes.

### A3 · BLOCKER · Match status writes
Each pass updated `MatchStatus` on staging. At 2M rows × 4 passes that is 8M row writes plus churn on the `Unmatched` filtered index, and it makes a failed pass leave staging half-updated.

**Fix applied:** passes write **only** to `ops.MatchResult` (per run). "Still unmatched" for pass N = `NOT EXISTS (SELECT 1 FROM MatchResult WHERE RunId = @run AND LeftStagingId = s.StagingId)`. `MatchStatus` on staging is set **once**, in a single set-based update at the end of the run. A failed pass is retried by deleting its `MatchResult` rows — staging is never touched mid-run. The filtered `Unmatched` index is dropped.

### D1 · BLOCKER · Free-text filter
`cfg.SqlSourceDefinition.FilterExpression` was free SQL. Anyone with `Configure` access could inject.

**Fix applied:** `FilterJson` — the same structured condition tree used by `MatchRule.LeftFilter`, compiled by the same single SQL-emitting class, fields validated against the registry (or, for SQL sources, against `INFORMATION_SCHEMA.COLUMNS` of the declared object). `ObjectName` is validated against `sys.objects` at save time and must be a view or procedure the connection account can already read.

---

### HIGH

**B2 · Concurrency.** Nothing prevents the scheduler and a manual trigger running the same definition simultaneously; both would stage the same date. *Fix:* `sp_getapplock` keyed on `DefinitionId + BusinessDate` at run start; a second attempt is recorded as `Rejected: already running`.

**B3 · Restartability.** A run that dies in pass 3 has no defined recovery. *Fix:* each `ReconRunStep` is a checkpoint; `Resume` re-executes from the first non-completed step; pass results are deletable by `(RunId, MatchRuleId)`. Acquire and Parse steps are idempotent by file hash.

**C2 · Late-arrival auto-close depends on old staging rows.** `ReconException.StagingId` points into staging, which has shorter retention than exceptions. *Fix:* `ReconException.KeyValuesJson` — a snapshot of the matchable field values at exception creation. The auto-close pass joins today's unmatched rows to open exceptions **by these snapshotted keys**, independent of staging retention.

**C3 · Intra-dataset duplicates.** A file containing the same reference twice will match one and orphan the other, or produce Ambiguous on the other side, with no signal that the *source* was the problem. *Fix:* `cfg.Dataset.DuplicateKeyFields`; a pre-match step marks second-and-later occurrences `MatchStatus = 'Duplicate'` and raises `DUPLICATE_IN_SOURCE`. They never enter matching.

**C4 · Currency minor units.** `AmountFils` hard-codes 3 decimals. JOD has 3; USD/EUR have 2; some partners settle in USD. *Fix:* `cfg.Currency (Code, MinorUnits)`; the integer amount slot is `AmountMinor`; the parser scales by the dataset's currency. All doc text updated from "fils" to "minor units" where it is generic.

**C5 · OM snapshot window.** Which date range does the `SqlProvider` pull for a business date? Late arrivals mean it is not "= BusinessDate". *Fix:* `cfg.ReconciliationDefinition.MatchingWindowDaysBefore/After`; the provider parameters `@FromDate/@ToDate` derive from them. Default 1/1.

**E1 · Excel row limit.** A worksheet holds 1 048 576 rows. A full day's matched list (2M) does not fit. Developers discover this in Phase 4 at 3 a.m. *Fix:* report engine splits sheets at 1M rows automatically; the "all transactions" scope is offered as **CSV** by default with Excel for exception-level scopes. Use a **streaming** writer (OpenXML SAX / MiniExcel) — ClosedXML/EPPlus will exhaust memory at this size.

**A4 · Composite-key candidate explosion.** Pass 4 (`amount + date-within + account`) on 2M rows can produce many-to-many candidate sets. *Fix:* the compiler always materializes candidates into a temp table with `COUNT(*) OVER (PARTITION BY LeftId)` and `(PARTITION BY RightId)`; any side with count > 1 resolves per `OnMultipleMatch`. Never `UPDATE ... FROM` a join directly.

**A5 · Read consistency during runs.** The portal will query staging and results while a run is inserting. *Fix:* enable `READ_COMMITTED_SNAPSHOT` on the database. Non-negotiable at this volume; without it the portal blocks behind the loader.

**E2 · Partition maintenance.** If no future partition exists at month rollover, everything lands in the last range and the sliding window breaks. *Fix:* a scheduled job that keeps ≥ 3 future monthly partitions ahead, and an alert if fewer than 2 exist. Retention is expressed as "months online"; older partitions are switched out to an archive table on cheaper storage.

**E3 · Staging vs. results retention differ.** Staging at 2M/day × long retention is the largest cost in the system; results and aggregates are small. *Fix:* two retention parameters: `StagingMonthsOnline` (default 3) and `ResultsMonthsOnline` (default retention period). `RunAggregate` and `KeyValuesJson` make this safe.

**B4 · Rerun types.** A rerun re-acquires and re-stages everything, doubling storage even when only rules changed. *Fix:* `RunType` gains `Rematch` with `SourceRunId` — reuses the source run's staged rows, writes new `MatchResult`/exceptions under the new `RunId`. Supersession still applies.

---

### MEDIUM

**B5 · Filter schema undefined.** `LeftFilter`/`RightFilter`/`ConditionJson` are "structured JSON" with no schema. *Fix:* one condition-tree schema (`{ "op": "and"|"or", "items": [ { "field": "<FieldCode>", "cmp": "eq|ne|gt|lt|in|isnull|isnotnull", "value": ... } ] }`) used by rules, classifications, source filters, and report filters. Validated on save.

**E4 · Parse error flood.** A wrong-format file yields 2M `ParseError` rows with `RawLine`. *Fix:* `cfg.FileFormatDefinition.MaxParseErrors` (default 1000); the parse step aborts the file beyond it and marks it `Failed`.

**C6 · Exclusion before matching.** Rows that must not match (e.g. `Status = RJCT`) need to leave the working set before pass 1 and be reported separately. *Fix:* `cfg.ExclusionRule` per dataset using the condition-tree; excluded rows get `MatchStatus = 'Excluded'` and never generate exceptions.

**C7 · Reversal chains.** `OriginalReference` linkage (pacs.004 → pacs.008) is described but not modelled. *Fix:* registry role `OriginalReference`; the compiler supports a rule condition `LeftField(OriginalReference) ↔ RightField(Reference)`; classification can then express "reversed with record" precisely.

**B6 · Universal field validation.** "Cannot activate without Reference/Amount/Currency/Date/Direction" was stated but has no enforcement point. *Fix:* an `Activate` service that validates roles, slot type compatibility, required mappings, and at least one rule with an indexed field — and writes the DDL for indexes and the readable view.

**A6 · Index generation rules.** "Generated from the active rule set" needs a rule. *Fix:* one nonclustered index per (dataset, field) where the field appears in a pass-1 or pass-2 condition, key `(DatasetId, <slot>, TxDate)`, include `(StagingId)`. Cap at 4 per dataset; the UI shows which fields are indexed.

**E5 · Sandbox cleanup.** Sandbox runs write to production staging. *Fix:* `RunType = 'Sandbox'` rows are purged by a nightly job after 7 days; sandbox runs are excluded from `IsCurrent` and from aggregates.

**D2 · Config-schema permissions.** The application account should not be `db_owner`. *Fix:* separate roles — `recon_loader` (INSERT/SELECT on `stg`, EXEC on load procs), `recon_app` (CRUD on `cfg`/`ops`, SELECT on `stg`), `recon_reader` (SELECT only). `CREATE VIEW` on schema `stg` only for the activation service.

**D3 · Audit completeness.** `AuditLog` captures config changes but not *reads* of sensitive exports. *Fix:* log `Export` actions (who downloaded which file/report, when). Regulators ask.

**C8 · Fee rounding default.** `HalfUp` is a guess. Flagged in v0.2; still open. *Action:* obtain JoPACC's stated rounding rule before Phase 5 — do not start fee development without it.

---

### LOW

**A7** · `ReconException.AgeDays` computed with `SYSDATETIME()` cannot be indexed; fine, but compute it in the query layer instead. **A8** · Add `DATA_COMPRESSION = PAGE` on staging partitions older than the current month (≈ 60% saving on text slots). **E6** · Alert on match-distribution drift (pass-4 share > threshold) — the design promised the signal; make it an `AlertPolicy.EventType`. **B7** · `TransformChain` as pipe-delimited string is fragile; make it a JSON array. **D4** · `AcquisitionDefinition.RequestTemplate` may embed headers; ensure secrets are only via `CredentialRef`. **E7** · Time-zone: both sides local today; if a partner sends UTC, `Dataset.TimeZone` must drive conversion at parse time — implement it in Phase 1 even though the first partner doesn't need it, because retrofitting date logic on staged data is painful.

---

## Second pass — v1.0 (schema read as one artifact)

**Reviewed:** `recon-platform-ddl.sql` v0.1 **together with** `recon-platform-ddl-v02-delta.sql`.
**Why a second pass:** the v0.3 review checked the design document and the base
schema. It did not check the base and the delta *as the single artifact a
developer would actually deploy*. Reading them together surfaced five gaps that
neither file shows on its own, plus one defect introduced by the fix for C4.

Every statement in the delta applied cleanly to the base — the `ALTER`s,
`DROP CONSTRAINT`s and `DROP INDEX`es all found their targets. The problems were
semantic, not syntactic.

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | `Rematch` silently overwrites the run it reuses; and nothing says which run's staged rows to read | **BLOCKER** | Fixed |
| 2 | `NormalizedSlot` sits outside slot-uniqueness — a companion slot can overwrite a mapped field | **BLOCKER** | Fixed |
| 3 | `*Fils` naming survived the multi-currency fix; in a USD reconciliation the column names lie | HIGH | Fixed |
| 4 | Retention parameters exist only as SQL comments — no table holds them | HIGH | Fixed |
| 5 | No role can `SELECT` on schema `aud`, so the promised audit viewer cannot read the audit log | HIGH | Fixed |

### V1 · BLOCKER · `Rematch` contradicts "re-runs never overwrite"

Three decisions, each correct alone, collide:

- **B4** — a `Rematch` reuses the source run's staged rows.
- **A3** — `MatchStatus` on staging is written **once**, at the end of the run.
- **§14** — "re-runs never overwrite. The prior run is marked `IsCurrent = 0` and remains intact and queryable."

A `Rematch` therefore writes its own `MatchStatus`, `MatchedWithId` and
`ExceptionCode` onto rows owned by the source run — destroying that run's
row-level results while the design claims they survive. Worse, nothing states
which run's staging a query should read: `stg.StagingTransaction.RunId` is the
*loading* run, so every query filtering by the executing `RunId` returns **zero
rows** on a Rematch.

**Fix applied — three parts:**

1. `stg.StagingTransaction.RunId` is renamed **`LoadRunId`**. The name now says
   what it means: the run that staged the row.
2. `ops.ReconRun.StagingRunId` is a persisted computed column,
   `ISNULL(SourceRunId, RunId)`. The rule for which staging to read is stated
   once, in the schema, and every consumer joins on it.
3. `stg.StagingTransaction.ResultRunId` stamps which run produced the cached
   status columns. Those four columns are now documented for what A3 made them:
   a **cache** of the most recent run over these rows. The authoritative
   per-run record is `ops.MatchResult` (per run, never rewritten) plus
   `ops.RunAggregate` — so "re-runs never overwrite" is true at the level that
   matters, and is now true as written.

### V2 · BLOCKER · The normalized companion slot can overwrite a mapped field

`UQ_DatasetField_Slot UNIQUE (DatasetId, StorageSlot)` guards `StorageSlot`
only. The delta added `NormalizedSlot` with no uniqueness, no foreign key to the
slot catalogue, and no check that the target slot is a string. Nothing prevented
field A's `NormalizedSlot = 'Text21'` while field B's `StorageSlot = 'Text21'` —
the parse-time normalizer then overwrites a mapped financial field, silently.

This also hid a capacity error: the real text capacity was never 30, but 30 minus
the number of normalized fields.

**Fix applied:** `cfg.StorageSlotCatalogue.IsNormalizedOnly` splits the pool.
`Text1..Text20` are reservable; `Text21..Text30` are companions and are never
offered as a `StorageSlot`. Both columns are now foreign keys to the catalogue, a
filtered unique index `UX_DatasetField_NormSlot` stops two fields sharing a
companion, and a trigger enforces the two rules a `CHECK` cannot express — pool
membership on each side, and slot type matching the field's declared type.

### V3 · HIGH · `*Fils` naming outlived the currency fix

C4 made minor units configurable and the delta introduced `AmountMinorSum`, but
twenty columns in the base still read `ToleranceFils`, `AmountFils`,
`RevenueFils`, `AmountFromFils` and so on. For a partner settling in USD the
column names are actively wrong, and the design document had already renamed
`ToleranceFils` to `ToleranceMinor` — the DDL had simply not followed.

**Fix applied:** every monetary column is `*Minor`. The window for this closes
with the first production row, and the database is empty today.

### V4 · HIGH · Retention parameters had nowhere to live

`StagingMonthsOnline` (default 3), `ResultsMonthsOnline`, the 7-day sandbox
purge and the partition-lookahead count were all defined in E2/E3/E5 — and all
existed only inside SQL comment blocks. There was no settings table anywhere in
the schema, so the largest cost driver in the system had no configurable home.

**Fix applied:** `cfg.PlatformSetting`, seeded with the ten operational
parameters the design relies on. `ResultsMonthsOnline` is seeded at 84 months
and explicitly labelled a placeholder, not a decision.

### V5 · HIGH · The audit viewer could not read the audit log

§13 promises an audit log viewer and D3 requires export actions to be logged
"because regulators ask". The delta's grants gave `recon_app` a single
permission on schema `aud`: `INSERT`. No role was granted `SELECT`. The feature
could not have worked.

**Fix applied:** `SELECT` on `aud` granted to `recon_app` and `recon_reader`;
`UPDATE` and `DELETE` on `aud` remain granted to nobody, which is the property
worth protecting. The audit `Action` list also gains `'Export'`, which D3 asked
for and the delta had not added.

### Smaller items fixed in the same pass

**`RunType` had no `CHECK`** — every other enumeration column in the schema has
one, and `UX_ReconRun_Current` filters on `RunType <> 'Sandbox'`, so a typo
would have pulled a sandbox run into the uniqueness scope and into period
aggregates. Constrained now, along with `StepName`, `FieldRole`, `EventType`,
`ErrorType`, `Severity` and `SqlSourceParameter.DataType`.

**A5 was commented out.** `SET READ_COMMITTED_SNAPSHOT ON` — described by this
review as "non-negotiable at this volume" — was a `--` comment in the delta. A
commented-out non-negotiable is one that gets skipped. It is now a real
statement in `db/04-database-options.sql`.

**`recon_activator` was over-granted.** `GRANT ALTER ON SCHEMA::stg` also permits
`ALTER TABLE` on a table with hundreds of millions of rows, well beyond the
"views and indexes only" intent. Narrowed to an object-level grant on
`stg.StagingTransaction`.

**Condition-tree columns disagreed on width.** B5 mandates one schema for all
filters, but `MatchRule.LeftFilter`/`RightFilter` and
`ControlTotalDefinition.SourceAExpression`/`B` were `NVARCHAR(1000)` while every
other column holding the same JSON was `NVARCHAR(MAX)`. One schema, one
compiler, and one of them truncating. All are `NVARCHAR(MAX)` now, each with an
`ISJSON` check.

**A7 and B7 were never applied.** `ReconException.AgeDays` was still a
non-deterministic computed column — unindexable and evaluated on every row read.
Dropped; aging is computed in the query layer, filtering on the indexed
`BusinessDate`. `TransformChain` is now `TransformChainJson`, a validated JSON
array instead of a pipe-delimited string.

**`RunAggregate` was inconsistent with the rest of the schema.** `RowCount`
collides with the reserved `ROWCOUNT` keyword (now `RowCnt`), and
`DefinitionId`/`CurrencyCode` carried no foreign keys in an otherwise
consistently referenced schema.

### One defect introduced during this pass, and caught

The first draft of the V2 fix used `CONSTRAINT UQ_DatasetField_NormSlot UNIQUE
(DatasetId, NormalizedSlot)`. SQL Server treats `NULL`s as equal in a unique
constraint, so that would have allowed exactly **one** field per dataset without
a companion slot — and most fields have none. Replaced with a filtered unique
index. Recorded here because it is the same class of error as the findings above:
a constraint that reads correctly and does something else.

---

## Third pass — executed against a live instance

**Method:** the schema was built on SQL Server 2022 and exercised, rather than
read. `./db/tests/run.sh` reproduces it: 28 metadata checks and 55 behavioural
tests, the latter writing bad data and requiring the database to refuse it.

The v1.0 pass had concluded with the schema *parse-checked but never executed*,
and said so. Executing it found three defects that no amount of reading would
have surfaced — two of them constraints that were present, correctly named, and
did nothing at all.

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | Case-insensitive collation let a wrong-case enum value through every `CHECK` | **BLOCKER** | Fixed |
| 2 | `CK_ControlTotal_Period` never rejected anything, because of three-valued logic | HIGH | Fixed |
| 3 | The script cannot be deployed by sqlcmd or CI: filtered indexes need `QUOTED_IDENTIFIER ON` | HIGH | Fixed |

### X1 · BLOCKER · A case-insensitive `CHECK` is not a vocabulary

`CK_ReconRun_Type` lists `'Sandbox'`. SQL Server's default collation is
case-insensitive, so `RunType = 'sandbox'` satisfies it — and is stored exactly
as typed.

Inside the database this is harmless: `UX_ReconRun_Current`, whose filter reads
`RunType <> 'Sandbox'`, also compares case-insensitively and excludes the row
correctly. The damage is at the boundary. **C# string comparison is
case-sensitive**, so `run.RunType != "Sandbox"` is *true* for `'sandbox'`: the
database excludes the run from its uniqueness scope while the application
includes it in period aggregates. That is precisely the divergence finding 8
added the constraint to prevent, and the constraint permitted it.

**Fix applied:** all 43 enumeration whitelists collate
`Latin1_General_CS_AS`, so the stored value is always canonical and a wrong-case
value is refused at the door. Four tests cover it across `RunType`,
`MatchStatus`, `Status` and `ProviderType`.

This generalises past this schema: **any enum column an application compares as
a string in code needs a case-sensitive constraint**, or the database and the
code disagree about what the value is.

### X2 · HIGH · A guard that rejected nothing

```sql
CONSTRAINT CK_ControlTotal_Period CHECK (Scope <> 'Period' OR PeriodDays > 0)
```

For a `Period`-scoped row with `PeriodDays` NULL: the left side is `FALSE`, the
right is `UNKNOWN`, and `FALSE OR UNKNOWN` is `UNKNOWN`. A `CHECK` constraint
rejects only on `FALSE` — so the row was accepted, and a period-scoped control
total could be configured with no window at all.

**Fix applied:** `OR (PeriodDays IS NOT NULL AND PeriodDays > 0)`. Every other
`OR`-guarded constraint in the schema was then audited for the same trap; all
24 state their NULL explicitly, and the four that deliberately permit NULL
(`FeeTier.AmountToMinor` for an open-ended top tier, the two `EffectiveTo`
guards, `ReconRun` self-reference) do so intentionally.

The lesson is worth stating because it recurs: in a `CHECK`, an `OR` whose
right side can evaluate to NULL silently disables the constraint.

### X3 · HIGH · The script could not be deployed by its own tooling

The very first execution failed on the first filtered index:

> Msg 1934: CREATE INDEX failed because the following SET options have incorrect
> settings: 'QUOTED_IDENTIFIER'.

Filtered indexes and indexes on computed columns require `QUOTED_IDENTIFIER ON`
and `ANSI_NULLS ON`. SSMS sets both; **sqlcmd sets `QUOTED_IDENTIFIER OFF`**. So
the schema would have deployed by hand and failed in any CI pipeline or
automated release — the worst possible place to discover it.

**Fix applied:** the script sets both options itself, before the first
`CREATE SCHEMA`, and is now independent of whichever client runs it. Verified by
running it with no client flags at all.

### What execution confirmed that review could not

- The **slot-pool trigger** actually refuses a companion slot used as primary
  storage, a type mismatch, and a shared companion — the silent
  data-corruption path from finding 2, closed and proven closed.
- The **filtered unique index** on `NormalizedSlot` permits many NULLs, which is
  the defect the v1.0 pass caught in its own first draft. Now a standing
  regression test.
- **`StagingRunId`** resolves to the source run on a `Rematch` and to the run's
  own id otherwise — read back from the persisted computed column, not inferred.
- The **audit grants** work as intended, tested as real role members via
  `EXECUTE AS USER`: `recon_reader` can read the log, neither it nor
  `recon_app` can rewrite or delete it, and `recon_loader` cannot touch
  configuration. Finding 5 is closed against live permissions rather than
  against `sys.database_permissions`.
- **Partition routing** places three business dates in three distinct monthly
  partitions.
- A **6.2 KB condition tree** stores intact, where the old `NVARCHAR(1000)`
  columns would have truncated it without complaint.

---

## What changed in the artifacts

**v0.3**

- **`cliq-recon-design.md` → v0.3**: §7 load path rewritten; §9 comparison types, pass mechanics, aggregate mode and candidate resolution rewritten; §11 control totals rewritten with scope and `RunAggregate`; §14 data model updated; new §9.7 reproducibility.
- **`recon-platform-ddl-v02-delta.sql`**: all schema changes above as a delta over v0.1.

**v1.0**

- **`cliq-recon-design.md` → v1.0**: §15 question 6 closed (it contradicted the confirmed decision in §7); monetary wording moved from "fils" to "minor units" throughout; new §13.1 recording the front-end stack decision.
- **`db/01-schema.sql`** — the base and the delta merged into one executable script with the findings above applied. 38 tables. It supersedes both `recon-platform-ddl.sql` and `recon-platform-ddl-v02-delta.sql`, which move to `db/superseded/`; the delta form served its purpose (every change visible for review) and now only creates two sources of truth.
- **`db/02-seed.sql`** — currencies, the split slot catalogue, and `cfg.PlatformSetting`.
- **`db/03-roles.sql`** — the four least-privilege roles, with the `aud` read path fixed.
- **`db/04-database-options.sql`** — `READ_COMMITTED_SNAPSHOT`, as a statement rather than a comment.
- **`db/tests/`** — `01-verify-schema.sql` (28 metadata checks), `02-behaviour.sql` (55 tests that require the database to refuse bad data), and `run.sh`, which builds the schema on a throwaway SQL Server container and runs both. Docker is the only prerequisite.
- **`ui/`** — the portal prototype: run dashboard, exception workspace, dataset/field registry, rule builder, counterparties, settings. HTML + Bootstrap 5 + jQuery, libraries vendored, no build step.

## Still open

Retention period · rounding mode · sales tax on fees · sessions per day · alert
channels · whether the primary reference is globally unique or reused across days.

All six are **business answers, not design work.** Each has a place to live in
the schema already (`cfg.PlatformSetting`, `FeeSchedule.RoundingMode`,
`cfg.AlertPolicy`, `ScheduleDefinition`), so none blocks the Phase 1 build.
Two have a deadline: the rounding mode must be confirmed **before Phase 5**, or
fee netting will never tie out; and whether the primary reference is reused
across days decides whether pass 1 joins on reference alone or reference + date,
which is the shape of the first index built.
