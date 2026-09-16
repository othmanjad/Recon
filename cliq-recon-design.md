# Reconciliation Platform
## Technical Design Document — v1.0 (post-review, schema consolidated)

**Source:** SRS "Cliq Reconciliation Portal and Cliq Interchange Module" V1, 7-4-2026
**Status:** Reviewed twice (see `design-review-v03.md`). The v1.0 pass closed the
five structural gaps found when the v0.1 base schema and the v0.2 delta were read
as one artifact; the schema now lives as a single executable script in `db/`.
The remaining items in §15 are **business answers, not design work** — none of
them blocks the Phase 1 build.

**Product definition (revised in v0.2):** this is **not** a CliQ reconciliation system. It is a **generic reconciliation platform** on which an Operations user can onboard **any counterparty** — JoPACC, a bank, a scheme, a remittance partner — define its data sources, build its matching rules, its fee schedules and its reports **from the portal, without a developer**.

**CliQ is the first configured reconciliation, not the product.** Everything the SRS describes becomes a row of configuration.

**Core principle:** the engine knows nothing about CliQ, JoPACC, or Orange Money. It knows datasets, fields, and rules.

---

## 1. Scope

The SRS deliverables, re-expressed as configuration on the platform:

| # | SRS module | On the platform |
|---|-----------|-----------------|
| 1 | Reconciliation Processing | A reconciliation definition: CliQ session dataset ↔ OM dataset |
| 2 | OFF-US Matching | A second definition: OFF-US dataset ↔ CliQ dataset (outbound scope) |
| 3 | Reconciliation Portal | The platform UI itself |
| 4 | Cliq Interchange Module | A fee schedule + a definition: calculated fees ↔ IPS Fee Net report |

**OFF-US definition (confirmed):** transactions going **out** of Orange Money to an external party through CliQ.

The engine components below are built once. Adding counterparty #2 costs configuration time, not development time — that is the entire point of the design.

---

## 2. Platform object model

This is the backbone. Everything else hangs off it.

```
Counterparty                    JoPACC · Bank X · Scheme Y · Partner Z
   │
   ├── Dataset                  a named stream of transactions
   │     ├── Provider           File | Sql | Api
   │     ├── FileFormat[]       versioned, effective-dated (CSV / XML / JSON)
   │     ├── FieldRegistry      the fields this dataset exposes ◀── feeds the rule builder
   │     └── Acquisition        schedule, endpoint, credentials, naming pattern
   │
   ├── ReconciliationDefinition the unit the user creates in the portal
   │     ├── LeftDatasetId / RightDatasetId
   │     ├── MatchRuleSet       ordered passes, user-built
   │     ├── ClassificationSet  what unmatched rows mean here
   │     ├── ControlTotalSet    the balance proofs for this recon
   │     ├── ReportDefinition[] output sheets
   │     ├── Schedule           when it runs
   │     ├── AlertPolicy        file not received, run failed, threshold breached
   │     └── Version            ◀── runs record which version executed them
   │
   ├── FeeSchedule[]            effective-dated, tiered
   └── AccessPolicy             which Ops roles may see/edit this counterparty
```

**A reconciliation is always dataset ↔ dataset.** Not "file vs database". That generalization matters: file↔file, API↔database, calculated↔reported are all the same operation to the engine. The Interchange module is just a definition whose left dataset is *calculated fees* and whose right dataset is the *IPS Fee Net report*.

### 2.1 Onboarding a new counterparty — the target workflow

1. Create the counterparty
2. Define a dataset: choose provider, upload a sample file, the system proposes a field registry from it
3. Review and adjust the field mappings and labels
4. Define the opposite dataset (often an OM view/SP)
5. Build the rule set: pick field pairs and comparison types, pass by pass
6. **Dry-run against the sample** — see match rates per pass before anything is live
7. Define classifications, control totals, reports
8. Activate → the scheduler picks it up

No deployment. No developer. Step 6 is not optional — without a sandbox run, Operations will activate broken rules against production data, and the first time they do it the platform loses its credibility.

---

## 3. Confirmed decisions

