# Portal

ASP.NET Core 8 MVC with Razor views. **No SPA framework and no build step:**
the client side is Bootstrap 5 and jQuery, vendored into `wwwroot/vendor` and
served from there, because a reconciliation portal inside a bank's network must
not depend on an outbound request to a third party to render its own page.

## Running it on your own machine

Nothing to prepare but a SQL Server the portal can reach — it builds its own
database:

```bash
dotnet run --project src/Recon.Web
#   http://localhost:5000 (or whatever it prints)
```

Sign in with any name, and the portal sends you to **/setup** because it has no
database yet. Type the server, press *Test connection*, press *Save*, then
*Create the database and install*. That runs `CREATE DATABASE` and the four
schema scripts — the same files `db/tests/run.sh` runs — and records each one
with the hash of its text, so the button is safe to press twice. *Load the demo
configuration* then gives you a complete worked reconciliation to look at, and
grants you access to it.

The connection string is stored in `recon.settings.json` beside the
application (`RECON_CONFIG_DIR` moves it). A deployment that keeps its secrets
elsewhere sets `ConnectionStrings:Recon` instead, and that value wins and is
never overwritten — the setup screen then shows it read-only rather than
offering to change something it cannot.

With Docker and no .NET installed, the same thing through containers:

```bash
./demo/run-demo.sh                  # build a database and reconcile a session
./demo/run-portal.sh --background   # serve the portal against it
#   sign in as cfg.omar, ops.hala or read.sami
./demo/run-portal.sh --fresh        # ...or with NO database, to walk the setup
./demo/run-portal.sh --stop
```

## The two properties that shape every screen

**1. Every read is filtered by the signed-in user's counterparty grants, in
SQL.** `PortalQueries` is one class rather than a repository per screen for
exactly that reason: the filter is a property of every query, and a screen that
fetched everything and hid rows afterwards would still have leaked them through
counts, totals and paging. `AccessService.CounterpartyFilter` returns `1 = 0`
for a user with no grants — a predicate that matches nothing, rather than an
empty `IN` list (a syntax error) or an omitted filter (everything).

An account with no grants is told so, on every screen. An empty table would say
"nothing exists"; the message says "you have no grants", which is a different
fact and the correct default for a new account.

**2. Nothing the user types becomes part of a SQL statement.** The rule
builder's dropdowns are populated from the dataset's field registry and submit
field *codes*; the server resolves each against that same registry and refuses
one that is absent or not marked matchable, before anything is written. The
condition-tree filter is validated the same way. `wwwroot/js/condition-tree.js`
validates it in the browser too — to give an immediate answer, never as the
boundary.

## Screens

| Route | What it is for |
|-------|----------------|
| `/` | Run dashboard: tiles from the **current** runs only, so a corrected day is not double-counted |
| `/runs` | The run list, and the manual trigger. A second attempt for the same definition and business date is refused by the run lock and recorded as `Rejected` |
| `/runs/detail/{id}` | One run in full: every stage with its timing and **the SQL it ran**, the control totals, the pass distribution, the durable aggregates, the parse errors |
| `/exceptions` | The workspace: filter, assign, close with a required reason, reopen. Aging is computed on the indexed business date |
| `/reports` | The report builder, and the exports themselves — streamed, never buffered |
| `/counterparties` | Counterparties, their definitions, and who may see them |
| `/datasets` | The field registry, the format and mapping editors, the acquisition folder, and what has actually arrived |
| `/rules` | The visual rule builder and the sandbox dry-run |
| `/fees` | Fee schedules, tiers, applicability, and interchange netting |
| `/schedules` | Cron schedules and alert policies, with the next fire times in both zones |
| `/settings` | `cfg.PlatformSetting`, validated against each setting's declared type |
| `/audit` | `aud.AuditLog`. Read-only **by grant**: no role anywhere has `UPDATE` or `DELETE` on that schema |
| `/setup` | Where the database is, and the schema installer. The only screen that answers on a portal which has none yet |

## Access levels

`cfg.UserCounterpartyAccess` gives one level per user per counterparty, and the
same screen looks different at each:

| Level | Sees | Can do |
|-------|------|--------|
| `Read` | everything it is granted | nothing — the forms are not rendered rather than rendered and refused |
| `Operate` | the same | trigger runs, work exceptions, calculate fees, dry-run |
| `Configure` | the same | edit datasets, rules, fees, schedules, settings, and grants |

