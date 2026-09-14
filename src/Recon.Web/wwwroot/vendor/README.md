# Vendored front-end libraries

Pinned, committed, and served from this folder — the portal loads no CDN at
runtime. A reconciliation portal inside a bank's network cannot depend on an
outbound request to a third party to render its own login page.

| File | Library | Version | License |
|------|---------|---------|---------|
| `jquery.min.js` | jQuery | 3.7.1 | MIT |
| `bootstrap.min.css`, `bootstrap.bundle.min.js` | Bootstrap | 5.3.3 | MIT |
| `bootstrap-icons.css`, `fonts/` | Bootstrap Icons | 1.11.3 | MIT |

To upgrade: `npm install bootstrap@<v> jquery@<v> bootstrap-icons@<v>`, then copy
`dist/` files here and update this table.
