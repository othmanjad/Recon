# Reconciliation Platform

A **generic reconciliation platform**: an Operations user onboards any
counterparty — a scheme, a bank, a remittance partner — and defines its data
sources, matching rules, fee schedules and reports from the portal, without a
developer.

CliQ is the first configured reconciliation, **not the product**. The engine
knows nothing about CliQ, JoPACC or Orange Money; it knows datasets, fields and
rules. Everything the source SRS describes becomes a row of configuration.

The measure of whether this was built is the Phase 3 exit criterion, and it is
deliberately harsh: **Operations onboards a second counterparty unassisted.** If
that needs a developer, what was built is a CliQ tool with extra tables.

## Repository

| Path | Contents |
|------|----------|
| `cliq-recon-design.md` | The technical design document (v1.0) |
| `design-review-v03.md` | Two review passes, findings and fixes |
| `db/` | The SQL Server schema — four scripts, run in order |
| `db/superseded/` | The v0.1 base and v0.2 delta, kept for history. Do not run |
| `src/` | The .NET 8 engine — see `src/README.md` |
| `ui/` | The portal prototype — HTML, Bootstrap 5, jQuery |
| `build.sh` | Build and test in a container (the SDK cannot be installed here) |

Start with `db/README.md` for the five things worth knowing before reading the
schema, and `ui/README.md` for the front end.

## The constraint that shapes everything

**~2,000,000 transactions per day**, 730M+ rows a year. Three consequences, none
of them optional:

1. **No in-memory matching.** All matching is set-based SQL over staged data. A
   LINQ join at 2M × 2M is not viable.
2. **Both sides are always staged locally**, even when the counterparty database
   sits on the same server. This makes the design independent of server
   topology and avoids row-by-row remote fetches, which collapse at this scale.
3. **Partitioning is designed in, not added later.** Retrofitting it onto a
   700M-row table is its own migration project.

Target run budget, to be validated by a mandatory Phase 1 performance spike:
bulk load ≤ 2 min · each pass ≤ 60 s · full session run ≤ 15 min.

## Position on AI

The matching path is **deterministic and contains no LLM.** Financial
reconciliation must be reproducible and explainable to an auditor: identical
inputs must always produce identical output. An LLM in the matching decision
destroys both properties.

AI is useful at the edges, and only there — proposing field mappings from a
sample file for human approval, emitting structured query JSON for
natural-language search, summarising a break, clustering recurring causes.
**No AI component may write to the ledger, decide a match, or close an
exception.**

## Status

| Area | State |
|------|-------|
| Design document | v1.0, reviewed twice |
| Database schema | Complete, consolidated, and **executed** — 38 tables, built on SQL Server 2022 with 83 passing tests |
| Portal | Prototype: six screens, browser-verified, reading mock data |
| Engine (.NET) | Phase 1 and matching complete — 175 tests, and the 2M-row spike meets every budget |
| Scheduler, reports, fees | **Not started** |

Six questions remain open. All are business answers rather than design work, all
have a place to live in the schema already, and none blocks the Phase 1 build —
see `design-review-v03.md`, "Still open".

### A note on what is verified

**The schema is executed, not just parsed.** `./db/tests/run.sh` builds it on a
throwaway SQL Server 2022 container and runs 28 metadata checks and 55
behavioural tests — the second suite writes bad data and requires the database
to refuse it. Docker is the only prerequisite.

Running it mattered. Two constraints were present, correctly named, and did
nothing:

- A case-insensitive collation let `RunType = 'sandbox'` satisfy a `CHECK`
  listing `'Sandbox'`. SQL Server would then treat the two as equal while C#
  would not, so an application filtering `RunType != "Sandbox"` would pull a
  sandbox run into production aggregates.
- `Scope <> 'Period' OR PeriodDays > 0` accepted a `Period` check with no
  window, because `FALSE OR UNKNOWN` is `UNKNOWN` and a `CHECK` rejects only on
  `FALSE`.

Executing it also caught a deployment hazard: filtered indexes require
`QUOTED_IDENTIFIER ON`, which SSMS sets and **sqlcmd does not** — so the script
now sets it itself rather than failing in whatever CI pipeline runs it first.

**The portal is executed too:** 20 unit tests for the condition-tree validator
and a Chromium pass that drives all six screens, failing on any console error or
horizontal overflow at phone width.

**The engine is executed too**, including against volume. 165 unit tests, 10
integration tests against a live server, and the Phase 1 spike at 2,000,000
rows per side — which meets every budget in the design and is the measurement
that keeps the escalation path deferred. See `src/README.md` for the numbers.

Running the engine found five more defects that the unit tests could not,
including two of a kind worth naming: a checkpoint whose identity omitted the
side, so the right dataset's exclusions, duplicates and classification were
silently skipped; and `UseNormalized` inferred from the fields rather than
stored, which quietly turned pass 1 — the clean reference match — into a second
normalized pass.
