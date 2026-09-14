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
| `ui/` | The portal prototype — HTML, Bootstrap 5, jQuery |

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
| Database schema | Complete and consolidated — 38 tables, parse-checked, not yet deployed |
| Portal | Prototype: six screens, browser-verified, reading mock data |
| Engine (.NET) | **Not started** |

Six questions remain open. All are business answers rather than design work, all
have a place to live in the schema already, and none blocks the Phase 1 build —
see `design-review-v03.md`, "Still open".

### A note on what is verified and what is not

The schema is **parse-checked, not executed.** All 77 batches parse as T-SQL, and
a consistency pass confirms every foreign-key target exists, no constraint or
index name is duplicated, and no legacy identifier survives. It has never been
run against a SQL Server instance, because none is available in this
environment — so `GRANT`, the partition functions, and the slot-pool trigger are
reviewed by eye rather than executed. **Run `db/` against a scratch instance
before trusting it.**

The portal **is** executed: 20 unit tests for the condition-tree validator, and
a Chromium pass that drives all six screens and fails on any console error or
horizontal overflow at phone width.

The .NET engine has not been started in part because the SDK cannot be installed
here — the environment's network policy blocks the Microsoft download host.