| Topic | Decision |
|-------|----------|
| Multi-counterparty | **Required** — self-service onboarding from the portal |
| File formats | CSV today; XML / JSON must work without engine changes |
| Acquisition | Scheduled API pull returning CSV (per-dataset configurable) |
| OM data access | SQL View or Stored Procedure (batch) / API (single lookup) |
| Matching keys | **User-defined per rule** — field-to-field pairs chosen in the UI |
| Amount comparison | To the **minor unit** — exact, on integers. JOD has 3 (fils), USD/EUR have 2; the scale comes from `cfg.Currency`, never from a constant |
| Any difference | Treated as a break |
| Failed Inward | **Detect and report only.** No automatic posting in this phase |
| Maker/Checker | Not required now (audit trail still mandatory) |
| Fees | Tiered, per-transaction calculation then aggregation |
| Fee applicability | Configurable per source/type — some reports carry fees, some do not |
| Volume | ~2,000,000 transactions **per day** |
| Users | Operations |
| UI language | English |
| UI stack | **HTML + Bootstrap 5 + jQuery + plain JavaScript.** No SPA framework, no build step — see §13.1 |
| Scheduling & alerting | Required |
| DB topology | Recon DB and OM DB may be same or different servers |
| Timestamps | Local time (Jordan, UTC+3, no DST) — timezone stored explicitly |

---

## 4. Volume: what 2M/day forces

730M+ rows per year. Three non-negotiable consequences:

1. **No in-memory matching.** All matching executes as set-based SQL over staged data. A `LINQ` join at 2M × 2M is not viable.
2. **Both sides are always staged locally**, even when OM sits on the same server. This makes the design **independent of server topology** — identical code path for same-server, linked-server, or separate servers — and avoids linked-server row-by-row remote fetches, which collapse at this scale.
3. **Partitioning is designed in, not added later.** Retrofitting it onto a 700M-row table is its own migration project.

**Target run budget (to validate):** bulk load ≤ 2 min · each pass ≤ 60 s · full session run ≤ 15 min.

---

## 5. Architecture

```
          ┌────────────────────────────────────────────────────────┐
          │           CONFIGURATION (database, portal-managed)     │
          │  Counterparties · Datasets · Field registries          │
          │  Formats & mappings · Rule sets · Classifications      │
          │  Control totals · Reports · Fee schedules · Schedules  │
          └───────────────────────────┬────────────────────────────┘
                                      │ drives every stage
  ┌──────────┐  ┌──────────┐  ┌───────▼──┐  ┌────────┐  ┌──────────┐  ┌────────┐
  │ ACQUIRE  │─▶│  PARSE   │─▶│  STAGE   │─▶│ MATCH  │─▶│ CLASSIFY │─▶│ REPORT │
  │ file/sql │  │ schema-  │  │ bulk     │  │ set-   │  │  rules   │  │ Excel  │
  │ /api     │  │ driven   │  │ copy     │  │ based  │  │          │  │        │
  └──────────┘  └──────────┘  └──────────┘  └────┬───┘  └──────────┘  └────────┘
                                                 ▼
                                        ┌─────────────────┐
                                        │ CONTROL TOTALS  │
                                        │  (balance proof)│
                                        └─────────────────┘
```

Every box is counterparty-agnostic. Every arrow is driven by configuration.

### 5.1 Provider abstraction

Both sides of a reconciliation are datasets, supplied by one of three providers:

| Provider | Reads from | Used for |
|----------|-----------|----------|
| `FileProvider` | filesystem / API-delivered file | CliQ sessions, IPS reports, partner files |
| `SqlProvider` | view or stored procedure | OM database snapshot |
| `ApiProvider` | REST endpoint | single-record lookup, partners without files |

A provider returns `IEnumerable<IDictionary<string,string>>` and knows nothing downstream of itself. `FileProvider` delegates to one of three readers — `CsvReader`, `XmlReader`, `JsonReader` — selected by the format definition. A new physical format = one new reader class; nothing else in the system changes.

**What is built:** `Recon.Engine.Providers.FolderAcquisition`, plus uploads through the portal. It reads `cfg.AcquisitionDefinition`, substitutes the business date into the format's file-name pattern, and refuses to choose when two files match — which of them is the day's truth is not something to guess at. `Sftp` and `Api` are in the schema's method list and are **refused by name** rather than half-built, in the screen and in the controller: configuration that fetches nothing looks like coverage, which is worse than a blank. The seam this section promises is that a new method is a new class beside it and nothing else changes.

Whichever way a file arrives, it is recorded once in `ops.SourceFile` with its SHA-256, and every staged row and parse error carries that file's id. The unique index on (dataset, hash, business date) is what makes the same content arriving twice a duplicate rather than a second run.

---

## 6. The field registry — what makes the rule builder possible

In v0.1 the canonical model had fixed, CliQ-shaped columns (`EndToEndId`, `DebtorAlias`). That cannot survive multi-counterparty: the next partner will use `ARN`, `MTCN`, or `BillingRef`.

**Replacement: every dataset carries a registry of its own fields.**

