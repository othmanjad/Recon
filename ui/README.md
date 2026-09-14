# Portal prototype

HTML + Bootstrap 5 + jQuery + plain JavaScript. **No SPA framework and no build
step** — open `index.html` in a browser and it runs. The rationale is in
`cliq-recon-design.md` §13.1.

```
ui/
├── index.html            run dashboard — status, duration, match distribution
├── exceptions.html       exception workspace — filter, age, assign
├── datasets.html         datasets and the field registry
├── rules.html            the rule builder and the condition-tree validator
├── counterparties.html   counterparty list and the onboarding sequence
├── settings.html         cfg.PlatformSetting
├── css/recon.css
├── js/
│   ├── recon-shell.js      header, sidebar, and the formatting helpers
│   ├── condition-tree.js   the ONE filter schema, and its validator
│   ├── mock-data.js        sample data shaped exactly like db/01-schema.sql
│   └── page-*.js           one per screen
└── vendor/               jQuery, Bootstrap, Bootstrap Icons — pinned, no CDN
```

## Two rules for anyone editing these files

**1. Escape everything you interpolate.** Rendering is string concatenation, so
`Recon.escapeHtml()` must wrap every value that reaches the DOM. A missed call
is an XSS bug. This is the standing review item for all front-end changes.

**2. Never format an amount by dividing.** `Recon.formatMinor(amountMinor, currency)`
is the only place minor units become a decimal string, and it does it by
inserting a separator into the digits — not by dividing a float. Amounts arrive
as integers and stay integers.

## What the prototype actually demonstrates

- **The field registry drives the rule builder.** Every dropdown is populated
  from `cfg.DatasetField`. A field the registry does not mark matchable — the
  `CURRENCY` field, in the sample data — never appears as an option.
- **The condition tree rejects what it should.** `rules.html` ships a
  deliberately hostile example: a SQL payload as a field code, a non-matchable
  field, and an empty `in` list. Press *Load rejected example* and the three
  reasons are listed; nothing compiles.
- **Non-indexable comparisons are visible as such.** Choose `Contains` in pass 1
  and the row is highlighted and a pass-order warning appears. The design
  requires the UI to warn, not merely to allow.
- **Activation is gated on the universal roles.** Select the JoPACC summary
  dataset (no fields mapped yet) and the panel says which roles are missing and
  why the definition is blocked.
- **`MatchDistributionDrift` is shown, not just stored.** Select run 4469 on the
  dashboard to see the last-pass share cross the threshold.
- **A `Rematch` is distinguishable from the run it supersedes.** Run 4469 is a
  Rematch of 4468; the dashboard shows the link, and 4468 is marked superseded.

## Testing

Two suites, both run against the real files:

```bash
node test-ct.js    # 20 unit tests for the condition-tree validator
node drive.js      # drives all 6 pages in Chromium: interactions,
                   # console errors, and 390px phone width
```

`drive.js` fails the build on any console error, any unrendered table body, and
any horizontal overflow at phone width.

## Not yet built

There is no server. The screens read `js/mock-data.js`, and the buttons that
would write (`Save rule set`, `Export`) say what would happen instead of doing
it. The .NET side is Phase 1 work and the SDK is not installable in this
environment — see the root `README.md`.
