# Portal

ASP.NET Core 8 MVC with Razor views. **No SPA framework and no build step:**
the client side is Bootstrap 5 and jQuery, vendored into `wwwroot/vendor` and
served from there, because a reconciliation portal inside a bank's network must
not depend on an outbound request to a third party to render its own page.

```bash
./demo/run-demo.sh                  # build the database and reconcile a session
./demo/run-portal.sh --background   # serve the portal against it
#   http://127.0.0.1:5080  — sign in as cfg.omar, ops.hala or read.sami
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
| `/datasets` | The field registry and the mapping editor, with the activation gate |
| `/rules` | The visual rule builder and the sandbox dry-run |
| `/fees` | Fee schedules, tiers, applicability, and interchange netting |
| `/schedules` | Cron schedules and alert policies, with the next fire times in both zones |
| `/settings` | `cfg.PlatformSetting`, validated against each setting's declared type |
| `/audit` | `aud.AuditLog`. Read-only **by grant**: no role anywhere has `UPDATE` or `DELETE` on that schema |

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

## The scheduler runs in-process

`Services/SchedulerService` is a `BackgroundService`: it wakes once a minute,
fires the schedules due **in each schedule's own time zone**, raises the alert
conditions, and runs the daily housekeeping (sandbox purge, partition
lookahead, file-not-received). In-process rather than a second deployable
because the phase's exit criterion is "runs unattended for a full week" and a
second thing to keep alive is a second thing that can be down. More than one
node is safe anyway: `sp_getapplock` keyed on `(DefinitionId, BusinessDate)`
means the second node's attempt is refused and recorded, not duplicated.

## What the browser driver proves

```bash
cd tests/browser && npm install          # playwright only
node drive-portal.js                     # needs the portal running
```

115 assertions in a real browser against the real database: every screen
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