```
DatasetField
├── DatasetId
├── FieldCode          internal, stable      e.g. REF_PRIMARY
├── DisplayLabel       what Ops sees         e.g. "End To End Id"
├── DataType           String | Integer | Decimal | DateTime | Boolean
├── Role               Reference | Amount | Date | Direction | Status | Party | Other
├── IsMatchable        may appear in a rule
├── IsIndexed          maintain an index on it
└── StorageSlot        physical column it lands in (see §7)
```

Three things follow from this:

- **The rule builder populates its dropdowns from the field registry**, so the user picking fields for JoPACC sees CliQ vocabulary, and for a card scheme sees card vocabulary — from the same UI, with no code.
- **`Role` gives the engine semantics without hardcoding names.** The engine doesn't look for "Amount"; it looks for the field whose role is `Amount`. That is how generic control totals, fee calculation, and date partitioning work across counterparties that name nothing alike.
- **`IsMatchable` is the security boundary.** Rule conditions resolve field names against the registry only — never from free user text. This is what prevents SQL injection through a user-built rule builder, and it is the single most important safety property in the whole design.

### 6.1 Universal fields

Regardless of counterparty, every dataset must map **at minimum**: one reference field, an amount, a currency, and a direction. The definition cannot be activated without them. These are what control totals and fee logic rely on.

**The transaction date is optional, and was not always.** It was required here until it was asked to be optional, and it can be: `TxDate` is the partition column, so it must come from somewhere deterministic, and for a dataset with no Date-role field that somewhere is the business date — which `RowParser` already did, because a summary feed is one row describing a whole session and has no transaction date of its own. What such a dataset gives up is real and is reported on the screen as an advisory rather than as a block: no `DateWithin` comparison against that side, and a transaction arriving late cannot be matched against the day it actually belongs to, because the matching window is a range over `TxDate` and every row now sits on the business date. Blocking activation over that was the platform deciding a business question it does not own.

---

## 7. Physical storage of dynamic fields — the hard trade-off

Dynamic fields at 2M rows/day is the one place where "fully dynamic" collides with physics. Three options:

| Option | Verdict |
|--------|---------|
| **EAV** (one row per field value) | **Rejected.** 2M transactions × 20 fields = 40M rows/day. Every match becomes a self-join nightmare. Non-viable at this volume — this is where flexible designs usually die |
| **Typed slot columns** — fixed wide staging table with `Text1..20`, `Num1..10`, `Date1..5`, mapped via `StorageSlot` | Viable. No runtime DDL, uniform partitioning, fully indexable. Cost: unreadable raw tables, wasted space, hard ceiling on field count |
| **Generated per-dataset tables** — `stg_<DatasetCode>` created from the field registry on activation | Viable. Real typed columns, isolated volume — but requires the application to hold DDL rights in production |

**DECISION (confirmed): typed slot columns.**

Rationale: the two viable options perform comparably — both are real, indexed columns, neither is EAV. The difference is organisational, not technical: generated tables require granting the application `CREATE TABLE` in a production financial database, which is a review cycle that may take weeks and may end in refusal. Slots need no one's permission and remove an entire subsystem (schema lifecycle management) from the codebase, worth roughly a week of work and a class of bugs.

Three conditions make slots work well:

1. **Be generous with slot counts from the start** — 30 text, 15 integer, 5 decimal, 8 date, 5 boolean. Widening later means `ALTER TABLE` on a table with hundreds of millions of rows. Empty columns cost almost nothing in SQL Server.
2. **The field registry is the only path to the data.** No hand-written query ever references `Text7` directly. This is what preserves the option to switch storage models later.
3. **Generate a readable view per dataset** from the registry (`Text1 AS EndToEndId, Num1 AS AmountMinor`). This removes the only genuine drawback of slots — unreadable raw tables — and creating a view is not a data-structure change, so it does not attract the objection that generated tables do.

**Load path (corrected in v0.3):** the earlier note "load into a heap, then index" is impossible on a shared table with a permanent clustered index. The real path: `SqlBulkCopy` with `TableLock`, batches of ~100 000, and an **`ORDER` hint matching the clustered key `(DatasetId, TxDate, StagingId)`** — sorted input into a clustered index is minimally logged and avoids page splits. Nonclustered indexes are kept to ≤ 4 per dataset. **Phase 1 includes a mandatory performance spike** at 2M synthetic rows; if load exceeds 2 minutes, the escalation path is daily partitions + a persisted composite partition column + heap load + `SWITCH PARTITION`.

**Volume isolation without separate tables:** `DatasetId` is the **leading column of the clustered index**, so every query seeks its own dataset's range and ignores the rest. Partitioning is on `TxDate` (SQL Server partitions on a single column). Together these give the same practical isolation as per-dataset tables. Without both, generated tables would clearly win — with them, the difference is marginal.

