# Schema tests

```bash
./db/tests/run.sh              # build the schema on a throwaway SQL Server and test it
./db/tests/run.sh --teardown   # ...and remove the container afterwards
```

Docker is the only requirement. The runner starts
`mcr.microsoft.com/mssql/server:2022-latest`, drops and recreates
`ReconPlatform`, runs the four schema scripts, then both suites. It exits
non-zero on any failure, so it drops straight into CI.

| File | What it proves |
|------|----------------|
| `01-verify-schema.sql` | The objects **exist** and are shaped as the design requires — 28 metadata checks |
| `02-behaviour.sql` | The constraints **bite** — 55 tests that try to write bad data and require it to be refused, plus the valid cases that must be accepted |

## Why the second file exists

Metadata checks confirm a constraint was created. They cannot tell you it
does anything. Both bugs found the first time this ran were of exactly that
kind — the constraint was present, correctly named, and did not work:

- **`RunType = 'sandbox'` was accepted.** SQL Server's default collation is
  case-insensitive, so a `CHECK` listing `'Sandbox'` admits `'sandbox'` and
  stores it as typed. The database then treats the two as equal while C#
  string comparison does not, so an application filtering
  `RunType != "Sandbox"` would pull a sandbox run into production
  aggregates — the exact bug the constraint was added to prevent. The enum
  whitelists now collate `Latin1_General_CS_AS`.

- **A `Period`-scoped control total with no `PeriodDays` was accepted.** The
  guard read `Scope <> 'Period' OR PeriodDays > 0`. With `PeriodDays` NULL
  that is `FALSE OR UNKNOWN` = `UNKNOWN`, and a `CHECK` rejects only on
  `FALSE`. Three-valued logic needs the NULL stated: `OR (PeriodDays IS NOT
  NULL AND PeriodDays > 0)`.

Every other `OR`-guarded constraint in the schema was audited for the same
trap and states its NULL explicitly.

## Conventions

- Each test runs in its own transaction and is rolled back, so the database
  is unchanged. The result is recorded **after** the rollback: a trigger
  that `THROW`s leaves the transaction doomed, and nothing can be written
  to it until it is rolled back.
- Fixture rows are prefixed `ZZ_` and removed at both ends of the run, so
  the suite is re-runnable after an interrupted attempt.
- Permission tests use `EXECUTE AS USER` against real role members rather
  than reading `sys.database_permissions` — a grant that looks right and a
  grant that works are different claims.
- Test names say which review finding they defend. A failure names the
  regression.
