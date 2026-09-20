# Engine and portal

.NET 8. Five projects, and the dependency direction is the point:

```
Recon.Domain    the field registry, the condition tree, money.  No I/O, no SQL.
   ▲
Recon.Data      repositories: configuration, runs, checkpoints, the snapshot.
   ▲
Recon.Engine    providers, parsing, staging, reporting, fees, scheduling,
   ▲            and THE SQL COMPILER.
   ├── Recon.Cli    the batch entry point.  Thin by design.
   └── Recon.Web    the portal: MVC, Razor, Bootstrap and jQuery.
                    See Recon.Web/README.md.
```

Nothing in `Recon.Web` emits SQL of its own beyond its own reads in
`PortalQueries`, and nothing there resolves a field name: the portal hands
field codes to the engine's compiler, which resolves them against the registry
exactly as the CLI's runs do.

## The one thing to read first

`Recon.Engine/Sql/SqlQueryBuilder.cs` is **the only class in the solution that
emits SQL**, and everything else about the design follows from keeping it that
way. Two invariants hold in every method:

1. **A column name reaches the SQL text only through the field registry.** A
   field code is resolved against `cfg.DatasetField`, checked for
   `IsMatchable`, and then checked against the slot whitelist. An unknown
   field, a withheld field, or a registry row naming something that is not a
   column of `stg.StagingTransaction` throws before any text is produced.
2. **A value never reaches the SQL text at all.** Values go to
   `SqlParameterBag` and appear as `@p0`, `@p1`. There is no code path in that
   file that interpolates a user value.

That is what makes a rule builder driven by Operations safe, and it is why
there is no ORM here: Dapper or EF would add a second SQL generator to a
codebase whose central claim is that there is exactly one.

## Money

Integer minor units, scaled from `cfg.Currency`. `MinorUnits.ToMinor` is the
only place a decimal becomes an integer and `MinorUnits.Format` the only place
it becomes a string — and `Format` inserts a separator into the digits rather
than dividing. No `float` or `double` appears anywhere in the solution.

Fee rounding is **per transaction, then summed**. `MoneyTests` contains a test
whose only purpose is to show that rounding the sum gives a different answer, so
that nobody later collapses the calculation into a single rounded total. The
design names that as the most common cause of netting discrepancies that never
close.

## What each stage does, and why in that order

| Stage | Notes |
|-------|-------|
| **Acquire / Parse** | `CsvReader` streams; a 2M-row file is never a list. `RowParser` reuses one `StagingRecord` across rows, so the hot path allocates only the values — and a test proves `Reset` leaves nothing behind, because a missed slot would let row N inherit row N−1's value invisibly |
| **Stage** | `StagingBulkCopy` with `TableLock`, 100k batches, batch-local sorting on the clustered key. There is no "load into a heap then index" path: staging is one shared table with a permanent clustered index (review blocker A1) |
| **Reset** | Only for a run reading another run's staged rows — a `Rematch` or a `Sandbox` replay. Those rows still carry the source run's verdict, and everything below filters on `Unmatched`, so without this the replay excluded nothing, found no duplicates and matched nothing while the stale cache made its totals look complete |
| **Exclude / Duplicates** | Before pass 1, once per dataset. With `Reset`, the only statements that write `MatchStatus` before the end of the run |
| **Match** | Ordered passes. Each writes **only** to `ops.MatchResult` and anti-joins against it; candidates are materialised and counted per side before anything is written, so a composite pass cannot explode many-to-many |
| **Stage (finalize)** | One set-based update stamps the outcome onto staging, with `ResultRunId` saying which run produced it |
| **Classify** | Once per dataset, first matching rule wins. Each exception snapshots its matchable key values, so late-arrival auto-close survives staging archival |
| **Aggregate** | `ops.RunAggregate` — the durable totals Operations returns to later, and what makes a 3-month staging retention safe |
| **ControlTotals** | Two aggregates compared. A non-zero net difference on a failing check **fails the run**, however many rows matched |

## Reading staging: always `StagingRunId`

`stg.StagingTransaction.LoadRunId` is the run that **staged** the row. A
`Rematch` reuses another run's rows, so the run executing and the run that
loaded the data are different, and `ops.ReconRun.StagingRunId`
(`ISNULL(SourceRunId, RunId)`) is the one to join on. Reading `LoadRunId =
RunId` returns nothing at all on a Rematch — which is exactly what happened
before the schema stated the rule.

## Getting the file in: one arrival, one pipeline

Two things can put a file into a run — an operator uploading it, or
`Recon.Engine/Providers/FolderAcquisition` fetching it from a directory — and
both go through the same two classes afterwards. `SourceFileRepository` records
the arrival in `ops.SourceFile` with its SHA-256, and `Staging/FileStager`
parses it under the run as a checkpointed step. Every staged row and every
parse error carries the `SourceFileId`, so "which file produced this row" is a
query rather than an inference from load order.

`FolderAcquisition` substitutes the business date into the file format's
file-name pattern (`{yyyyMMdd}`, `{yyyy-MM-dd}`, `{ddMMyyyy}`, `{yyyy}`,
`{MM}`, `{dd}`, `{session}`) and matches it as a regex, bounded by a timeout
because the pattern comes from configuration. Two files matching is an error,
not a choice. `Sftp` and `Api` are refused by name: the seam is that a new
method is a new class here, and half of one is worse than none.

## Building and testing

The SDK cannot be installed in the environment this was written in (the network
policy blocks Microsoft's download host), so `./build.sh` runs the SDK in a
container. On a machine with .NET 8 installed, the same `dotnet` commands work
directly.

```bash
./build.sh                 # restore + build, warnings as errors
./build.sh test            # 168 unit tests
./build.sh test-all        # + integration tests (needs RECON_TEST_CONNECTION)
./build.sh perf            # the Phase 1 2M-row spike
```

The portal has its own runner and its own browser suite:

```bash
./demo/run-portal.sh --background          # serve it against the demo database
node tests/browser/drive-portal.js         # 136 assertions in Chromium
```

The integration tests **skip** rather than fail when `RECON_TEST_CONNECTION` is
unset, so the unit suite runs anywhere. Start a server with
`./db/tests/run.sh`, then:

```bash
export RECON_TEST_CONNECTION="Server=127.0.0.1,1433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False"
./build.sh test-all
```

## Measured: the Phase 1 spike

2,000,000 rows per side, on SQL Server 2022 in a container sharing a cloud VM
with the test host — slower than any real deployment, so treat these as a floor.

| Stage | Measured | Budget |
|-------|----------|--------|
| Bulk load, left | 104.7s (19,111 rows/s) | ≤ 120s ✓ |
| Bulk load, right | 102.3s (19,555 rows/s) | ≤ 120s ✓ |
| Pass 1 — reference | 44.0s, 1,980,000 matched | ≤ 60s ✓ |
| Pass 2 — normalized | 8.6s | ≤ 60s ✓ |
| Pass 3 — composite | 6.3s | ≤ 60s ✓ |
| Finalize staging | 85.5s | *no budget in the design* |
| Classify (per side) | 1.8s, 20,000 exceptions | — |
| Aggregate | 10.6s | — |
| **Full run** | **166.4s** | ≤ 15 min ✓ |

Every budget is met, so the design's escalation path (daily partitions +
`SWITCH PARTITION`) stays deferred — which is the decision the spike exists to
make.

**The finalize step is the thing to watch.** At 85.5s it is the second most
expensive stage and the design gives it no budget: it is a single set-based
update over 4M rows, and it grows with volume the same way pass 1 does. It is
comfortable inside the session budget today and is the first place to look if
that stops being true.