### 7.1 Money handling — non-negotiable

- **Never** `float` or `double`, at any stage.
- Store `DECIMAL(18,3)`; derive `AmountMinor = CAST(ROUND(Amount * POWER(10, MinorUnits), 0) AS BIGINT)`, where `MinorUnits` comes from `cfg.Currency` (JOD = 3 → fils; USD/EUR = 2).
- **All comparisons, joins, and sums use `AmountMinor` (integer).** Decimal comparison across systems produces phantom differences; integer comparison cannot.
- Fee rounding is **per transaction** to the minor unit, then summed. Summing first and rounding last yields a different netting figure than the counterparty's — the most common cause of netting discrepancies that never close.

---

## 8. Dynamic file definitions

**`FileFormatDefinition`** — one row per format **version**: type (Csv/Xml/Json), delimiter, encoding, header flag, skip lines, filename pattern, record path (XPath/JsonPath root), `Version`, `EffectiveFrom`, `EffectiveTo`.

Effective-dated versioning is what lets the system re-read a 2024 file correctly after the layout changes in 2026. Without it, historical re-runs break.

**`FieldMapping`** — one row per field:

| Column | Purpose |
|--------|---------|
| `SourcePath` | CSV column name/ordinal, XPath, or JsonPath |
| `DatasetFieldId` | target field in the registry |
| `DataType` / `Format` | parsing |
| `Transform` | Trim, Upper, StripNonAlphanumeric, Substring, RegexExtract, Lookup |
| `IsRequired` | rejects the row if missing |
| `DefaultValue` | applied when absent |

New column from the partner → one new row. CSV becomes XML → same mappings, `SourcePath` becomes an XPath, reader swaps. **No engine code changes in either case.**

---

## 9. Dynamic matching engine

```
MatchRuleSet                    belongs to a ReconciliationDefinition
└── MatchRule                   ordered passes, Sequence 1..n
    ├── LeftFilter / RightFilter      optional row filters
    ├── Cardinality                   OneToOne | OneToMany
    ├── OnMultipleMatch               MarkAmbiguous | TakeEarliest | Fail
    └── MatchCondition[]              the field pairs the user chooses
        ├── LeftFieldId  ─┐
        ├── RightFieldId  ├─ both resolved from the field registry
        ├── ComparisonType
        └── Tolerance
```

### 9.1 Comparison types offered in the UI

| Type | Behaviour | Indexable |
|------|-----------|-----------|
| `Exact` | literal equality | yes |
| `NumericExact` | integer minor-unit equality | yes |
| `NumericTolerance` | within ± N minor units (range seek) | yes |
| `DateExact` | same calendar date | yes |
| `DateWithin` | within ± N minutes / hours / days (range seek) | yes |
| `StartsWith` | prefix match | partly |
| `EndsWith` / `Contains` | partial match | **no — UI warns; late passes only** |

**`Normalized` is not a runtime comparison.** Normalizing inside a join predicate (`UPPER(TRIM(...))`) defeats every index. Instead, a registry field flagged `NormalizeForMatch` gets a **companion slot populated at parse time** with the normalized value; the rule compares that field with `Exact`. Same performance as the clean case.

### 9.2 Match modes

| Mode | What it does |
|------|--------------|
| `Row` | row ↔ row on the chosen field pairs |
| `Aggregate` | groups one or both sides by user-chosen `GroupByFields`, sums the Amount-role field and counts rows, then joins the grouped result — **one summary line ↔ many transactions** is an ordinary rule |

Aggregate mode is what lets a JOPACC summary line, a settlement batch total, or a monthly fee report be reconciled against detail rows without special code.

### 9.3 How passes work

Rules run in sequence. **Passes write only to `MatchResult`** — staging is never updated mid-run. "Still unmatched" for pass N is an anti-join: `NOT EXISTS (MatchResult for this RunId and this StagingId)`. `MatchStatus` on staging is written **once**, in a single set-based update at the end. Consequences: a failed pass is retried by deleting its `MatchResult` rows; no half-updated staging; no index churn on 2M rows per pass.

Before pass 1: **exclusion rules** remove rows that must not match (e.g. rejected status) and **duplicate detection** flags second-and-later occurrences of the dataset's declared key as `Duplicate`. Neither enters matching; both are reported.

A typical rule set for CliQ ↔ OM:

