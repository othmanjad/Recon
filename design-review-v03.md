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

## What changed in the artifacts

- **`cliq-recon-design.md` → v0.3**: §7 load path rewritten; §9 comparison types, pass mechanics, aggregate mode and candidate resolution rewritten; §11 control totals rewritten with scope and `RunAggregate`; §14 data model updated; new §9.7 reproducibility.
- **`recon-platform-ddl-v02-delta.sql`**: all schema changes above as a delta over v0.1 (new tables `cfg.Currency`, `cfg.ExclusionRule`, `ops.RunAggregate`; altered `MatchRule`, `MatchCondition`, `ControlTotalDefinition`, `ReconRun`, `ReconException`, `Dataset`, `ReconciliationDefinition`, `SqlSourceDefinition`, `FileFormatDefinition`; dropped filtered index; new roles).

## Still open (unchanged from v0.2)
Retention period · rounding mode · sales tax on fees · sessions per day · alert channels.
