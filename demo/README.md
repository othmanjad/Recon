# Demo

The whole platform, end to end, from nothing:

```bash
./demo/run-demo.sh
```

Docker is the only prerequisite — SQL Server and the .NET SDK both run in
containers, and nothing is installed on the host. It takes a couple of minutes
the first time (pulling two images) and about twenty seconds after that.

What it does, in order:

1. Starts SQL Server 2022 in a container
2. Builds the schema from `db/`
3. Creates a CliQ ↔ OM configuration (`01-demo-config.sql`) — the same rows
   Operations would create from the portal
4. Validates that the definition can be activated, and says why not if it
   cannot
5. Acquires, parses, stages, matches, classifies and proves the totals for one
   session from the two CSVs in `files/`
6. Prints what the run recorded, and leaves the database up so you can look

## What you should see, and why each line is there

```
  Left  CLIQ_SESSION: staged 19 rows in 0.1s, 1 rejected
         line 18: 'not-a-number' is not a valid Integer
```
One row is rejected and the rest of the file still loads. The rejection is in
`stg.ParseError` with its line, field, reason and raw line.

```
match distribution
  1  P1_REF                     13   86.67%
  2  P2_REF_NORM                 1    6.67%
  3  P3_SEC_REF                  1    6.67%
  4  P4_COMPOSITE                0    0.00%
```
Pass 1 takes the clean majority. Pass 2 catches the one reference the two
systems spell differently — via the companion slot filled at parse time, so it
is still an indexed `Exact` match. Pass 3 catches the one that only agrees on
the secondary reference. **That distribution is the data-quality signal:** a
rising share in the last pass means the clean reference match is degrading
upstream, and above 15% it is the `MatchDistributionDrift` alert.

```
control totals
  ok  MATCHED_TOTAL     5,682.708 vs  5,682.708  diff 0.000
  ok  MATCHED_COUNT            15 vs         15  diff 0
  OUT INWARD_TOTAL      1,928.458 vs  1,982.708  diff -54.250  (reported, does not fail the run)
```
The matched value agrees to the fils, which is the proof that matters. The
inward check is deliberately out: the two sides genuinely hold different
inward totals, because one OM row has no CliQ counterpart, one CliQ row was
excluded as rejected and one was a source duplicate. It is configured with
`FailRunOnMismatch = 0` for exactly that reason — a directional total that
moves with a legitimate one-sided arrival is a signal, not a failure.

Change `MATCHED_TOTAL`'s tolerance to 0 and alter one amount in a CSV, and the
run comes back **Failed**: a run with fully matched rows and a non-zero net
difference is a failed run.

```
ExceptionCode     Side   Items  ValueJod
FAILED_INWARD     Left       1   265.000
FAILED_OUTWARD    Left       1   999.000
MISSING_IN_CLIQ   Right      1   720.000
```

```
Dataset       MatchStatus  Rows
CLIQ_SESSION  Duplicate      1     <- the declared key twice in one file
CLIQ_SESSION  Excluded       1     <- status RJCT, removed before pass 1
CLIQ_SESSION  Matched       15
CLIQ_SESSION  Unmatched      2     <- the two classified above
OM_TXN        Matched       15
OM_TXN        Unmatched      1
```
Neither the excluded nor the duplicate row raises an exception. A rejected
transaction is not a break, and a source duplicate is a data-quality report.

## Things worth trying next

```bash
CONN="Server=127.0.0.1,1433;Database=ReconDemo;User Id=sa;Password=Recon#Verify2026x;TrustServerCertificate=True;Encrypt=False"

# The SQL a pass actually ran — persisted with the step, so "what matched this
# row in June" is answerable from the run itself rather than from today's config
docker exec -it reconsql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
  -P 'Recon#Verify2026x' -C -d ReconDemo -y0 \
  -Q "SELECT GeneratedSql FROM ops.ReconRunStep WHERE StepName = 'Match' AND MatchRuleId = 1;"

# Run it again: the unique index refuses a second current run for the same
# date and session, so the first is superseded rather than overwritten
./build.sh run --project src/Recon.Cli -- run --conn "$CONN" --definition 1 \
    --business-date 2026-09-13 --session S1 --type Rerun --by you \
    --left-file demo/files/CLIQ_SESSION_20260913_S1.csv \
    --right-file demo/files/OM_TXN_20260913.csv

# A Rematch: new rules, the SAME staged rows, and the first run's results
# stay intact
./build.sh run --project src/Recon.Cli -- run --conn "$CONN" --definition 1 \
    --business-date 2026-09-13 --session S1 --type Rematch --source-run 1 --by you

# Break the configuration and watch the activation gate refuse it
docker exec -it reconsql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
  -P 'Recon#Verify2026x' -C -d ReconDemo \
  -Q "UPDATE cfg.DatasetField SET FieldRole = NULL WHERE FieldCode = 'AMOUNT';"
./build.sh run --project src/Recon.Cli -- validate --conn "$CONN" --definition 1
```

## Files

| | |
|---|---|
| `run-demo.sh` | The script above |
| `01-demo-config.sql` | The whole configuration, commented — read this to see what "onboarding a counterparty" actually consists of |
| `files/` | The two session CSVs, and a table of what every row is there to exercise |