| Seq | Conditions | Purpose |
|-----|-----------|---------|
| 1 | primary reference — Exact | the clean majority |
| 2 | primary reference (normalized companion) — Exact | whitespace / case / formatting noise |
| 3 | secondary reference — Exact | fallback |
| 4 | amount Exact + date Within(1d) + account Exact | composite last resort |

**Operational value:** the distribution of matches across passes is a data-quality early-warning system; `MatchDistributionDrift` is an alert type.

### 9.4 Compilation and safety

The engine compiles a rule into SQL over the two staging ranges.

- Field names come **from the registry only**, never interpolated from user text (§6). Source filters, rule filters, classifications, and report filters all use **one structured condition-tree schema**, validated on save, compiled by the same class.
- Values are parameterized. **One class, and only one class, emits SQL.**
- The generated SQL is **persisted with the run step**.
- **Candidate resolution:** the compiler never `UPDATE … FROM` a join. Candidates go to a temp table with `COUNT(*) OVER (PARTITION BY LeftId)` and `(PARTITION BY RightId)`; any side with count > 1 resolves per `OnMultipleMatch`. This is what keeps composite passes (amount + date + account) from exploding many-to-many.

### 9.5 Ambiguity is a first-class result

At 2M/day, duplicate references are certain. A rule matching multiple candidates must **never silently pick one**: the row becomes `Ambiguous` and goes to Operations.

### 9.6 Indexing

Indexes on staging are generated at dataset **activation**, not at run time: one nonclustered index per (dataset, field) appearing in a pass-1 or pass-2 condition, key `(DatasetId, <slot>, TxDate)`, **capped at 4 per dataset**. The UI shows which fields are indexed. Every extra index is a 2M-row maintenance cost on every load.

### 9.7 Reproducibility

`DefinitionVersion` alone proves nothing when rule rows are edited in place. Every run therefore stores **`DefinitionSnapshotJson`** — the full effective definition (rules, conditions, classifications, control totals, both field registries, fee schedules in effect) serialized at run start — and the compiler reads **from the snapshot, not live config**. "What rule matched this in June" is then answerable from the run itself.

### 9.8 Concurrency and restartability

- An application lock keyed on `(DefinitionId, BusinessDate)` prevents the scheduler and a manual trigger running the same reconciliation simultaneously; the second attempt is recorded as `Rejected`.
- Each `ReconRunStep` is a checkpoint. `Resume` re-executes from the first non-completed step. Acquire and Parse are idempotent by file hash; a pass is idempotent by `(RunId, MatchRuleId)`.
- **Rerun types:** `Rerun` re-acquires and re-stages (corrected file); `Rematch` reuses the source run's staged rows and only re-executes matching (rule change). Both supersede the prior run (`IsCurrent = 0`) and never overwrite it.

## 10. Classification and exceptions

Classification turns unmatched rows into business meaning — by rule, per definition, not by code. For CliQ ↔ OM:

| Condition | Exception code | SRS requirement |
|-----------|---------------|-----------------|
| In CliQ, not in OM | `FAILED_INWARD` | ✓ |
| In OM, not in CliQ | `MISSING_IN_CLIQ` | ✓ |
| Reversed in OM, CliQ record exists | `REVERSED_WITH_RECORD` | ✓ |
| Not reversed, no CliQ record | `NOT_REVERSED_NO_RECORD` | ✓ |
| Matched, amounts differ | `AMOUNT_DIFFERENCE` | OFF-US module |
| Multiple candidates | `AMBIGUOUS` | added — not in SRS |

Each classification carries an **action hook**, today always `ReportOnly`. The hook for automatic Failed Inward posting exists in the model but is **disabled and unimplemented** — when the business asks for it, it is work behind an existing seam, not a redesign.

### 10.1 Late arrivals (added — not in SRS)

A transaction near the session cut-off can legitimately land in the next session. Rule: an open break matched in a later run closes as `AutoClosed`, with the closing run recorded. Each exception stores a **snapshot of its matchable key values** (`KeyValuesJson`), so the auto-close pass joins today's unmatched rows to open exceptions by those keys — independent of whether the original staging rows are still online. Without this the open-items list grows forever and Operations stops trusting it within weeks.

---

## 11. Control totals and summaries — balance proof

Row-level matching is not enough; each run must prove the net position, and Operations must be able to **return to any past reconciliation, take a total, and compare it to a summary** — daily, per session, or across a period.

Three mechanisms, all configuration:

**1. `RunAggregate` — durable totals per run.** Every run persists count and amount (minor units) per dataset, per side, per grouping key (e.g. `Direction=Inward`), per match status. Returning to "run 4471, matched inward total" is a primary-key read, never a rescan of staged rows — and it survives staging archival.

