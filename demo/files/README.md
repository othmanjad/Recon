# Demo files

Two CSVs for one CliQ session, built so that **every outcome the engine can
produce actually occurs**. A demo where everything matches proves nothing.

| Row | What it exercises |
|-----|-------------------|
| `E2E-...000001` .. `000003`, `000010` .. `000014`, `000016` | Clean pairs. Pass 1, matched on reference |
| `e2e-20260913 000004` | Same transaction, spelled with a space and lower case on the CliQ side. Pass 1 misses it; **pass 2** pairs it via the parse-time normalized companion |
| `000005` | A clean outward pair |
| `000006` | Status `RJCT`. **Excluded** before pass 1 and reported — never an exception, because a rejected transaction is not a break |
| `000007` twice | The declared duplicate key appears twice in one file. The first matches; the second is flagged **`DUPLICATE_IN_SOURCE`** and never enters matching |
| `000009` | Matches, then contributes to the outward control total |
| `TXN-9915` ↔ `OM-77812004501` | References differ entirely, but the CliQ `TXN-9915` equals OM's `ExternalRef`. **Pass 3** pairs it on the secondary reference |
| `000017` | `Amount` is `not-a-number`. A **parse error**: the row is rejected and written to `stg.ParseError` with its line, field, reason and raw line — and the rest of the file still loads |
| `000018` | Posted just before midnight on the 12th and settled on the 13th. Only inside the **±1-day matching window** (finding C5) |
| `OM-77812004502` | In OM, no CliQ counterpart. Classified **`MISSING_IN_CLIQ`** |
| `000019`, `000020` | In CliQ, no OM counterpart. Classified **`FAILED_INWARD`** and **`FAILED_OUTWARD`** by direction — detect and report only, with no automatic posting in this phase |

Amounts are decimals in the file and integers in minor units everywhere after
parsing: `125.500` JOD becomes `125500`. The accounts are deliberately written
`0079-012-345` on one side and `0079012345` on the other, so the
`StripNonAlphanumeric` transform in the mapping has something to do.
