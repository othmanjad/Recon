# Database

The reconciliation platform schema for SQL Server.

**None of these scripts creates the database**, and none of them contains a
`USE`: they build the schema in whichever database they are connected to. So
either let the portal do it — `/setup` runs `CREATE DATABASE` and then these
four files, recording each with the hash of its text — or create it yourself
first and point the client at it:

```sql
CREATE DATABASE ReconPlatform;   -- any name; nothing depends on this one
```

```bash
for f in db/01-schema.sql db/02-seed.sql db/03-roles.sql db/04-database-options.sql; do
    sqlcmd -S localhost -U sa -P '...' -d ReconPlatform -b -i "$f" || break
done
```

`-d` is the part that matters: without it the scripts build the schema in
`master`. `-b` stops on the first error instead of carrying on into the next
script.

Run the scripts in order:

| # | Script | What it does |
|---|--------|--------------|
| 1 | `01-schema.sql` | Schemas, partitioning, and all 38 tables |
| 2 | `02-seed.sql` | Currencies, the slot catalogue, platform settings |
| 3 | `03-roles.sql` | The four least-privilege database roles |
| 4 | `04-database-options.sql` | `READ_COMMITTED_SNAPSHOT`, plus the maintenance-job outlines |

Requires SQL Server 2016 or later (`ISJSON`, `STRING_SPLIT`-era T-SQL).
`04-database-options.sql` applies to whichever database it is run in
(`QUOTENAME(DB_NAME())`) and takes an exclusive lock with
`ROLLBACK IMMEDIATE`, so run it in a maintenance window rather than on a live
loader.

Two scripts do the whole thing for you, if Docker is all you have:
`./db/tests/run.sh` builds the schema on a throwaway SQL Server and runs both
test suites against it; `./demo/run-demo.sh` goes further and reconciles a
session.

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

## Verification

```bash
./db/tests/run.sh
```

Builds the schema on a throwaway SQL Server 2022 container and runs 28
metadata checks plus 55 behavioural tests. See `db/tests/README.md` —
including the two bugs the behavioural suite caught that no amount of
reading the script would have found.

## Open items

Six questions remain, all business answers rather than design work — see
`design-review-v03.md`, "Still open". Two have deadlines:
`FeeSchedule.RoundingMode` must be confirmed before Phase 5 or fee netting will
never tie out, and whether the primary reference is reused across days decides
the shape of the first index built.