**2. Control-total checks with scope and source.** Each check compares two aggregates:

| Property | Values |
|----------|--------|
| `Scope` | `Run` · `BusinessDate` (all sessions of a day) · `Period` (N days) |
| `SourceType` A / B | `Staging` · `MatchResult` · `RunAggregate` · `Dataset` (a summary file/API modelled as its own dataset) |
| `ToleranceMinor` | default 0 — exact |
| `FailRunOnMismatch` | default true |

**Current-run rule:** period-scoped aggregates use only runs with `IsCurrent = 1`; superseded reruns and sandbox runs are excluded automatically.

**3. Aggregate match mode (§9.2)** for line-level summaries — one summary row ↔ many detail rows, with the group key chosen by the user.

For CliQ:

| Check | Source A | Source B | Scope |
|-------|----------|----------|-------|
| Debit count & total | Staging (CliQ, Direction=Outward) | JOPACC summary dataset | Run |
| Credit count & total | Staging (CliQ, Direction=Inward) | JOPACC summary dataset | Run |
| OM vs CliQ totals | RunAggregate (OM side) | RunAggregate (CliQ side) | Run |
| Daily net across sessions | RunAggregate | Daily summary dataset | BusinessDate |
| Monthly fee netting | RunAggregate (fees) | IPS Fee Net report dataset | Period |

The JOPACC summary may arrive **inside the session file or from a separate API** — it is modelled as its own dataset either way.

**A run with fully matched rows but a non-zero net difference is a failed run.**

## 12. Fees and interchange

Generic, per counterparty:

**`FeeSchedule` / `FeeTier`** — direction, transaction type, amount band (in minor units), calculation type (Fixed / Percentage / Fixed+Percentage), value, min/max cap, `EffectiveFrom` / `EffectiveTo`, counterparty.

**Applicability** is a configuration flag per source/transaction type — confirmed that some reconciliation reports carry fees and some do not.

Flow: per-transaction calculation → Revenue (paid to OM) / Cost (paid by OM) → Netting → **compared against the counterparty's fee report as an ordinary reconciliation definition**.

**Why transaction-level:** an aggregate-only figure is one you cannot defend. Transaction-level lets you answer "why is our netting 4.120 JOD below theirs" by pointing at rows — which is the reason the module exists.

Effective dating is mandatory: a tariff change must never retroactively alter last month's figures.

---

## 13. Portal

| Area | Feature |
|------|---------|
| **Configuration** | Counterparties · datasets · field registry · format & mapping editor · **visual rule builder** · classifications · control totals · report designer · fee schedules · schedules & alerts |
| **Sandbox** | Dry-run a definition against a sample file; per-pass match rates before activation |
| **Operations** | Run dashboard (status, duration, match distribution per pass) · session browser · exception workspace (filter, assign, comment, close with reason, aging) |
| **Search** | Single + bulk, per dataset, respecting each dataset's field labels |
| **Downloads** | Excel for exception-level scopes; **CSV by default for full-transaction scopes** (a worksheet holds 1 048 576 rows — a 2M-row day does not fit; the engine auto-splits sheets and uses a streaming writer) · **byte-identical original files** with hash verification |
| **Admin** | Role-based access **per counterparty**, audit log viewer |

**Access control:** with multiple counterparties, permissions are scoped per counterparty — a user managing JoPACC need not see another partner's data. This must exist from Phase 3, not be bolted on later.

**Search at this volume:** SQL Server with correct indexes covers exact-reference and date-range lookups. **Elasticsearch is not recommended now** — real operational overhead, and nothing in the requirements is a free-text search problem. Revisit only if that changes.

**Original file integrity:** stored on filesystem / object storage with SHA-256 and metadata in the database — **not as database blobs**. At 2M/day the blob path leads straight to an unmanageable database (the OJM lesson). Archive policy is defined at build time.

### 13.1 Front-end stack (decided)

**HTML, Bootstrap 5, jQuery and plain JavaScript. No SPA framework, no build step, no CDN.**

The libraries are vendored into the application and served from it —
`src/Recon.Web/wwwroot/vendor/` for the portal, `ui/vendor/` for the prototype
that preceded it. A reconciliation portal inside a bank's network must not
depend on an outbound request to a third party to render its own page, and an
air-gapped deployment must not require a Node toolchain to produce a
stylesheet.

What this buys, and what it costs:

