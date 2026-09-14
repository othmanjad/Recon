# Superseded schema scripts

These two files are kept for history only. **Do not run them.**

| File | What it was |
|------|-------------|
| `recon-platform-ddl-v01.sql` | The original base schema (v0.1) |
| `recon-platform-ddl-v02-delta.sql` | The v0.3 review fixes, expressed as a delta over v0.1 |

The delta form was deliberate: it kept every change visible for review while
nothing was deployed. Once the v1.0 review read the base and the delta *as one
artifact* — which is what a developer deploys — keeping two files stopped being
a review aid and started being two sources of truth. Five of the six v1.0
findings were only visible when reading them together.

The live schema is `db/01-schema.sql` and the three scripts beside it.

Two things in these files are actively wrong and are the reason they must not be
run:

- `recon-platform-ddl-v01.sql` carries the comment *"bulk load into the
  heap-like structure first, then build indexes — not the reverse."* That is
  blocker A1: impossible on a single shared table with a permanent clustered
  index. The design document was corrected; this comment never was.
- Its header states *"All monetary comparison happens on BIGINT fils"*, which
  finding C4 replaced with configurable minor units per currency.