`AccessService.RequireAsync` throws rather than returning false, because a call
site that carried on would write data the user may not write.
`Filters/AccessDeniedFilter` turns that into a page once, instead of a
try/catch around every action, and logs the refusal with the user and the
counterparty.

Sign-in is a development stand-in and says so on the page: it authenticates
nobody and only names the operator, so that the audit log has a subject and the
access checks have someone to check. A deployment replaces it with the bank's
identity provider — the authorization logic reads claims and does not care
where they came from.

## Everything is a screen

There is no step in operating this platform that needs a shell.

| Was a script or a config file | Now |
|---|---|
| Editing `appsettings.json` and restarting | `/setup` — server, database, credentials, file storage, tested before saving |
| `sqlcmd -i db/01-schema.sql` × 4 | *Create the database and install*, with a ledger of what has been applied and what has changed since |
| `demo/01-demo-config.sql` by hand | *Load the demo configuration* |
| The first grant, inserted by hand | *Grant me access to unadministered counterparties* — it only ever fills a vacancy, so it cannot be used to get into a counterparty somebody already administers |
| `recon run --left-file … --right-file …` | **Upload a session and reconcile it** on `/runs`: two files in, a reconciled run out |
| `INSERT cfg.Dataset` / `FileFormatDefinition` / `FieldMapping` | The dataset, format and mapping editors on `/datasets` — a counterparty can be onboarded without SQL |
| `INSERT cfg.ExclusionRule` / `ClassificationRule` / `ControlTotalDefinition` | The three rule-set editors on `/rules`, each validated through the field registry |
| `INSERT cfg.AcquisitionDefinition`, and hoping something read it | The acquisition card on `/datasets`, with *Check now* — which says whether tonight's run will find the file, before tonight |
| `Recon:Scheduler:Enabled` + a restart | *Stop the scheduler* on `/schedules`, effective within a minute |
| Waiting for the nightly tick | *Run housekeeping and alert checks now* — the same methods the tick calls |

The upload path writes each file to the storage root with its SHA-256 and
parses it from there. The original stays on disk as the byte-identical record
the design requires; the database keeps the path and the hash, never the bytes
— at two million rows a day the blob path is how a database becomes
unmanageable.

The staging itself is `Recon.Engine.Staging.FileStager`, which the CLI and the
scheduler also call. That class exists because the pipeline had quietly grown
a second copy: the CLI carried the whole sequence, and the portal needed
exactly that sequence. Two copies of a pipeline is two pipelines.

Whichever way a file arrives — uploaded or acquired — it is recorded once, in
`ops.SourceFile`, through `SourceFileRepository`, with its SHA-256, its row
count and its reject count. Every staged row and every parse error carries that
file's id, so "which file produced this row" is answerable from the data rather
than from the order things happened to load in. The unique index on
(dataset, hash, business date) is what makes the same content arriving twice a
*duplicate* rather than a second run; the run is still allowed, because a retry
after a configuration fix is legitimate, but the screen says so.

## Where the files come from

A run with no uploads asks each dataset's acquisition for the day's file.
`Recon.Engine.Providers.FolderAcquisition` is the provider: it takes the folder
from `cfg.AcquisitionDefinition`, substitutes the business date into the file
format's file-name pattern — `{yyyyMMdd}`, `{yyyy-MM-dd}`, `{ddMMyyyy}`,
`{yyyy}`, `{MM}`, `{dd}`, `{session}` — matches that as a regex against the
folder, and refuses to choose when two files match. Which of them is the day's
truth is not something to guess at.

Only **Folder** and **Manual** can be saved. `Sftp` and `Api` are in the
schema's list of methods and have no provider, so the screen does not offer
them and the controller refuses them if posted anyway: configuration that
fetches nothing looks like coverage, which is worse than a blank. A new method
is a new class beside `FolderAcquisition` and nothing else changes.

A day whose file never arrived is recorded as a `Rejected` run carrying that
reason, and raises the file-not-received alert — never as a run that
reconciled nothing. The two look identical on a dashboard and are not the same
event.

## The scheduler runs in-process