| | |
|---|---|
| **Fits the screens we actually have** | Configuration forms, dense tables, and a rule builder. All are server-rendered pages with local interactivity — not a client-side application with its own state machine |
| **One less runtime to secure and patch** | No npm dependency tree shipped to production. The three vendored files are pinned, reviewed, and replaced deliberately |
| **Any .NET developer can maintain it** | No framework-specific expertise needed to add a field to a form |
| **The cost** | Rendering is string concatenation in jQuery, so every screen must escape its own output. `Recon.escapeHtml()` exists for exactly this and is used on every interpolated value — a missed call is an XSS bug, and that is the standing review item for all front-end changes |

The rule builder is the one screen with real client-side state (ordered passes,
each with a variable number of conditions). It re-renders from a plain JavaScript
array on every change and uses delegated event handlers — simple enough to hold
in one's head, and the reason no framework is needed.

**The condition tree is validated on the client for immediate feedback and on the
server as the actual boundary.** `ui/js/condition-tree.js` rejects a field code
that is not in the dataset's registry, and one that is in it but not marked
matchable; the identical check runs server-side, because a client-side validator
is a courtesy to Operations, never a security control.

---

## 14. Operational data model

| Table | Purpose | Partitioned |
|-------|---------|-------------|
| `ReconRun` | one row per execution; records definition version | — |
| `ReconRunStep` | per-stage status, timing, row counts | — |
| `SourceFile` | path, hash, size, received/processed, status | by date |
| `stg_<DatasetCode>` | staged canonical rows, per dataset | **by date** |
| `ParseError` | rejected rows with reason and raw line (capped per file) | by date |
| `MatchResult` | left id, right id, rule code, status | **by date** |
| `ReconException` | open items, aging, assignment, resolution, notes | by date |
| `ControlTotalResult` | per run, per check, expected vs actual | — |
| `RunAggregate` | durable per-run totals by dataset / group / status — the numbers you return to later | — |
| `TransactionFee` | per-transaction calculated fee | **by date** |
| `InterchangeSummary` | aggregated revenue / cost / netting | — |
| `AuditLog` | every configuration change and manual action | by date |

**Re-runs never overwrite.** A corrected file produces a **new run** with a new `RunId`; the prior run is marked `IsCurrent = 0` and remains intact and queryable.

**Retention is two parameters, not one:** `StagingMonthsOnline` (default 3 — the largest cost in the system) and `ResultsMonthsOnline` (the audit retention period, still open). `RunAggregate` and exception key snapshots make short staging retention safe. Partitions older than the current month get page compression; a scheduled job keeps ≥ 3 future monthly partitions ahead and alerts when fewer than 2 exist.

**Read consistency:** `READ_COMMITTED_SNAPSHOT` is enabled so the portal never blocks behind the loader.

**Least privilege:** separate database roles — loader (insert on staging), app (config/ops), activator (`CREATE VIEW` and indexes on the staging schema only), reader. The application is never `db_owner`.

**The audit log is Phase 1**, even though Maker/Checker is deferred. With users editing rules that decide financial outcomes, "who changed this rule and when" is not optional. Retrofitting an audit trail always costs more than writing it.

---

## 15. Open questions

**Blocking Phase 1:**
1. **Sample files** — CliQ session CSV, OFF-US report, IPS report, Fee Net Transactions report.
2. **Is the primary reference globally unique forever, or reused across days?** Determines whether the primary rule joins on reference alone or reference + date.
3. **Retention period** — 1 year? 7 years for audit? Drives partitioning and archive design.
4. **OM view/SP performance** — can it return a full day's window efficiently, and is its date column indexed? The likeliest bottleneck in the pipeline.
5. **Sessions per day and their timing** — drives the scheduler and "file not received" thresholds.

> **Closed in v1.0.** "Runtime DDL acceptable?" was still listed here as a
> Phase-1 blocker while §7 already recorded the opposite: typed slot columns,
> decision confirmed, with the reasoning that generated tables would require
> granting the application `CREATE TABLE` in a production financial database.
> The question is answered — the schema in `db/` is slot-based — and a developer
> reading §15 should no longer see a settled decision presented as a blocker.

**Before the Interchange module:**
7. **Rounding mode** — half-up, half-even, or truncate? Must match the counterparty exactly or netting will never tie out.
8. **Sales tax on fees** — applicable, and at which stage?
9. **Fee schedule source** — document, file, or API? Who maintains it?

**Before Phase 3:**
10. Bulk search — maximum record count and input method.
11. Alert channels — email, SMS, dashboard?
12. Counterparty-level access separation — required from day one, or single Ops team initially?

---

## 16. Delivery phases

