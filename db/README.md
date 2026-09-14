# Database

The reconciliation platform schema for SQL Server. Run the scripts in order:

| # | Script | What it does |
|---|--------|--------------|
| 1 | `01-schema.sql` | Schemas, partitioning, and all 38 tables |
| 2 | `02-seed.sql` | Currencies, the slot catalogue, platform settings |
| 3 | `03-roles.sql` | The four least-privilege database roles |
| 4 | `04-database-options.sql` | `READ_COMMITTED_SNAPSHOT`, plus the maintenance-job outlines |

Requires SQL Server 2016 or later (`ISJSON`, `STRING_SPLIT`-era T-SQL).
`04-database-options.sql` names the database `[ReconPlatform]` and takes an
exclusive lock, so run it in a maintenance window.

## The five things worth knowing before reading the schema

**1. Amounts are integers, in minor units.** `DECIMAL(18,3)` is stored for
display; every comparison, join and sum uses the `BIGINT` minor-unit column.
The scale comes from `cfg.Currency.MinorUnits` — JOD has 3, USD and EUR have 2.
There is no `float` anywhere, at any stage. Decimal comparison across two
systems produces phantom differences; integer comparison cannot.

**2. Field names live in `cfg.DatasetField`, not in the code.** The staging
table has generic typed slots (`Text1`, `Num1`, `Date1`). Nothing outside the
provider layer may reference a slot directly — the field registry is the only
path to the data, which is what preserves the option to change storage models
later. `IsMatchable` on that table is the security boundary: rule conditions
resolve field names against the registry and never from user text.

**3. `ops.MatchResult` is the record of truth for matching.** Passes write only
there. The status columns on `stg.StagingTransaction` are a cache written once
at the end of a run, stamped with `ResultRunId` to say which run produced them.

**4. Read staging through `ops.ReconRun.StagingRunId`, never `RunId`.** A
`Rematch` reuses another run's staged rows, so the run executing and the run
that loaded the data are different. `StagingRunId` is
`ISNULL(SourceRunId, RunId)` and exists so that rule is stated once.

**5. Text slots come in two pools.** `Text1..Text20` are reservable as a field's
own storage; `Text21..Text30` are companions holding normalized values written
at parse time. `cfg.StorageSlotCatalogue.IsNormalizedOnly` enforces the split —
without it, a normalized companion can overwrite a mapped field.

## What is deliberately absent

- **No `float` or `double`.** See above.
- **No `Normalized` comparison type.** Normalizing inside a join predicate is
  non-sargable and defeats every index. Normalization happens at parse time into
  a companion slot, and the rule compares that slot with `Exact`.
- **No free SQL text in any configuration column.** Every filter is a structured
  condition tree, validated on save and compiled by one class.
- **No blobs.** Original files live on the filesystem or object storage with a
  SHA-256 in `ops.SourceFile`. At 2M rows/day the blob path leads to an
  unmanageable database.
- **No `UPDATE` or `DELETE` grant on schema `aud`, to any role.**

## Open items

Six questions remain, all business answers rather than design work — see
`design-review-v03.md`, "Still open". Two have deadlines:
`FeeSchedule.RoundingMode` must be confirmed before Phase 5 or fee netting will
never tie out, and whether the primary reference is reused across days decides
the shape of the first index built.
