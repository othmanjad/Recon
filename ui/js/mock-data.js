/* =====================================================================
   Mock data for the portal prototype.

   Embedded as a plain script (not fetched) so every page opens straight
   from the filesystem with no server and no CORS problem.

   Shapes mirror db/01-schema.sql exactly, including the naming the v1.0
   review fixed: amounts are integer MINOR UNITS (never decimals), the
   field registry carries roles and slots, and a run records both
   loadRunId and resultRunId.
   ===================================================================== */

window.ReconMock = (function () {
    "use strict";

    var currencies = {
        JOD: { code: "JOD", name: "Jordanian Dinar", minorUnits: 3 },
        USD: { code: "USD", name: "US Dollar", minorUnits: 2 }
    };

    var counterparties = [
        { id: 1, code: "JOPACC", name: "Jordan Payments & Clearing Company", datasets: 3, definitions: 2, active: true },
        { id: 2, code: "BANK_X", name: "Bank X", datasets: 1, definitions: 1, active: true },
        { id: 3, code: "PARTNER_Z", name: "Remittance Partner Z", datasets: 0, definitions: 0, active: false }
    ];

    /* cfg.StorageSlotCatalogue — two pools. Text21..Text30 are companion
       slots for parse-time normalization and are NEVER offered as a
       field's own StorageSlot (v1.0 finding 2). */
    var slots = (function () {
        var out = [];
        var i;
        for (i = 1; i <= 30; i++) {
            out.push({ name: "Text" + i, type: "String", maxLength: 300, normalizedOnly: i > 20 });
        }
        for (i = 1; i <= 15; i++) { out.push({ name: "Num" + i, type: "Integer", normalizedOnly: false }); }
        for (i = 1; i <= 5; i++) { out.push({ name: "Dec" + i, type: "Decimal", normalizedOnly: false }); }
        for (i = 1; i <= 8; i++) { out.push({ name: "Date" + i, type: "DateTime", normalizedOnly: false }); }
        for (i = 1; i <= 5; i++) { out.push({ name: "Flag" + i, type: "Boolean", normalizedOnly: false }); }
        return out;
    }());

    var datasets = [
        {
            id: 1, counterpartyId: 1, code: "CLIQ_SESSION", name: "CliQ Session File",
            provider: "File", format: "Csv", currency: "JOD", timeZone: "Asia/Amman",
            active: true, duplicateKeyFields: "REF_PRIMARY",
            fields: [
                { id: 11, code: "REF_PRIMARY", label: "End To End Id", type: "String", role: "Reference", slot: "Text1", matchable: true, indexed: true, required: true, normalize: true, normalizedSlot: "Text21" },
                { id: 12, code: "REF_TXN", label: "Transaction Id", type: "String", role: "Reference", slot: "Text2", matchable: true, indexed: true, required: true, normalize: false, normalizedSlot: null },
                { id: 13, code: "ORIG_REF", label: "Original Reference", type: "String", role: "OriginalReference", slot: "Text3", matchable: true, indexed: false, required: false, normalize: false, normalizedSlot: null },
                { id: 14, code: "CRDTR_ACCT", label: "Creditor Account", type: "String", role: "Party", slot: "Text4", matchable: true, indexed: false, required: false, normalize: true, normalizedSlot: "Text22" },
                { id: 15, code: "AMOUNT", label: "Amount", type: "Integer", role: "Amount", slot: "Num1", matchable: true, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 16, code: "CURRENCY", label: "Currency", type: "String", role: "Currency", slot: "Text5", matchable: false, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 17, code: "TX_DATETIME", label: "Transaction Date Time", type: "DateTime", role: "Date", slot: "Date1", matchable: true, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 18, code: "DIRECTION", label: "Direction", type: "String", role: "Direction", slot: "Text6", matchable: true, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 19, code: "STATUS", label: "Status", type: "String", role: "Status", slot: "Text7", matchable: true, indexed: false, required: false, normalize: false, normalizedSlot: null }
            ]
        },
        {
            id: 2, counterpartyId: 1, code: "OM_TXN", name: "Orange Money Transactions",
            provider: "Sql", format: "View", currency: "JOD", timeZone: "Asia/Amman",
            active: true, duplicateKeyFields: null,
            fields: [
                { id: 21, code: "OM_REF", label: "OM Reference", type: "String", role: "Reference", slot: "Text1", matchable: true, indexed: true, required: true, normalize: true, normalizedSlot: "Text21" },
                { id: 22, code: "EXT_REF", label: "External Reference", type: "String", role: "Reference", slot: "Text2", matchable: true, indexed: true, required: false, normalize: false, normalizedSlot: null },
                { id: 23, code: "ACCOUNT", label: "Account", type: "String", role: "Party", slot: "Text3", matchable: true, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 24, code: "AMOUNT_MINOR", label: "Amount", type: "Integer", role: "Amount", slot: "Num1", matchable: true, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 25, code: "CURRENCY", label: "Currency", type: "String", role: "Currency", slot: "Text4", matchable: false, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 26, code: "POSTED_AT", label: "Posted At", type: "DateTime", role: "Date", slot: "Date1", matchable: true, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 27, code: "DIRECTION", label: "Direction", type: "String", role: "Direction", slot: "Text5", matchable: true, indexed: false, required: true, normalize: false, normalizedSlot: null },
                { id: 28, code: "IS_REVERSED", label: "Is Reversed", type: "Boolean", role: "Status", slot: "Flag1", matchable: true, indexed: false, required: false, normalize: false, normalizedSlot: null }
            ]
        },
        {
            id: 3, counterpartyId: 1, code: "JOPACC_SUMMARY", name: "JoPACC Session Summary",
            provider: "Api", format: "Json", currency: "JOD", timeZone: "Asia/Amman",
            active: true, duplicateKeyFields: null, fields: []
        },
        {
            id: 4, counterpartyId: 2, code: "BANKX_STMT", name: "Bank X Statement",
            provider: "File", format: "Csv", currency: "USD", timeZone: "Asia/Amman",
            active: false, duplicateKeyFields: null, fields: []
        }
    ];

    var definitions = [
        { id: 1, code: "CLIQ_OM_INWARD", name: "CliQ ↔ OM (inward & outward)",
          counterpartyId: 1, leftDatasetId: 1, rightDatasetId: 2,
          windowBefore: 1, windowAfter: 1, active: true },
        { id: 2, code: "CLIQ_OFFUS", name: "OFF-US ↔ CliQ (outbound)",
          counterpartyId: 1, leftDatasetId: 1, rightDatasetId: 2,
          windowBefore: 1, windowAfter: 1, active: true },
        { id: 3, code: "BANKX_RECON", name: "Bank X statement ↔ OM",
          counterpartyId: 2, leftDatasetId: 4, rightDatasetId: 2,
          windowBefore: 2, windowAfter: 2, active: false }
    ];

    /* The ordered passes of definition 1, exactly as design §9.3 lays them
       out. Pass 2 compares the NORMALIZED companion field with 'Exact' —
       there is no 'Normalized' comparison type at runtime (A2). */
    var rules = [
        { id: 1, seq: 1, code: "P1_REF", name: "Primary reference",
          mode: "Row", cardinality: "OneToOne", onMultiple: "MarkAmbiguous",
          conditions: [ { leftFieldId: 11, rightFieldId: 21, cmp: "Exact" } ] },
        { id: 2, seq: 2, code: "P2_REF_NORM", name: "Primary reference (normalized companion)",
          mode: "Row", cardinality: "OneToOne", onMultiple: "MarkAmbiguous",
          conditions: [ { leftFieldId: 11, rightFieldId: 21, cmp: "Exact", useNormalized: true } ] },
        { id: 3, seq: 3, code: "P3_SEC_REF", name: "Secondary reference",
          mode: "Row", cardinality: "OneToOne", onMultiple: "MarkAmbiguous",
          conditions: [ { leftFieldId: 12, rightFieldId: 22, cmp: "Exact" } ] },
        { id: 4, seq: 4, code: "P4_COMPOSITE", name: "Amount + date + account",
          mode: "Row", cardinality: "OneToOne", onMultiple: "MarkAmbiguous",
          conditions: [
              { leftFieldId: 15, rightFieldId: 24, cmp: "NumericExact" },
              { leftFieldId: 17, rightFieldId: 26, cmp: "DateWithin", tolerance: 1, unit: "Day" },
              { leftFieldId: 14, rightFieldId: 23, cmp: "Exact" }
          ] }
    ];

    /* Run 4471 — the run the design keeps using as its example. */
    var runs = [
        { id: 4471, definitionId: 1, businessDate: "2026-09-13", sessionRef: "S3",
          type: "Scheduled", status: "Completed", isCurrent: true, sourceRunId: null,
          stagingRunId: 4471, startedAt: "2026-09-13 22:14:03", durationSec: 512,
          leftRowCnt: 1984120, rightRowCnt: 1985004, matchedCnt: 1979633,
          unmatchedCnt: 5371, ambiguousCnt: 118, currency: "JOD",
          passes: [
              { seq: 1, code: "P1_REF", rows: 1984120, matched: 1961004, ms: 41200 },
              { seq: 2, code: "P2_REF_NORM", rows: 23116, matched: 14882, ms: 9100 },
              { seq: 3, code: "P3_SEC_REF", rows: 8234, matched: 3402, ms: 6400 },
              { seq: 4, code: "P4_COMPOSITE", rows: 4832, matched: 345, ms: 21800 }
          ],
          controlTotals: [
              { code: "DEBIT_COUNT", name: "Debit count", a: 921044, b: 921044, balanced: true },
              { code: "DEBIT_TOTAL", name: "Debit total", a: 418923114000, b: 418923114000, balanced: true },
              { code: "CREDIT_COUNT", name: "Credit count", a: 1063076, b: 1063076, balanced: true },
              { code: "CREDIT_TOTAL", name: "Credit total", a: 502117884500, b: 502117884500, balanced: true },
              { code: "OM_VS_CLIQ", name: "OM vs CliQ totals", a: 921041044000, b: 921041044000, balanced: true }
          ] },
        { id: 4470, definitionId: 1, businessDate: "2026-09-13", sessionRef: "S2",
          type: "Scheduled", status: "Completed", isCurrent: true, sourceRunId: null,
          stagingRunId: 4470, startedAt: "2026-09-13 16:05:11", durationSec: 498,
          leftRowCnt: 1120884, rightRowCnt: 1120884, matchedCnt: 1120884,
          unmatchedCnt: 0, ambiguousCnt: 0, currency: "JOD",
          passes: [
              { seq: 1, code: "P1_REF", rows: 1120884, matched: 1120884, ms: 33900 },
              { seq: 2, code: "P2_REF_NORM", rows: 0, matched: 0, ms: 120 },
              { seq: 3, code: "P3_SEC_REF", rows: 0, matched: 0, ms: 90 },
              { seq: 4, code: "P4_COMPOSITE", rows: 0, matched: 0, ms: 80 }
          ],
          controlTotals: [] },
        /* A Rematch: reuses run 4468's staged rows, writes its own results.
           stagingRunId is what every query must read — the whole point of
           v1.0 finding 1. */
        { id: 4469, definitionId: 1, businessDate: "2026-09-12", sessionRef: "S3",
          type: "Rematch", status: "Completed", isCurrent: true, sourceRunId: 4468,
          stagingRunId: 4468, startedAt: "2026-09-13 09:41:55", durationSec: 186,
          leftRowCnt: 1902551, rightRowCnt: 1903002, matchedCnt: 1900884,
          unmatchedCnt: 1667, ambiguousCnt: 451, currency: "JOD",
          passes: [
              { seq: 1, code: "P1_REF", rows: 1902551, matched: 1881402, ms: 38800 },
              { seq: 2, code: "P2_REF_NORM", rows: 21149, matched: 18004, ms: 8200 },
              { seq: 3, code: "P3_SEC_REF", rows: 3145, matched: 1204, ms: 3900 },
              { seq: 4, code: "P4_COMPOSITE", rows: 1941, matched: 274, ms: 12400 }
          ],
          controlTotals: [] },
        { id: 4468, definitionId: 1, businessDate: "2026-09-12", sessionRef: "S3",
          type: "Scheduled", status: "Completed", isCurrent: false, sourceRunId: null,
          stagingRunId: 4468, startedAt: "2026-09-12 22:12:40", durationSec: 505,
          leftRowCnt: 1902551, rightRowCnt: 1903002, matchedCnt: 1894120,
          unmatchedCnt: 8431, ambiguousCnt: 902, currency: "JOD",
          passes: [], controlTotals: [] },
        { id: 4467, definitionId: 2, businessDate: "2026-09-13", sessionRef: null,
          type: "Scheduled", status: "Failed", isCurrent: true, sourceRunId: null,
          stagingRunId: 4467, startedAt: "2026-09-13 23:02:00", durationSec: 74,
          leftRowCnt: 48221, rightRowCnt: null, matchedCnt: null,
          unmatchedCnt: null, ambiguousCnt: null, currency: "JOD",
          error: "ControlTotalMismatch: CREDIT_TOTAL differs by 4 120 minor units",
          passes: [],
          controlTotals: [
              { code: "CREDIT_TOTAL", name: "Credit total", a: 88214120000, b: 88214115880, balanced: false }
          ] },
        { id: 4466, definitionId: 1, businessDate: "2026-09-14", sessionRef: "S1",
          type: "Scheduled", status: "Running", isCurrent: true, sourceRunId: null,
          stagingRunId: 4466, startedAt: "2026-09-14 06:02:11", durationSec: null,
          leftRowCnt: 640112, rightRowCnt: null, matchedCnt: null,
          unmatchedCnt: null, ambiguousCnt: null, currency: "JOD",
          passes: [], controlTotals: [] }
    ];

    var exceptions = [
        { id: 90114, runId: 4471, definitionId: 1, businessDate: "2026-09-13", side: "Left",
          code: "FAILED_INWARD", amountMinor: 125500, currency: "JOD", status: "Open",
          assignedTo: null, ref: "E2E-20260913-0099412" },
        { id: 90115, runId: 4471, definitionId: 1, businessDate: "2026-09-13", side: "Right",
          code: "MISSING_IN_CLIQ", amountMinor: 48000, currency: "JOD", status: "InProgress",
          assignedTo: "ops.hala", ref: "OM-778120044" },
        { id: 90116, runId: 4471, definitionId: 1, businessDate: "2026-09-13", side: "Left",
          code: "AMBIGUOUS", amountMinor: 200000, currency: "JOD", status: "Open",
          assignedTo: null, ref: "E2E-20260913-0100004" },
        { id: 90117, runId: 4471, definitionId: 1, businessDate: "2026-09-13", side: "Right",
          code: "NOT_REVERSED_NO_RECORD", amountMinor: 1500000, currency: "JOD", status: "Open",
          assignedTo: null, ref: "OM-778120981" },
        { id: 90090, runId: 4469, definitionId: 1, businessDate: "2026-09-12", side: "Left",
          code: "FAILED_INWARD", amountMinor: 75250, currency: "JOD", status: "AutoClosed",
          assignedTo: null, ref: "E2E-20260912-0088120" },
        { id: 90091, runId: 4469, definitionId: 1, businessDate: "2026-09-12", side: "Left",
          code: "AMOUNT_DIFFERENCE", amountMinor: 4120, currency: "JOD", status: "Resolved",
          assignedTo: "ops.hala", ref: "E2E-20260912-0088977" },
        { id: 89902, runId: 4460, definitionId: 1, businessDate: "2026-09-05", side: "Right",
          code: "MISSING_IN_CLIQ", amountMinor: 320000, currency: "JOD", status: "Open",
          assignedTo: "ops.khalid", ref: "OM-771004532" }
    ];

    var exceptionCodes = [
        { code: "FAILED_INWARD", label: "In CliQ, not in OM", severity: "High" },
        { code: "MISSING_IN_CLIQ", label: "In OM, not in CliQ", severity: "High" },
        { code: "REVERSED_WITH_RECORD", label: "Reversed in OM, CliQ record exists", severity: "Normal" },
        { code: "NOT_REVERSED_NO_RECORD", label: "Not reversed, no CliQ record", severity: "High" },
        { code: "AMOUNT_DIFFERENCE", label: "Matched, amounts differ", severity: "Critical" },
        { code: "AMBIGUOUS", label: "Multiple candidates", severity: "Normal" },
        { code: "DUPLICATE_IN_SOURCE", label: "Duplicate of the declared key in one file", severity: "Normal" }
    ];

    /* Comparison types offered in the rule builder, with the indexability
       the UI must surface (design §9.1). 'Normalized' is deliberately
       absent — it is a parse-time transform, not a runtime comparison. */
    var comparisons = [
        { value: "Exact", label: "Exact — literal equality", indexable: "yes", needsTolerance: false },
        { value: "NumericExact", label: "Numeric exact — minor-unit equality", indexable: "yes", needsTolerance: false },
        { value: "NumericTolerance", label: "Numeric tolerance — ± N minor units", indexable: "yes", needsTolerance: true },
        { value: "DateExact", label: "Date exact — same calendar date", indexable: "yes", needsTolerance: false },
        { value: "DateWithin", label: "Date within — ± N minutes / hours / days", indexable: "yes", needsTolerance: true },
        { value: "StartsWith", label: "Starts with — prefix match", indexable: "partly", needsTolerance: false },
        { value: "EndsWith", label: "Ends with — partial match", indexable: "no", needsTolerance: false },
        { value: "Contains", label: "Contains — partial match", indexable: "no", needsTolerance: false }
    ];

    var settings = [
        { key: "StagingMonthsOnline", value: "3", type: "Int", desc: "Months of staging kept online. The single largest cost in the system." },
        { key: "ResultsMonthsOnline", value: "84", type: "Int", desc: "OPEN — confirm the regulatory retention period. 84 is a placeholder." },
        { key: "SandboxPurgeDays", value: "7", type: "Int", desc: "Sandbox runs purged after this many days." },
        { key: "FuturePartitionsMin", value: "3", type: "Int", desc: "Future monthly partitions kept ahead." },
        { key: "MaxIndexesPerDataset", value: "4", type: "Int", desc: "Cap on generated matching indexes per dataset." },
        { key: "ExcelMaxRowsPerSheet", value: "1000000", type: "Int", desc: "Sheet split threshold; a worksheet holds 1 048 576 rows." },
        { key: "MatchDriftThresholdPct", value: "15", type: "Decimal", desc: "Raise MatchDistributionDrift above this last-pass share." },
        { key: "BulkCopyBatchSize", value: "100000", type: "Int", desc: "SqlBulkCopy BatchSize; rows arrive sorted on the clustered key." }
    ];

    function fieldById(id) {
        var i, j;
        for (i = 0; i < datasets.length; i++) {
            for (j = 0; j < datasets[i].fields.length; j++) {
                if (datasets[i].fields[j].id === id) { return datasets[i].fields[j]; }
            }
        }
        return null;
    }

    function datasetById(id) {
        for (var i = 0; i < datasets.length; i++) {
            if (datasets[i].id === id) { return datasets[i]; }
        }
        return null;
    }

    function definitionById(id) {
        for (var i = 0; i < definitions.length; i++) {
            if (definitions[i].id === id) { return definitions[i]; }
        }
        return null;
    }

    function runById(id) {
        for (var i = 0; i < runs.length; i++) {
            if (runs[i].id === id) { return runs[i]; }
        }
        return null;
    }

    return {
        currencies: currencies,
        counterparties: counterparties,
        slots: slots,
        datasets: datasets,
        definitions: definitions,
        rules: rules,
        runs: runs,
        exceptions: exceptions,
        exceptionCodes: exceptionCodes,
        comparisons: comparisons,
        settings: settings,
        fieldById: fieldById,
        datasetById: datasetById,
        definitionById: definitionById,
        runById: runById
    };
}());