| Phase | Content | Exit criterion |
|-------|---------|----------------|
| **1 — Foundation** | Object model, config tables, field registry, CSV reader, parser, parse-time normalization, staging, ordered bulk load, run framework with checkpoints and locking, definition snapshot, audit log, **2M-row performance spike** | A synthetic 2M-row file ingests and stages within budget; one pass runs within budget |
| **2 — Matching** | Rule engine, SQL compiler, passes, classification, control totals, late-arrival closure | A full session reconciles end to end; net difference proves out |
| **3 — Portal** | Rule builder, mapping editor, sandbox dry-run, search, exception workspace, dashboard, per-counterparty RBAC | Operations onboards a **second** counterparty unassisted — the real test of the design |
| **4 — Reporting** | Dynamic report engine, the four SRS Excel outputs | Sheets match the SRS specification |
| **5 — Interchange** | Fee schedules, per-transaction calculation, netting, fee-report comparison | Netting ties to the Fee Net Transactions Report |
| **6 — Automation** | Scheduler, folder acquisition, alerting, retries, archive policy | Runs unattended for a full week |
| **7 — Extensions** | XML/JSON readers, definition cloning/templates, NL search over `QueryJson`, exception clustering | — |

The Phase 3 exit criterion is deliberately harsh: if a second counterparty cannot be onboarded without a developer, the platform was not built — only a CliQ tool with extra tables.

XML/JSON readers sit in Phase 7 on purpose: the architecture already supports them, and building them before a real non-CSV file exists means building against an imagined format.

### 16.1 What is built

All seven phases are implemented and executed. The schema runs on SQL Server 2022 with 88 passing tests; the engine has 168 unit and 10 integration tests and meets every budget in §4 at 2,000,000 rows per side; the portal is ASP.NET Core MVC over the real database, driven in Chromium by 133 assertions across thirteen screens, three access levels and an account with no grants. `./demo/run-demo.sh` reconciles a session from nothing and `./demo/run-portal.sh` serves the portal against it, with Docker as the only prerequisite.

The Phase 3 exit criterion is not something a test can assert: onboarding a second counterparty unassisted is a claim about what an Operations user can do, and it is settled by watching one of them do it. What the portal can show is that nothing in that sequence needs a developer, and `tests/browser/drive-onboard.js` shows it in 115 assertions: a counterparty, two datasets, their field registries, a file format and its field mappings per side, a definition, a matching pass, an exclusion, a classification per side, a control total, two activations and a run from two uploaded files — then a second business date reconciled from a watched folder with nothing uploaded at all, the wrong day's file sitting beside the right one. No SQL anywhere. `demo/01-demo-config.sql` is the same rows written as SQL, for a script that must not need a browser.

The Phase 6 exit criterion — "runs unattended for a full week" — needed that second part to be true at all. The scheduler carried its own copy of the run sequence, that copy had no acquisition step, and nothing read `cfg.AcquisitionDefinition`: an unattended week would have reconciled whatever somebody had already staged. A fired schedule now goes through the same `SessionRunner` the portal's buttons use, and a day whose file never arrived is recorded as a `Rejected` run carrying that reason and raises the file-not-received alert, rather than appearing as a run that matched nothing.

The portal also installs the platform. It builds a connection string, tests it, creates the database and applies the four scripts in `db/`, which means the answer to "where does this run" is a machine with SQL Server on it and nothing else. Every operational step is a screen: the datasets, formats and mappings, the three rule sets, the acquisition folder, the schedules and their alert policies, the scheduler's own switch, and the housekeeping jobs.

The six open questions in §15 remain open. Each is a business answer, each has a place in the schema already, and the portal surfaces two of them where the decision is taken rather than in a document: the rounding mode on a fee schedule says HalfUp is a default and not a decision, and the retention period on the settings screen is flagged as a placeholder.

---

## 17. Position on AI

The matching path is **deterministic and contains no LLM**. Financial reconciliation must be reproducible and explainable to an auditor; identical inputs must always produce identical output. An LLM in the matching decision destroys both properties.

AI is valuable at the edges, and only there:

| Use | Mechanism | Risk |
|-----|-----------|------|
| Onboarding assistant — propose field mappings from a sample file | Generates a draft registry/mapping for **human approval** | Low — never self-applies |
| Natural-language portal search | LLM emits structured `QueryJson`; `SqlQueryBuilder` is the sole SQL generator | Low — validated structure, not SQL |
| Break narrative summaries | Read-only explanation of an existing result | None |
| Exception clustering | Groups recurring break causes | None — analytical only |

The onboarding assistant is where AI pays for itself on this platform: mapping a new partner's file is the slowest step of adding a counterparty, and it is a suggestion task with a human gate — exactly the shape of problem an LLM is safe on.

**No AI component may write to the ledger, decide a match, or close an exception.**