`Services/SchedulerService` is a `BackgroundService`: it wakes once a minute,
fires the schedules due **in each schedule's own time zone**, raises the alert
conditions, and runs the daily housekeeping (sandbox purge, partition
lookahead, file-not-received). A fired schedule goes through the same
`SessionRunner` the portal's buttons use — acquisition, staging, passes and
all. It used to carry its own copy of that sequence, which is why a scheduled
run reconciled whatever happened to be staged already: the copy had no
acquisition step, and the demo always staged its files first, so nothing said
so. In-process rather than a second deployable
because the phase's exit criterion is "runs unattended for a full week" and a
second thing to keep alive is a second thing that can be down. More than one
node is safe anyway: `sp_getapplock` keyed on `(DefinitionId, BusinessDate)`
means the second node's attempt is refused and recorded, not duplicated.

## What the browser driver proves

```bash
cd tests/browser && npm install          # playwright only
node drive-portal.js                     # needs the portal running
```

133 assertions in a real browser against the real database: every screen
renders with its shell intact and no console error, the rule builder offers no
field the registry withholds, a withheld field in a filter is rejected and a
valid one accepted, a non-sargable comparison warns and warns harder in pass 1,
a dry-run's passes actually match, the mapping editor offers no slot of the
wrong type and no companion outside the companion pool, a settings value of the
wrong type is refused, the cron preview converts Amman to UTC, a CSV export
streams with the registry's columns and lands in the audit log, an exception
can be assigned and closed with a reason, `Read` access is offered no form,
an account with no grants sees nothing at all, and nothing scrolls sideways at
400px.

It was worth writing. It found five defects that the unit, integration, schema
and behavioural suites all passed over — among them a sandbox dry-run that
matched nothing while reporting a flawless run, and every transaction-level
report export failing outright. The things that break in a server-rendered
portal are not the things a unit test sees.

`drive-setup.js` is the other half: it starts from a portal with **no
database**, walks the setup wizard, and ends with a reconciled run.

```bash
./demo/run-portal.sh --fresh             # a portal with no database at all
node tests/browser/drive-setup.js
```

`drive-onboard.js` is the third, and it is the design's Phase 3 exit
criterion as far as software can settle it: it creates a counterparty, two
datasets, their field registries, a CSV format and its mappings per side, a
definition, a matching pass, an exclusion, a classification per side and a
control total, activates all of it, uploads two files it writes itself, and
asserts the run matched what those files were built to match — with no SQL
anywhere.

Then it reconciles a **second** business date the way a scheduled night does
it: it configures folder acquisition, gives the format a file-name pattern,
drops the day's file into the watched directory with the *wrong* day's file
beside it, and triggers a run with nothing uploaded at all — asserting that the
right file was picked, that checking the folder records nothing, that an empty
folder reports the file as not received, that the same content twice is a
duplicate, and that two files matching one pattern are refused rather than
guessed between.

```bash
node tests/browser/drive-onboard.js
```

The "unassisted" half of that criterion is about a person and a test cannot
settle it. What a test can settle is that every step exists as a screen and
that the result reconciles. It found the two defects that made the answer
"no": creating a counterparty was refused by the database on every attempt
(`CreatedBy` is `NOT NULL` and the insert omitted it), and the create forms
carried the currently selected row's id, so a *second* counterparty or
dataset could not be created at all — every save was an edit of whichever one
happened to be first.

`drive-setup.js` asserts the things that are easy to get wrong once and never notice: a
wrong password is refused with the server's own reason rather than written to
disk, the saved connection is rendered redacted, the install creates the
database and reports which scripts it applied, pressing install a second time
recognises its own work instead of failing, the demo seed grants access to the
person who ran it, and two uploaded files produce the same fifteen matched
pairs the CLI reports for them. It found the defect that mattered most here:
`/setup` itself could not be built without a database connection, so every
request to the one screen that has to work without one answered 500.

It now presses *Load the demo configuration* twice, because the script claims
to be re-runnable and pressing the button twice is the only thing that proves
it. The first time anything did, the answer was a 500: the script deleted the
reconciliation definition ahead of the eight tables that reference it, and
`SqlInstallException` — the type the installer throws itself to say which batch
failed — was caught nowhere, so the one path that reports a script failure
threw instead of reporting. Each run also names its own database, so "created
database" is asserted against a genuine first run rather than passing once and
lying afterwards.
