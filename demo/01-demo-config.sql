/* =====================================================================
   DEMO CONFIGURATION — a complete, working CliQ ↔ OM reconciliation.

   Run after db/01-schema.sql .. 04-database-options.sql. Everything here
   is ordinary configuration: no code knows about CliQ, and the same rows
   for a different counterparty would be a different reconciliation.

   This is also the shape of what Operations would create from the portal
   in the onboarding sequence (design §2.1).
   ===================================================================== */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* Re-runnable: drop whatever a previous demo left behind.

   The order is the foreign keys' order, child before parent, and every
   subselect finds its parent by code — so a parent deleted early both
   fails on its own constraint and orphans everything below it. Pressing
   "Load the demo configuration" a second time is what proves this block,
   and it is the only thing that does. */
DELETE ops.RunAggregate WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE ops.ControlTotalResult WHERE ControlTotalId IN
    (SELECT ControlTotalId FROM cfg.ControlTotalDefinition WHERE DefinitionId IN
        (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM'));
DELETE ops.ReconException WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE ops.MatchResult WHERE RunId IN
    (SELECT RunId FROM ops.ReconRun WHERE DefinitionId IN
        (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM'));
DELETE ops.ReconRunStep WHERE RunId IN
    (SELECT RunId FROM ops.ReconRun WHERE DefinitionId IN
        (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM'));
DELETE ops.InterchangeSummary WHERE RunId IN
    (SELECT RunId FROM ops.ReconRun WHERE DefinitionId IN
        (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM'));
DELETE stg.ParseError WHERE RunId IN
    (SELECT RunId FROM ops.ReconRun WHERE DefinitionId IN
        (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM'));
DELETE ops.ReconRun WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE stg.StagingTransaction WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE cfg.MatchCondition WHERE MatchRuleId IN
    (SELECT MatchRuleId FROM cfg.MatchRule WHERE DefinitionId IN
        (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM'));
DELETE cfg.MatchRule WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE cfg.ControlTotalDefinition WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE cfg.ClassificationRule WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE cfg.ReportColumn WHERE ReportDefinitionId IN
    (SELECT ReportDefinitionId FROM cfg.ReportDefinition WHERE DefinitionId IN
        (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM'));
DELETE cfg.ReportDefinition WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE cfg.ScheduleDefinition WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DELETE cfg.AlertPolicy WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
/* LAST of the definition's rows, not first: eight tables reference
   DefinitionId, and deleting the definition ahead of them both failed on
   the foreign key and left the rows below unreachable, because every
   subselect here finds the definition by its code. A second "Load the
   demo configuration" answered 500. */
DELETE cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM';
DELETE cfg.FeeApplicability WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE cfg.FeeTier WHERE FeeScheduleId IN
    (SELECT FeeScheduleId FROM cfg.FeeSchedule WHERE CounterpartyId IN
        (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'JOPACC'));
DELETE cfg.FeeSchedule WHERE CounterpartyId IN
    (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'JOPACC');
DELETE cfg.UserCounterpartyAccess WHERE CounterpartyId IN
    (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'JOPACC');
DELETE cfg.ExclusionRule WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE cfg.FieldMapping WHERE FileFormatId IN
    (SELECT FileFormatId FROM cfg.FileFormatDefinition WHERE DatasetId IN
        (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN')));
DELETE cfg.FileFormatDefinition WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE cfg.DatasetField WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE ops.SourceFile WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE cfg.AcquisitionDefinition WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE cfg.SqlSourceDefinition WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN'));
DELETE cfg.Dataset WHERE Code IN ('CLIQ_SESSION','OM_TXN');
DELETE cfg.Counterparty WHERE Code = 'JOPACC';
GO


/* ---------------------------------------------------------------------
   1. The counterparty
   --------------------------------------------------------------------- */
INSERT cfg.Counterparty (Code, Name, Description, CreatedBy)
VALUES ('JOPACC', N'Jordan Payments & Clearing Company',
        N'CliQ instant payments. The first configured reconciliation, not the product.', 'demo');
GO

DECLARE @cp INT = (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'JOPACC');

/* ---------------------------------------------------------------------
   2. Two datasets. Both are File providers here so the demo runs from
      CSVs; in production the OM side is a SQL view or procedure, and
      nothing else in the configuration changes.
   --------------------------------------------------------------------- */
INSERT cfg.Dataset
    (CounterpartyId, Code, Name, ProviderType, DefaultCurrency, DuplicateKeyFields, IsActive, CreatedBy)
VALUES
    (@cp, 'CLIQ_SESSION', N'CliQ Session File', 'File', 'JOD', 'REF_PRIMARY', 1, 'demo'),
    (@cp, 'OM_TXN',       N'Orange Money Transactions', 'File', 'JOD', NULL, 1, 'demo');
GO

DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');
DECLARE @right INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'OM_TXN');

/* ---------------------------------------------------------------------
   3. The field registries.

   The two sides name nothing alike — REF_PRIMARY vs OM_REF, AMOUNT vs
   AMOUNT_MINOR — and the engine does not care. It reads FieldRole: it
   looks for the field whose role is Amount, never for a field called
   "Amount" (§6).

   CURRENCY is deliberately NOT matchable. It is context, not a matching
   key, and the rule builder will not offer it.
   --------------------------------------------------------------------- */
INSERT cfg.DatasetField
    (DatasetId, FieldCode, DisplayLabel, DataType, FieldRole, StorageSlot,
     IsMatchable, IsIndexed, IsRequired, NormalizeForMatch, NormalizedSlot, DisplayOrder)
VALUES
    (@left, 'REF_PRIMARY', N'End To End Id',        'String',   'Reference',         'Text1', 1, 1, 1, 1, 'Text21', 1),
    (@left, 'REF_TXN',     N'Transaction Id',       'String',   'Reference',         'Text2', 1, 1, 0, 0, NULL,     2),
    (@left, 'ORIG_REF',    N'Original Reference',   'String',   'OriginalReference', 'Text3', 1, 0, 0, 0, NULL,     3),
    (@left, 'CRDTR_ACCT',  N'Creditor Account',     'String',   'Party',             'Text4', 1, 0, 0, 1, 'Text22', 4),
    (@left, 'AMOUNT',      N'Amount',               'Integer',  'Amount',            'Num1',  1, 0, 1, 0, NULL,     5),
    (@left, 'CURRENCY',    N'Currency',             'String',   'Currency',          'Text5', 0, 0, 1, 0, NULL,     6),
    (@left, 'TX_DATETIME', N'Transaction Date Time','DateTime', 'Date',              'Date1', 1, 0, 1, 0, NULL,     7),
    (@left, 'DIRECTION',   N'Direction',            'String',   'Direction',         'Text6', 1, 0, 1, 0, NULL,     8),
    (@left, 'STATUS',      N'Status',               'String',   'Status',            'Text7', 1, 0, 0, 0, NULL,     9);

INSERT cfg.DatasetField
    (DatasetId, FieldCode, DisplayLabel, DataType, FieldRole, StorageSlot,
     IsMatchable, IsIndexed, IsRequired, NormalizeForMatch, NormalizedSlot, DisplayOrder)
VALUES
    (@right, 'OM_REF',       N'OM Reference',       'String',   'Reference', 'Text1', 1, 1, 1, 1, 'Text21', 1),
    (@right, 'EXT_REF',      N'External Reference', 'String',   'Reference', 'Text2', 1, 1, 0, 0, NULL,     2),
    (@right, 'ACCOUNT',      N'Account',            'String',   'Party',     'Text3', 1, 0, 1, 1, 'Text22', 3),
    (@right, 'AMOUNT_MINOR', N'Amount',             'Integer',  'Amount',    'Num1',  1, 0, 1, 0, NULL,     4),
    (@right, 'CURRENCY',     N'Currency',           'String',   'Currency',  'Text5', 0, 0, 1, 0, NULL,     5),
    (@right, 'POSTED_AT',    N'Posted At',          'DateTime', 'Date',      'Date1', 1, 0, 1, 0, NULL,     6),
    (@right, 'DIRECTION',    N'Direction',          'String',   'Direction', 'Text6', 1, 0, 1, 0, NULL,     7),
    (@right, 'STATUS',       N'Status',             'String',   'Status',    'Text7', 1, 0, 0, 0, NULL,     8);
GO


/* ---------------------------------------------------------------------
   4. File formats, effective-dated.

   The version is what lets a 2026 file still be read correctly after the
   layout changes in 2027 (§8). MaxParseErrors caps the damage a
   wrong-format file can do (E4).
   --------------------------------------------------------------------- */
DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');
DECLARE @right INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'OM_TXN');

INSERT cfg.FileFormatDefinition
    (DatasetId, FormatType, Version, EffectiveFrom, Delimiter, TextQualifier,
     Encoding, HasHeader, SkipLeadingLines, SkipTrailingLines, FileNamePattern, MaxParseErrors)
VALUES
    (@left,  'Csv', 1, '2026-01-01', ',', '"', 'UTF-8', 1, 0, 0, N'^CLIQ_SESSION_\d{8}_S\d\.csv$', 1000),
    (@right, 'Csv', 1, '2026-01-01', ',', '"', 'UTF-8', 1, 0, 0, N'^OM_TXN_\d{8}\.csv$', 1000);
GO


/* ---------------------------------------------------------------------
   5. Field mappings: source column -> registry field.

   A new column from the partner is one new row here. CSV becoming XML is
   the same rows with SourcePath as an XPath. No engine change either
   way (§8).

   The transform chains are where formatting noise is dealt with, once
   per value at load, rather than inside a join predicate where it would
   defeat every index (blocker A2).
   --------------------------------------------------------------------- */
DECLARE @lf INT = (SELECT FileFormatId FROM cfg.FileFormatDefinition
                   WHERE DatasetId = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION'));
DECLARE @rf INT = (SELECT FileFormatId FROM cfg.FileFormatDefinition
                   WHERE DatasetId = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'OM_TXN'));

INSERT cfg.FieldMapping (FileFormatId, DatasetFieldId, SourcePath, ParseFormat, TransformChainJson, IsRequired)
SELECT @lf, f.DatasetFieldId, v.SourcePath, v.ParseFormat, v.Transforms, v.IsRequired
FROM (VALUES
    ('REF_PRIMARY', 'EndToEndId',  NULL,             N'[{"op":"Trim"},{"op":"Upper"}]', CAST(1 AS BIT)),
    ('REF_TXN',     'TxnId',       NULL,             N'[{"op":"Trim"}]',                CAST(0 AS BIT)),
    ('ORIG_REF',    'OriginalRef', NULL,             N'[{"op":"Trim"}]',                CAST(0 AS BIT)),
    ('CRDTR_ACCT',  'CreditorAcct',NULL,             N'[{"op":"Trim"},{"op":"StripNonAlphanumeric"}]', CAST(0 AS BIT)),
    ('AMOUNT',      'Amount',      NULL,             NULL,                              CAST(1 AS BIT)),
    ('CURRENCY',    'Currency',    NULL,             N'[{"op":"Trim"},{"op":"Upper"}]', CAST(1 AS BIT)),
    ('TX_DATETIME', 'TxnDateTime', 'yyyyMMddHHmmss', NULL,                              CAST(1 AS BIT)),
    ('DIRECTION',   'Direction',   NULL,             N'[{"op":"Trim"}]',                CAST(1 AS BIT)),
    ('STATUS',      'Status',      NULL,             N'[{"op":"Trim"},{"op":"Upper"}]', CAST(0 AS BIT))
) AS v(FieldCode, SourcePath, ParseFormat, Transforms, IsRequired)
JOIN cfg.DatasetField AS f
    ON f.FieldCode = v.FieldCode
   AND f.DatasetId = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');

INSERT cfg.FieldMapping (FileFormatId, DatasetFieldId, SourcePath, ParseFormat, TransformChainJson, IsRequired)
SELECT @rf, f.DatasetFieldId, v.SourcePath, v.ParseFormat, v.Transforms, v.IsRequired
FROM (VALUES
    ('OM_REF',       'Reference',   NULL,                  N'[{"op":"Trim"},{"op":"Upper"}]', CAST(1 AS BIT)),
    ('EXT_REF',      'ExternalRef', NULL,                  N'[{"op":"Trim"}]',                CAST(0 AS BIT)),
    ('ACCOUNT',      'Account',     NULL,                  N'[{"op":"Trim"},{"op":"StripNonAlphanumeric"}]', CAST(1 AS BIT)),
    ('AMOUNT_MINOR', 'Amount',      NULL,                  NULL,                              CAST(1 AS BIT)),
    ('CURRENCY',     'Currency',    NULL,                  N'[{"op":"Trim"},{"op":"Upper"}]', CAST(1 AS BIT)),
    ('POSTED_AT',    'PostedAt',    'yyyy-MM-dd HH:mm:ss', NULL,                              CAST(1 AS BIT)),
    ('DIRECTION',    'Direction',   NULL,                  N'[{"op":"Trim"}]',                CAST(1 AS BIT)),
    ('STATUS',       'Status',      NULL,                  N'[{"op":"Trim"},{"op":"Upper"}]', CAST(0 AS BIT))
) AS v(FieldCode, SourcePath, ParseFormat, Transforms, IsRequired)
JOIN cfg.DatasetField AS f
    ON f.FieldCode = v.FieldCode
   AND f.DatasetId = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'OM_TXN');
GO

/* ---------------------------------------------------------------------
   6. Exclusion: rejected transactions leave the working set before pass 1
      and are reported, never raised as exceptions (finding C6). A
      rejected transaction is not a break.
   --------------------------------------------------------------------- */
INSERT cfg.ExclusionRule (DatasetId, Name, ConditionJson, ReasonCode)
SELECT DatasetId, N'Rejected by the scheme',
       N'{"op":"and","items":[{"field":"STATUS","cmp":"eq","value":"RJCT"}]}',
       'REJECTED'
FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION';
GO


/* ---------------------------------------------------------------------
   7. The reconciliation definition and its ordered passes (§9.3).
   --------------------------------------------------------------------- */
DECLARE @cp INT = (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'JOPACC');
DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');
DECLARE @right INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'OM_TXN');

INSERT cfg.ReconciliationDefinition
    (CounterpartyId, Code, Name, LeftDatasetId, RightDatasetId,
     MatchingWindowDaysBefore, MatchingWindowDaysAfter, IsActive, CreatedBy)
VALUES (@cp, 'CLIQ_OM', N'CliQ session to Orange Money', @left, @right, 1, 1, 1, 'demo');
GO

DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');

INSERT cfg.MatchRule (DefinitionId, RuleCode, Name, Sequence, OnMultipleMatch)
VALUES
    (@def, 'P1_REF',       N'Primary reference',                       1, 'MarkAmbiguous'),
    (@def, 'P2_REF_NORM',  N'Primary reference (normalized companion)',2, 'MarkAmbiguous'),
    (@def, 'P3_SEC_REF',   N'Secondary reference',                     3, 'MarkAmbiguous'),
    (@def, 'P4_COMPOSITE', N'Amount + date + account',                 4, 'MarkAmbiguous');
GO

/* The field pairs. Note P2: the SAME pair as P1, but UseNormalized = 1 so
   it compares the parse-time companions — an indexed Exact match over
   whatever P1 could not pair (blocker A2). That flag is the rule's
   intent and has to be stated; inferring it from the fields would make
   P1 a normalized pass too. */
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');
DECLARE @right INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'OM_TXN');

INSERT cfg.MatchCondition
    (MatchRuleId, LeftFieldId, RightFieldId, ComparisonType,
     ToleranceValue, ToleranceUnit, UseNormalized, Sequence)
SELECT r.MatchRuleId, lf.DatasetFieldId, rf.DatasetFieldId,
       v.Cmp, v.Tol, v.Unit, v.Norm, v.Seq
FROM (VALUES
    ('P1_REF',       'REF_PRIMARY', 'OM_REF',       'Exact',        CAST(NULL AS BIGINT), CAST(NULL AS VARCHAR(20)), CAST(0 AS BIT), 1),
    ('P2_REF_NORM',  'REF_PRIMARY', 'OM_REF',       'Exact',        NULL, NULL,  CAST(1 AS BIT), 1),
    ('P3_SEC_REF',   'REF_TXN',     'EXT_REF',      'Exact',        NULL, NULL,  CAST(0 AS BIT), 1),
    ('P4_COMPOSITE', 'AMOUNT',      'AMOUNT_MINOR', 'NumericExact', NULL, NULL,  CAST(0 AS BIT), 1),
    ('P4_COMPOSITE', 'TX_DATETIME', 'POSTED_AT',    'DateWithin',   1,    'Day', CAST(0 AS BIT), 2),
    ('P4_COMPOSITE', 'CRDTR_ACCT',  'ACCOUNT',      'Exact',        NULL, NULL,  CAST(0 AS BIT), 3)
) AS v(RuleCode, LeftField, RightField, Cmp, Tol, Unit, Norm, Seq)
JOIN cfg.MatchRule AS r ON r.RuleCode = v.RuleCode AND r.DefinitionId = @def
JOIN cfg.DatasetField AS lf ON lf.FieldCode = v.LeftField AND lf.DatasetId = @left
JOIN cfg.DatasetField AS rf ON rf.FieldCode = v.RightField AND rf.DatasetId = @right;
GO


/* ---------------------------------------------------------------------
   8. Classifications — what an unmatched row MEANS here (§10).

   Evaluated in sequence, first match wins, so the order is meaningful
   configuration rather than an implementation detail.
   --------------------------------------------------------------------- */
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');

INSERT cfg.ClassificationRule
    (DefinitionId, ExceptionCode, DisplayName, AppliesToSide, ConditionJson, Severity, Sequence)
VALUES
    /* In CliQ, not in OM. Detect and report only: no automatic posting in
       this phase, and the seam for it is disabled in the model. */
    (@def, 'FAILED_INWARD', N'In CliQ, not in OM', 'Left',
     N'{"op":"and","items":[{"field":"DIRECTION","cmp":"eq","value":"Inward"}]}', 'High', 1),

    (@def, 'FAILED_OUTWARD', N'In CliQ, not in OM (outward)', 'Left',
     N'{"op":"and","items":[{"field":"DIRECTION","cmp":"eq","value":"Outward"}]}', 'High', 2),

    /* In OM, not in CliQ. */
    (@def, 'MISSING_IN_CLIQ', N'In OM, not in CliQ', 'Right',
     N'{"op":"and","items":[{"field":"DIRECTION","cmp":"isnotnull"}]}', 'High', 3);
GO


/* ---------------------------------------------------------------------
   9. Control totals — the balance proof (§11).

   Row-level matching is not enough: a run with fully matched rows but a
   non-zero net difference is a FAILED run. FailRunOnMismatch says so.
   --------------------------------------------------------------------- */
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');
DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');
DECLARE @right INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'OM_TXN');

INSERT cfg.ControlTotalDefinition
    (DefinitionId, CheckCode, DisplayName,
     SourceAExpressionJson, SourceBExpressionJson,
     Scope, SourceTypeA, SourceTypeB, ToleranceMinor, FailRunOnMismatch)
VALUES
    /* The matched value on each side must agree to the fils. Tolerance 0:
       any difference is a break (§3). */
    (@def, 'MATCHED_TOTAL', N'Matched value agrees',
     N'{"function":"SumAmount","datasetId":' + CAST(@left AS NVARCHAR(10)) + N',"matchStatus":"Matched"}',
     N'{"function":"SumAmount","datasetId":' + CAST(@right AS NVARCHAR(10)) + N',"matchStatus":"Matched"}',
     'Run', 'Staging', 'Staging', 0, 1),

    (@def, 'MATCHED_COUNT', N'Matched count agrees',
     N'{"function":"Count","datasetId":' + CAST(@left AS NVARCHAR(10)) + N',"matchStatus":"Matched"}',
     N'{"function":"Count","datasetId":' + CAST(@right AS NVARCHAR(10)) + N',"matchStatus":"Matched"}',
     'Run', 'Staging', 'Staging', 0, 1),

    /* Inward value on each side, as a directional proof. Reported but not
       run-failing, because a legitimate one-sided late arrival moves it. */
    (@def, 'INWARD_TOTAL', N'Inward value agrees',
     N'{"function":"SumAmount","datasetId":' + CAST(@left AS NVARCHAR(10)) + N',"filter":{"op":"and","items":[{"field":"DIRECTION","cmp":"eq","value":"Inward"}]}}',
     N'{"function":"SumAmount","datasetId":' + CAST(@right AS NVARCHAR(10)) + N',"filter":{"op":"and","items":[{"field":"DIRECTION","cmp":"eq","value":"Inward"}]}}',
     'Run', 'Staging', 'Staging', 0, 0);
GO


/* ---------------------------------------------------------------------
   10. Alerting
   --------------------------------------------------------------------- */
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');

INSERT cfg.AlertPolicy (DefinitionId, EventType, ThresholdValue, Channel, Recipients)
VALUES
    (@def, 'RunFailed',              NULL, 'Email',     N'ops@example.local'),
    (@def, 'ControlTotalMismatch',   NULL, 'Email',     N'ops@example.local'),
    (@def, 'FileNotReceived',        NULL, 'Dashboard', NULL),
    /* The design promised this signal; making it a row is what turns the
       pass distribution into an alert rather than a chart nobody reads. */
    (@def, 'MatchDistributionDrift', 15,   'Dashboard', NULL);
GO

PRINT 'Demo configuration created. Definition code: CLIQ_OM';
SELECT d.Code AS Definition, d.Name,
       l.Code AS LeftDataset, r.Code AS RightDataset,
       (SELECT COUNT(*) FROM cfg.MatchRule WHERE DefinitionId = d.DefinitionId) AS Passes,
       (SELECT COUNT(*) FROM cfg.ControlTotalDefinition WHERE DefinitionId = d.DefinitionId) AS ControlTotals
FROM cfg.ReconciliationDefinition AS d
JOIN cfg.Dataset AS l ON l.DatasetId = d.LeftDatasetId
JOIN cfg.Dataset AS r ON r.DatasetId = d.RightDatasetId
WHERE d.Code = 'CLIQ_OM';


/* ---------------------------------------------------------------------
   11. Portal access.

   Access is scoped per counterparty (design §13), and the portal filters
   every read by these rows IN SQL. Three users so the levels are visible
   rather than described: the same screen looks different to each of them,
   and a fourth name with no row here sees nothing at all — which is the
   correct default for a new account, not an error.
   --------------------------------------------------------------------- */
DECLARE @cp INT = (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'JOPACC');

INSERT cfg.UserCounterpartyAccess (UserName, CounterpartyId, AccessLevel, GrantedBy)
VALUES
    (N'cfg.omar',  @cp, 'Configure', N'demo'),
    (N'ops.hala',  @cp, 'Operate',   N'demo'),
    (N'read.sami', @cp, 'Read',      N'demo');
GO


/* ---------------------------------------------------------------------
   12. The schedule.

   Evaluated in the SCHEDULE's time zone, not the server's: a server in
   UTC firing '22:00' for a Jordan session would fire at 01:00 local,
   which is the wrong business date. ExpectedFileByTime is what makes a
   file that never arrived distinguishable from one not due yet.
   --------------------------------------------------------------------- */
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');

INSERT cfg.ScheduleDefinition
    (DefinitionId, CronExpression, TimeZone, ExpectedFileByTime, IsEnabled)
VALUES (@def, '0 22 * * *', 'Asia/Amman', '21:30', 1);
GO


/* ---------------------------------------------------------------------
   13. Fees and interchange (Phase 5).

   Every amount is in MINOR UNITS of the schedule's currency: JOD has
   three, so 0.150 JOD is 150. Bands are continuous and the top band is
   open-ended — a gap would fail the run rather than silently charge
   nothing.

   Two schedules, because netting needs both sides: what the scheme pays
   us for inward traffic (Revenue) and what we pay for outward (Cost).
   --------------------------------------------------------------------- */
DECLARE @cp INT = (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'JOPACC');
DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');

INSERT cfg.FeeSchedule
    (CounterpartyId, Code, Name, Direction, TransactionType, FeeParty,
     CurrencyCode, RoundingMode, EffectiveFrom, EffectiveTo, IsActive)
VALUES
    (@cp, 'CLIQ_IN_2026', N'CliQ inward interchange 2026',  'Inward',  NULL, 'Revenue',
     'JOD', 'HalfUp', '2026-01-01', NULL, 1),
    (@cp, 'CLIQ_OUT_2026', N'CliQ outward interchange 2026', 'Outward', NULL, 'Cost',
     'JOD', 'HalfUp', '2026-01-01', NULL, 1);
GO

DECLARE @in INT = (SELECT FeeScheduleId FROM cfg.FeeSchedule WHERE Code = 'CLIQ_IN_2026');
DECLARE @out INT = (SELECT FeeScheduleId FROM cfg.FeeSchedule WHERE Code = 'CLIQ_OUT_2026');

/* Continuous from zero, open-ended at the top. 100000 minor = 100.000 JOD. */
INSERT cfg.FeeTier
    (FeeScheduleId, AmountFromMinor, AmountToMinor, CalculationType,
     FixedAmountMinor, Percentage, MinFeeMinor, MaxFeeMinor)
VALUES
    (@in,  0,      100000, 'Fixed',              100, 0,    NULL, NULL),
    (@in,  100001, NULL,   'FixedPlusPercentage', 100, 0.1, 150,  2000),
    (@out, 0,      NULL,   'Percentage',           0,  0.2, 50,   1500);
GO

/* Whether fees apply at all is configuration, not an assumption: it is
   confirmed that some reconciliation reports carry fees and some do not. */
DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');
DECLARE @in INT = (SELECT FeeScheduleId FROM cfg.FeeSchedule WHERE Code = 'CLIQ_IN_2026');

INSERT cfg.FeeApplicability (DatasetId, TransactionType, IsFeeApplicable, FeeScheduleId)
VALUES (@left, NULL, 1, @in);
GO


/* ---------------------------------------------------------------------
   14. Reports (Phase 4).

   Columns name registry fields or one of the compiler's documented
   computed values — never free text. The unmatched report is CSV because
   a transaction-level scope on a 2M-row day exceeds a worksheet's
   1,048,576 rows (E1); the exception report is small enough for Xlsx.
   --------------------------------------------------------------------- */
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'CLIQ_OM');

INSERT cfg.ReportDefinition
    (DefinitionId, Code, Name, SheetName, DataScope, FilterJson, OutputFormat,
     IncludeSubtotals, IsActive)
VALUES
    (@def, 'UNMATCHED_CSV', N'Unmatched transactions', N'Unmatched', 'Unmatched',
     NULL, 'Csv', 0, 1),
    (@def, 'EXCEPTIONS_XLSX', N'Open exceptions', N'Exceptions', 'Exceptions',
     NULL, 'Xlsx', 0, 1),
    (@def, 'CONTROL_TOTALS', N'Control totals', N'Totals', 'ControlTotals',
     NULL, 'Xlsx', 0, 1);
GO

DECLARE @left INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'CLIQ_SESSION');
DECLARE @unmatched INT = (SELECT ReportDefinitionId FROM cfg.ReportDefinition
                          WHERE Code = 'UNMATCHED_CSV');
DECLARE @exceptions INT = (SELECT ReportDefinitionId FROM cfg.ReportDefinition
                           WHERE Code = 'EXCEPTIONS_XLSX');

INSERT cfg.ReportColumn
    (ReportDefinitionId, DatasetFieldId, ComputedField, Header, DisplayFormat,
     ColumnWidth, Sequence)
SELECT @unmatched, f.DatasetFieldId, NULL, v.Header, v.Format, v.Width, v.Seq
FROM (VALUES
        ('REF_PRIMARY', N'Reference',   NULL,          28, 1),
        ('AMOUNT',      N'Amount',      N'#,##0.000',  16, 2),
        ('CURRENCY',    N'Currency',    NULL,           9, 3),
        ('TX_DATETIME', N'Date',        N'yyyy-mm-dd', 14, 4),
        ('DIRECTION',   N'Direction',   NULL,          11, 5),
        ('CRDTR_ACCT',  N'Account',     NULL,          24, 6)
     ) AS v(Code, Header, Format, Width, Seq)
JOIN cfg.DatasetField AS f ON f.DatasetId = @left AND f.FieldCode = v.Code;

/* CURRENCY is not matchable, and a report column does not need it to be:
   a report may show a field the rule builder withholds. */
INSERT cfg.ReportColumn
    (ReportDefinitionId, DatasetFieldId, ComputedField, Header, DisplayFormat,
     ColumnWidth, Sequence)
VALUES
    (@unmatched, NULL, 'MatchStatus',       N'Match status', NULL, 14, 7),
    (@unmatched, NULL, 'StagingId',         N'Staging id',   NULL, 14, 8),
    (@exceptions, NULL, 'ExceptionCode',    N'Code',         NULL, 20, 1),
    (@exceptions, NULL, 'MatchStatus',      N'Match status', NULL, 14, 2),
    (@exceptions, NULL, 'StagingId',        N'Staging id',   NULL, 14, 3),
    (@exceptions, NULL, 'MatchedByRuleCode', N'Matched by',  NULL, 18, 4);
GO

PRINT '';
PRINT 'Portal configuration: 3 access grants, 1 schedule, 2 fee schedules, 3 reports.';
SELECT (SELECT COUNT(*) FROM cfg.UserCounterpartyAccess) AS Grants,
       (SELECT COUNT(*) FROM cfg.ScheduleDefinition)      AS Schedules,
       (SELECT COUNT(*) FROM cfg.FeeSchedule)             AS FeeSchedules,
       (SELECT COUNT(*) FROM cfg.FeeTier)                 AS FeeTiers,
       (SELECT COUNT(*) FROM cfg.ReportDefinition)        AS Reports,
       (SELECT COUNT(*) FROM cfg.ReportColumn)            AS ReportColumns;
