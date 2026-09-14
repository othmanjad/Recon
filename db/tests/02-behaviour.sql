/* =====================================================================
   BEHAVIOURAL TESTS — do the constraints actually bite?
   Run AFTER 01-schema.sql, 02-seed.sql, 03-roles.sql.

   01-verify-schema.sql proves the objects EXIST. This file proves they
   REJECT what must be rejected and ACCEPT what must be accepted. Each
   test names the review finding it defends; a failure here means a fix
   regressed.

   Structure: the fixture is committed once, each test runs in its own
   transaction and is rolled back, and the fixture is removed at the end.
   The rollback-then-record order matters: a trigger that THROWs leaves
   the transaction doomed, so the result cannot be written until after
   the rollback.
   ===================================================================== */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('tempdb..#t') IS NOT NULL DROP TABLE #t;
CREATE TABLE #t (
    Seq      INT IDENTITY(1,1),
    Finding  VARCHAR(20),
    Test     NVARCHAR(170),
    Want     VARCHAR(10),
    Got      VARCHAR(10),
    Detail   NVARCHAR(300) NULL,
    Passed   BIT
);
GO

/* ---------------------------------------------------------------------
   The harness. Runs one statement batch, rolls back whatever it did,
   and records whether the database accepted or rejected it.
   --------------------------------------------------------------------- */
CREATE PROCEDURE #expect
    @finding VARCHAR(20),
    @test    NVARCHAR(170),
    @want    VARCHAR(10),          -- 'accept' | 'reject'
    @sql     NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @err NVARCHAR(300) = NULL;

    BEGIN TRAN;
    BEGIN TRY
        EXEC sp_executesql @sql;
    END TRY
    BEGIN CATCH
        SET @err = LEFT(ERROR_MESSAGE(), 250);
    END CATCH;
    IF XACT_STATE() <> 0 ROLLBACK;

    DECLARE @got VARCHAR(10) = CASE WHEN @err IS NULL THEN 'accept' ELSE 'reject' END;
    INSERT #t (Finding, Test, Want, Got, Detail, Passed)
    VALUES (@finding, @test, @want, @got, @err, CASE WHEN @got = @want THEN 1 ELSE 0 END);
END
GO

/* ---------------------------------------------------------------------
   Remove any fixture left behind by an interrupted earlier run, so the
   suite is re-runnable without manual cleanup.
   --------------------------------------------------------------------- */
DELETE ops.RunAggregate WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code LIKE 'ZZ[_]%');
DELETE ops.ReconRun WHERE TriggeredBy = 'tests';
DELETE cfg.MatchCondition WHERE MatchRuleId IN
    (SELECT MatchRuleId FROM cfg.MatchRule WHERE RuleCode LIKE 'ZZ[_]%');
DELETE cfg.MatchRule WHERE RuleCode LIKE 'ZZ[_]%';
DELETE cfg.ControlTotalDefinition WHERE CheckCode LIKE 'ZZ[_]%';
DELETE cfg.ClassificationRule WHERE ExceptionCode LIKE 'ZZ[_]%';
DELETE cfg.AlertPolicy WHERE DefinitionId IN
    (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code LIKE 'ZZ[_]%');
DELETE cfg.ReconciliationDefinition WHERE Code LIKE 'ZZ[_]%';
DELETE cfg.FeeTier WHERE FeeScheduleId IN
    (SELECT FeeScheduleId FROM cfg.FeeSchedule WHERE Code LIKE 'ZZ[_]%');
DELETE cfg.FeeSchedule WHERE Code LIKE 'ZZ[_]%';
DELETE cfg.ExclusionRule WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code LIKE 'ZZ[_]%');
DELETE stg.StagingTransaction WHERE LoadRunId IN (9001, 9002);
DELETE cfg.FieldMapping WHERE FileFormatId IN
    (SELECT FileFormatId FROM cfg.FileFormatDefinition WHERE DatasetId IN
        (SELECT DatasetId FROM cfg.Dataset WHERE Code LIKE 'ZZ[_]%'));
DELETE cfg.FileFormatDefinition WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code LIKE 'ZZ[_]%');
DELETE cfg.SqlSourceDefinition WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code LIKE 'ZZ[_]%');
DELETE cfg.FeeApplicability WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code LIKE 'ZZ[_]%');
DELETE cfg.DatasetField WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code LIKE 'ZZ[_]%');
DELETE cfg.Dataset WHERE Code LIKE 'ZZ[_]%';
DELETE cfg.Counterparty WHERE Code = 'ZZ_TEST';
GO


/* ---------------------------------------------------------------------
   Fixture — committed, so it survives each test's rollback.
   --------------------------------------------------------------------- */
INSERT cfg.Counterparty (Code, Name, CreatedBy) VALUES ('ZZ_TEST', N'Test Counterparty', 'tests');
DECLARE @cp INT = SCOPE_IDENTITY();

INSERT cfg.Dataset (CounterpartyId, Code, Name, ProviderType, DefaultCurrency, CreatedBy)
VALUES (@cp, 'ZZ_LEFT',  N'Left dataset',  'File', 'JOD', 'tests'),
       (@cp, 'ZZ_RIGHT', N'Right dataset', 'Sql',  'JOD', 'tests');

DECLARE @dsL INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'ZZ_LEFT');
DECLARE @dsR INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'ZZ_RIGHT');

INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, FieldRole, StorageSlot,
                         IsMatchable, NormalizeForMatch, NormalizedSlot)
VALUES (@dsL, 'L_REF', N'Left reference',  'String',  'Reference', 'Text1', 1, 1, 'Text21'),
       (@dsL, 'L_AMT', N'Left amount',     'Integer', 'Amount',    'Num1',  1, 0, NULL),
       (@dsL, 'L_DATE',N'Left date',       'DateTime','Date',      'Date1', 1, 0, NULL),
       (@dsR, 'R_REF', N'Right reference', 'String',  'Reference', 'Text1', 1, 1, 'Text21'),
       (@dsR, 'R_AMT', N'Right amount',    'Integer', 'Amount',    'Num1',  1, 0, NULL),
       (@dsR, 'R_DATE',N'Right date',      'DateTime','Date',      'Date1', 1, 0, NULL);

INSERT cfg.ReconciliationDefinition (CounterpartyId, Code, Name, LeftDatasetId, RightDatasetId, CreatedBy)
VALUES (@cp, 'ZZ_DEF', N'Test definition', @dsL, @dsR, 'tests');
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'ZZ_DEF');

INSERT cfg.MatchRule (DefinitionId, RuleCode, Name, Sequence) VALUES (@def, 'ZZ_P1', N'Pass 1', 1);

/* A committed base run, so supersession and Rematch can be tested. */
INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef, RunType, TriggeredBy)
VALUES (@def, 1, '2026-09-13', 'ZZS1', 'Scheduled', 'tests');
GO

DECLARE @dsL INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'ZZ_LEFT');
DECLARE @dsR INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'ZZ_RIGHT');
DECLARE @cp  INT = (SELECT CounterpartyId FROM cfg.Counterparty WHERE Code = 'ZZ_TEST');
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'ZZ_DEF');
DECLARE @rule INT = (SELECT MatchRuleId FROM cfg.MatchRule WHERE RuleCode = 'ZZ_P1');
DECLARE @run BIGINT = (SELECT RunId FROM ops.ReconRun WHERE SessionRef = 'ZZS1');
DECLARE @fL INT = (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode = 'L_REF');
DECLARE @fR INT = (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode = 'R_REF');

/* =====================================================================
   FINDING 2 · the slot pools. This is the path that silently overwrote
   a mapped financial field in v0.2.
   ===================================================================== */

EXEC #expect 'finding2', N'StorageSlot = Text21 (a companion-pool slot)', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''BAD1'', N''Bad'', ''String'', ''Text21'');';

EXEC #expect 'finding2', N'NormalizedSlot = Text2 (a reservable-pool slot)', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot,
                               NormalizeForMatch, NormalizedSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''BAD2'', N''Bad'', ''String'', ''Text5'', 1, ''Text2'');';

EXEC #expect 'finding2', N'String field mapped into an Integer slot', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''BAD3'', N''Bad'', ''String'', ''Num5'');';

EXEC #expect 'finding2', N'two fields pointing at the same companion slot', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot,
                               NormalizeForMatch, NormalizedSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''N1'', N''N1'', ''String'', ''Text6'', 1, ''Text22''),
             ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''N2'', N''N2'', ''String'', ''Text7'', 1, ''Text22'');';

EXEC #expect 'finding2', N'two fields claiming the same StorageSlot', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''S1'', N''S1'', ''String'', ''Text8''),
             ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''S2'', N''S2'', ''String'', ''Text8'');';

/* The regression test for the defect introduced WHILE fixing finding 2:
   a UNIQUE CONSTRAINT on (DatasetId, NormalizedSlot) treats NULLs as
   equal and would allow only ONE field per dataset without a companion.
   Most fields have none, so this must be accepted. */
EXEC #expect 'finding2', N'three more fields with NO companion slot (NULL x3)', 'accept',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''P1'', N''P1'', ''String'', ''Text9''),
             ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''P2'', N''P2'', ''String'', ''Text10''),
             ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''P3'', N''P3'', ''String'', ''Text11'');';

EXEC #expect 'finding2', N'valid field: Text12 storage + Text23 companion', 'accept',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, FieldRole,
                               StorageSlot, NormalizeForMatch, NormalizedSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''OK1'', N''Ok'', ''String'', ''Reference'', ''Text12'', 1, ''Text23'');';

EXEC #expect 'finding2', N'NormalizeForMatch set on an Integer field', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot,
                               NormalizeForMatch, NormalizedSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''BAD4'', N''Bad'', ''Integer'', ''Num6'', 1, ''Text24'');';

EXEC #expect 'finding2', N'NormalizedSlot set without NormalizeForMatch', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot,
                               NormalizeForMatch, NormalizedSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''BAD5'', N''Bad'', ''String'', ''Text13'', 0, ''Text25'');';

EXEC #expect 'finding2', N'StorageSlot that is not in the catalogue at all', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, StorageSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''BAD6'', N''Bad'', ''String'', ''Text99'');';

/* =====================================================================
   THE FIELD REGISTRY · FieldRole is what the engine reads instead of
   column names, so a typo silently disables control totals and fees.
   ===================================================================== */
EXEC #expect 'registry', N'misspelt FieldRole ''Ammount''', 'reject',
    N'INSERT cfg.DatasetField (DatasetId, FieldCode, DisplayLabel, DataType, FieldRole, StorageSlot)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''BAD7'', N''Bad'', ''String'', ''Ammount'', ''Text14'');';

EXEC #expect 'registry', N'definition with the same dataset on both sides', 'reject',
    N'INSERT cfg.ReconciliationDefinition (CounterpartyId, Code, Name, LeftDatasetId,
                                           RightDatasetId, CreatedBy)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''), ''ZZ_SELF'', N''Self'', (SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), (SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''tests'');';

EXEC #expect 'registry', N'negative matching window', 'reject',
    N'INSERT cfg.ReconciliationDefinition (CounterpartyId, Code, Name, LeftDatasetId,
                                           RightDatasetId, MatchingWindowDaysBefore, CreatedBy)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''), ''ZZ_NEG'', N''Neg'', (SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), (SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_RIGHT''), -1, ''tests'');';

/* =====================================================================
   FINDING 1 · StagingRunId and the supersession rules
   ===================================================================== */

EXEC #expect 'finding1', N'Rematch with no SourceRunId', 'reject',
    N'INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef,
                           RunType, TriggeredBy)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), 1, ''2026-09-20'', ''ZZS9'', ''Rematch'', ''tests'');';

EXEC #expect 'supersede', N'a second CURRENT run for the same date and session', 'reject',
    N'INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef,
                           RunType, TriggeredBy)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), 1, ''2026-09-13'', ''ZZS1'', ''Scheduled'', ''tests'');';

/* Sandbox runs share the staging table but are never authoritative, so
   one must be allowed to coexist with the current run (E5). */
EXEC #expect 'E5', N'a Sandbox run alongside the current run', 'accept',
    N'INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef,
                           RunType, TriggeredBy)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), 1, ''2026-09-13'', ''ZZS1'', ''Sandbox'', ''tests'');';

EXEC #expect 'finding8', N'RunType = ''sandbox'' in the wrong case', 'reject',
    N'INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef,
                           RunType, TriggeredBy)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), 1, ''2026-09-21'', ''ZZS1'', ''sandbox'', ''tests'');';

/* Case sensitivity is not cosmetic. SQL Server's default collation is
   case-INSENSITIVE, so 'sandbox' satisfied a CHECK listing 'Sandbox' and
   was stored as typed. The database then treats it as Sandbox while C#
   string comparison does not — so an app filtering RunType != "Sandbox"
   would pull a sandbox run into production aggregates. The enum CHECKs
   collate case-sensitively so the stored value is always canonical. */
EXEC #expect 'collation', N'MatchStatus = ''matched'' (wrong case)', 'reject',
    N'INSERT stg.StagingTransaction (DatasetId, LoadRunId, TxDate, Text1, MatchStatus)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), 9001,
              ''2026-09-15'', N''E2E-9'', ''matched'');';

EXEC #expect 'collation', N'exception Status = ''OPEN'' (wrong case)', 'reject',
    N'INSERT ops.ReconException (RunId, DefinitionId, BusinessDate, Side, ExceptionCode, Status)
      VALUES ((SELECT RunId FROM ops.ReconRun WHERE SessionRef=''ZZS1''),
              (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''),
              ''2026-09-13'', ''Left'', ''FAILED_INWARD'', ''OPEN'');';

EXEC #expect 'collation', N'Dataset ProviderType = ''file'' (wrong case)', 'reject',
    N'INSERT cfg.Dataset (CounterpartyId, Code, Name, ProviderType, CreatedBy)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''),
              ''ZZ_CASE'', N''Case test'', ''file'', ''tests'');';

EXEC #expect 'collation', N'Dataset ProviderType = ''File'' (canonical)', 'accept',
    N'INSERT cfg.Dataset (CounterpartyId, Code, Name, ProviderType, CreatedBy)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''),
              ''ZZ_CASE2'', N''Case test'', ''File'', ''tests'');';

EXEC #expect 'finding1', N'a run whose SourceRunId is itself', 'reject',
    N'DECLARE @r BIGINT;
      INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef,
                           RunType, TriggeredBy)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), 1, ''2026-09-22'', ''ZZS8'', ''Scheduled'', ''tests'');
      SET @r = SCOPE_IDENTITY();
      UPDATE ops.ReconRun SET SourceRunId = @r WHERE RunId = @r;';

EXEC #expect 'finding8', N'run Status = ''Resuming'' (added by B3)', 'accept',
    N'INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef,
                           RunType, Status, TriggeredBy)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), 1, ''2026-09-23'', ''ZZS7'', ''Scheduled'', ''Resuming'', ''tests'');';

/* =====================================================================
   A2 · 'Normalized' is not a runtime comparison type
   C1 · Aggregate mode needs a group key
   ===================================================================== */

EXEC #expect 'A2', N'ComparisonType = ''Normalized''', 'reject',
    N'INSERT cfg.MatchCondition (MatchRuleId, LeftFieldId, RightFieldId, ComparisonType)
      VALUES ((SELECT MatchRuleId FROM cfg.MatchRule WHERE RuleCode=''ZZ_P1''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''L_REF''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''R_REF''), ''Normalized'');';

EXEC #expect 'A2', N'ComparisonType = ''Exact'' on the companion pair', 'accept',
    N'INSERT cfg.MatchCondition (MatchRuleId, LeftFieldId, RightFieldId, ComparisonType)
      VALUES ((SELECT MatchRuleId FROM cfg.MatchRule WHERE RuleCode=''ZZ_P1''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''L_REF''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''R_REF''), ''Exact'');';

EXEC #expect 'rules', N'DateWithin with no tolerance value', 'reject',
    N'INSERT cfg.MatchCondition (MatchRuleId, LeftFieldId, RightFieldId, ComparisonType)
      VALUES ((SELECT MatchRuleId FROM cfg.MatchRule WHERE RuleCode=''ZZ_P1''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''L_REF''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''R_REF''), ''DateWithin'');';

EXEC #expect 'rules', N'DateWithin with tolerance 1 Day', 'accept',
    N'INSERT cfg.MatchCondition (MatchRuleId, LeftFieldId, RightFieldId, ComparisonType,
                                 ToleranceValue, ToleranceUnit)
      VALUES ((SELECT MatchRuleId FROM cfg.MatchRule WHERE RuleCode=''ZZ_P1''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''L_REF''), (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''R_REF''), ''DateWithin'', 1, ''Day'');';

EXEC #expect 'C1', N'Aggregate rule with no GroupByFields', 'reject',
    N'INSERT cfg.MatchRule (DefinitionId, RuleCode, Name, Sequence, MatchMode)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''ZZ_AGG'', N''Agg'', 2, ''Aggregate'');';

EXEC #expect 'C1', N'Aggregate rule with GroupByFields and a function', 'accept',
    N'INSERT cfg.MatchRule (DefinitionId, RuleCode, Name, Sequence, MatchMode,
                            LeftGroupByFields, AggregateFunction)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''ZZ_AGG2'', N''Agg'', 3, ''Aggregate'', ''L_REF'', ''SumAndCount'');';

EXEC #expect 'rules', N'two passes sharing Sequence = 1', 'reject',
    N'INSERT cfg.MatchRule (DefinitionId, RuleCode, Name, Sequence)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''ZZ_DUP'', N''Dup'', 1);';

/* =====================================================================
   D1 / B5 · the structured filter replaced free SQL text
   ===================================================================== */

EXEC #expect 'D1', N'FilterJson holding SQL instead of JSON', 'reject',
    N'INSERT cfg.SqlSourceDefinition (DatasetId, ObjectType, ObjectName, ConnectionName, FilterJson)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_RIGHT''), ''View'', ''dbo.vw_T'', ''OM'',
              ''1=1; DROP TABLE stg.StagingTransaction--'');';

EXEC #expect 'D1', N'FilterJson holding a valid condition tree', 'accept',
    N'INSERT cfg.SqlSourceDefinition (DatasetId, ObjectType, ObjectName, ConnectionName, FilterJson)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_RIGHT''), ''View'', ''dbo.vw_T2'', ''OM'',
              N''{"op":"and","items":[{"field":"R_REF","cmp":"ne","value":"RJCT"}]}'');';

EXEC #expect 'C6', N'ExclusionRule ConditionJson that is not JSON', 'reject',
    N'INSERT cfg.ExclusionRule (DatasetId, Name, ConditionJson, ReasonCode)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), N''Rejected'', ''Status = RJCT'', ''REJECTED'');';

EXEC #expect 'C6', N'ExclusionRule with a valid condition tree', 'accept',
    N'INSERT cfg.ExclusionRule (DatasetId, Name, ConditionJson, ReasonCode)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), N''Rejected'',
              N''{"op":"and","items":[{"field":"L_REF","cmp":"eq","value":"RJCT"}]}'', ''REJECTED'');';

EXEC #expect 'B7', N'TransformChainJson that is not JSON', 'reject',
    N'DECLARE @ff INT;
      INSERT cfg.FileFormatDefinition (DatasetId, FormatType, EffectiveFrom)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''Csv'', ''2026-01-01'');
      SET @ff = SCOPE_IDENTITY();
      INSERT cfg.FieldMapping (FileFormatId, DatasetFieldId, SourcePath, TransformChainJson)
      VALUES (@ff, (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''L_REF''), ''Col1'', ''Trim|Upper|StripNonAlphanumeric'');';

EXEC #expect 'B7', N'TransformChainJson as a JSON array', 'accept',
    N'DECLARE @ff INT;
      INSERT cfg.FileFormatDefinition (DatasetId, FormatType, EffectiveFrom)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''Csv'', ''2026-01-01'');
      SET @ff = SCOPE_IDENTITY();
      INSERT cfg.FieldMapping (FileFormatId, DatasetFieldId, SourcePath, TransformChainJson)
      VALUES (@ff, (SELECT DatasetFieldId FROM cfg.DatasetField WHERE FieldCode=''L_REF''), ''Col1'', N''[{"op":"Trim"},{"op":"Upper"}]'');';

/* =====================================================================
   C4 / finding 11 · money
   ===================================================================== */

EXEC #expect 'finding11', N'FeeSchedule in an unknown currency ''XXX''', 'reject',
    N'INSERT cfg.FeeSchedule (CounterpartyId, Code, Name, Direction, FeeParty,
                              CurrencyCode, EffectiveFrom)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''), ''ZZ_FS1'', N''Bad'', ''Inward'', ''Revenue'', ''XXX'', ''2026-01-01'');';

EXEC #expect 'fees', N'fee tier whose upper bound is below its lower', 'reject',
    N'DECLARE @fs INT;
      INSERT cfg.FeeSchedule (CounterpartyId, Code, Name, Direction, FeeParty,
                              CurrencyCode, EffectiveFrom)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''), ''ZZ_FS2'', N''Ok'', ''Inward'', ''Revenue'', ''JOD'', ''2026-01-01'');
      SET @fs = SCOPE_IDENTITY();
      INSERT cfg.FeeTier (FeeScheduleId, AmountFromMinor, AmountToMinor, CalculationType)
      VALUES (@fs, 100000, 50000, ''Fixed'');';

EXEC #expect 'fees', N'FeeSchedule ending before it starts', 'reject',
    N'INSERT cfg.FeeSchedule (CounterpartyId, Code, Name, Direction, FeeParty, CurrencyCode,
                              EffectiveFrom, EffectiveTo)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''), ''ZZ_FS3'', N''Bad'', ''Inward'', ''Revenue'', ''JOD'',
              ''2026-06-01'', ''2026-01-01'');';

EXEC #expect 'fees', N'unknown RoundingMode ''Banker''', 'reject',
    N'INSERT cfg.FeeSchedule (CounterpartyId, Code, Name, Direction, FeeParty, CurrencyCode,
                              RoundingMode, EffectiveFrom)
      VALUES ((SELECT CounterpartyId FROM cfg.Counterparty WHERE Code=''ZZ_TEST''), ''ZZ_FS4'', N''Bad'', ''Inward'', ''Revenue'', ''JOD'',
              ''Banker'', ''2026-01-01'');';

EXEC #expect 'fees', N'two FeeApplicability defaults for one dataset', 'reject',
    N'INSERT cfg.FeeApplicability (DatasetId, TransactionType, IsFeeApplicable)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), NULL, 0), ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), NULL, 1);';

/* =====================================================================
   E1 / E4 / C1 · operational limits and control totals
   ===================================================================== */

EXEC #expect 'C1', N'Period-scoped control total with no PeriodDays', 'reject',
    N'INSERT cfg.ControlTotalDefinition (DefinitionId, CheckCode, DisplayName,
              SourceAExpressionJson, SourceBExpressionJson, Scope)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''ZZ_CT'', N''Monthly'', N''{"agg":"sum"}'', N''{"agg":"sum"}'', ''Period'');';

EXEC #expect 'C1', N'Period-scoped control total with PeriodDays = 30', 'accept',
    N'INSERT cfg.ControlTotalDefinition (DefinitionId, CheckCode, DisplayName,
              SourceAExpressionJson, SourceBExpressionJson, Scope, PeriodDays, SourceTypeA)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''ZZ_CT2'', N''Monthly'', N''{"agg":"sum"}'', N''{"agg":"sum"}'',
              ''Period'', 30, ''RunAggregate'');';

EXEC #expect 'C1', N'control total SourceType = ''Guess''', 'reject',
    N'INSERT cfg.ControlTotalDefinition (DefinitionId, CheckCode, DisplayName,
              SourceAExpressionJson, SourceBExpressionJson, SourceTypeA)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''ZZ_CT3'', N''Bad'', N''{}'', N''{}'', ''Guess'');';

EXEC #expect 'E6', N'AlertPolicy EventType = ''MatchDistributionDrift''', 'accept',
    N'INSERT cfg.AlertPolicy (DefinitionId, EventType, Channel)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''MatchDistributionDrift'', ''Dashboard'');';

EXEC #expect 'E6', N'AlertPolicy EventType = ''SomethingElse''', 'reject',
    N'INSERT cfg.AlertPolicy (DefinitionId, EventType, Channel)
      VALUES ((SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''SomethingElse'', ''Email'');';

/* =====================================================================
   STAGING · partition routing and the status vocabulary
   ===================================================================== */

EXEC #expect 'staging', N'MatchStatus = ''Duplicate'' and ''Excluded'' (C3/C6)', 'accept',
    N'INSERT stg.StagingTransaction (DatasetId, LoadRunId, TxDate, Text1, Num1, MatchStatus)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), 9001, ''2026-03-15'', N''E2E-1'', 125500, ''Duplicate''),
             ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), 9001, ''2026-09-15'', N''E2E-2'', 48000,  ''Excluded'');';

EXEC #expect 'staging', N'MatchStatus = ''Maybe''', 'reject',
    N'INSERT stg.StagingTransaction (DatasetId, LoadRunId, TxDate, Text1, MatchStatus)
      VALUES ((SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), 9001, ''2026-09-15'', N''E2E-3'', ''Maybe'');';

EXEC #expect 'results', N'MatchResult referencing neither side', 'reject',
    N'INSERT ops.MatchResult (RunId, BusinessDate, MatchStatus)
      VALUES ((SELECT RunId FROM ops.ReconRun WHERE SessionRef=''ZZS1''), ''2026-09-13'', ''Matched'');';

EXEC #expect 'results', N'exception resolved with no ClosedAt', 'reject',
    N'INSERT ops.ReconException (RunId, DefinitionId, BusinessDate, Side, ExceptionCode, Status)
      VALUES ((SELECT RunId FROM ops.ReconRun WHERE SessionRef=''ZZS1''), (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''2026-09-13'', ''Left'', ''FAILED_INWARD'', ''Resolved'');';

EXEC #expect 'C2', N'exception carrying its KeyValuesJson snapshot', 'accept',
    N'INSERT ops.ReconException (RunId, DefinitionId, BusinessDate, Side, ExceptionCode,
                                 AmountMinor, CurrencyCode, KeyValuesJson)
      VALUES ((SELECT RunId FROM ops.ReconRun WHERE SessionRef=''ZZS1''), (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''2026-09-13'', ''Left'', ''FAILED_INWARD'',
              125500, ''JOD'', N''{"L_REF":"E2E-20260913-0099412"}'');';

EXEC #expect 'C2', N'exception whose KeyValuesJson is not JSON', 'reject',
    N'INSERT ops.ReconException (RunId, DefinitionId, BusinessDate, Side, ExceptionCode,
                                 KeyValuesJson)
      VALUES ((SELECT RunId FROM ops.ReconRun WHERE SessionRef=''ZZS1''), (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''2026-09-13'', ''Left'', ''FAILED_INWARD'',
              ''L_REF=E2E-1'');';

EXEC #expect 'C1', N'two RunAggregate rows for the same key', 'reject',
    N'INSERT ops.RunAggregate (RunId, DefinitionId, BusinessDate, DatasetId, Side,
                               GroupKey, MatchStatus, RowCnt, AmountMinorSum, CurrencyCode)
      VALUES ((SELECT RunId FROM ops.ReconRun WHERE SessionRef=''ZZS1''), (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''2026-09-13'', (SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''Left'',
              ''Direction=Inward'', ''Matched'', 100, 5000, ''JOD''),
             ((SELECT RunId FROM ops.ReconRun WHERE SessionRef=''ZZS1''), (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code=''ZZ_DEF''), ''2026-09-13'', (SELECT DatasetId FROM cfg.Dataset WHERE Code=''ZZ_LEFT''), ''Left'',
              ''Direction=Inward'', ''Matched'', 200, 9000, ''JOD'');';
GO

/* =====================================================================
   Checks that read values rather than expecting an error.
   ===================================================================== */
DECLARE @def INT = (SELECT DefinitionId FROM cfg.ReconciliationDefinition WHERE Code = 'ZZ_DEF');
DECLARE @dsL INT = (SELECT DatasetId FROM cfg.Dataset WHERE Code = 'ZZ_LEFT');
DECLARE @run BIGINT = (SELECT RunId FROM ops.ReconRun WHERE SessionRef = 'ZZS1');

/* finding 1 · a normal run reads its own staging. */
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
SELECT 'finding1', N'normal run: StagingRunId equals its own RunId', 'accept',
       CASE WHEN StagingRunId = RunId THEN 'accept' ELSE 'reject' END,
       N'RunId=' + CAST(RunId AS NVARCHAR(20)) + N' StagingRunId=' + CAST(StagingRunId AS NVARCHAR(20)),
       CASE WHEN StagingRunId = RunId THEN 1 ELSE 0 END
FROM ops.ReconRun WHERE RunId = @run;

/* finding 1 · a Rematch reads the SOURCE run's staging. This is the rule
   whose absence made every staging query return zero rows. */
BEGIN TRAN;
UPDATE ops.ReconRun SET IsCurrent = 0 WHERE RunId = @run;
DECLARE @r2 BIGINT;
INSERT ops.ReconRun (DefinitionId, DefinitionVersion, BusinessDate, SessionRef, RunType,
                     SourceRunId, SupersedesRunId, TriggeredBy)
VALUES (@def, 1, '2026-09-13', 'ZZS1', 'Rematch', @run, @run, 'tests');
SET @r2 = SCOPE_IDENTITY();

INSERT #t (Finding, Test, Want, Got, Detail, Passed)
SELECT 'finding1', N'Rematch: StagingRunId points at SourceRunId, not its own RunId', 'accept',
       CASE WHEN StagingRunId = @run AND RunId <> @run THEN 'accept' ELSE 'reject' END,
       N'RunId=' + CAST(RunId AS NVARCHAR(20)) + N' SourceRunId=' + CAST(SourceRunId AS NVARCHAR(20))
         + N' StagingRunId=' + CAST(StagingRunId AS NVARCHAR(20)),
       CASE WHEN StagingRunId = @run AND RunId <> @run THEN 1 ELSE 0 END
FROM ops.ReconRun WHERE RunId = @r2;
ROLLBACK;

/* C4 · minor units come from the table, not a constant. */
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
SELECT 'C4', N'minor units read from cfg.Currency (JOD=3, USD=2)', 'accept',
       CASE WHEN j.MinorUnits = 3 AND u.MinorUnits = 2 THEN 'accept' ELSE 'reject' END,
       N'JOD=' + CAST(j.MinorUnits AS NVARCHAR(2)) + N' USD=' + CAST(u.MinorUnits AS NVARCHAR(2)),
       CASE WHEN j.MinorUnits = 3 AND u.MinorUnits = 2 THEN 1 ELSE 0 END
FROM cfg.Currency j CROSS JOIN cfg.Currency u
WHERE j.CurrencyCode = 'JOD' AND u.CurrencyCode = 'USD';

/* Partition routing: three dates must land in three monthly partitions. */
BEGIN TRAN;
INSERT stg.StagingTransaction (DatasetId, LoadRunId, TxDate, Text1, Num1)
VALUES (@dsL, 9002, '2026-03-15', N'E2E-A', 125500),
       (@dsL, 9002, '2026-09-15', N'E2E-B', 48000),
       (@dsL, 9002, '2027-01-15', N'E2E-C', 200000);

INSERT #t (Finding, Test, Want, Got, Detail, Passed)
SELECT 'partition', N'three business dates route to three distinct partitions', 'accept',
       CASE WHEN COUNT(DISTINCT $PARTITION.pf_ByMonth(TxDate)) = 3 THEN 'accept' ELSE 'reject' END,
       N'partition numbers: ' + STUFF((SELECT DISTINCT N',' + CAST($PARTITION.pf_ByMonth(TxDate) AS NVARCHAR(4))
                                       FROM stg.StagingTransaction WHERE LoadRunId = 9002
                                       FOR XML PATH('')), 1, 1, N''),
       CASE WHEN COUNT(DISTINCT $PARTITION.pf_ByMonth(TxDate)) = 3 THEN 1 ELSE 0 END
FROM stg.StagingTransaction WHERE LoadRunId = 9002;
ROLLBACK;

/* finding 9 · a long condition tree must survive intact. The old
   NVARCHAR(1000) columns would have truncated it silently. */
BEGIN TRAN;
DECLARE @big NVARCHAR(MAX) = N'{"op":"and","items":['
    + STUFF(REPLICATE(N',{"field":"L_REF","cmp":"eq","value":"padpadpadpadpadpad"}', 80), 1, 1, N'')
    + N']}';
INSERT cfg.ClassificationRule (DefinitionId, ExceptionCode, DisplayName, AppliesToSide,
                               ConditionJson, Sequence)
VALUES (@def, 'ZZ_BIG', N'Big tree', 'Left', @big, 9);

INSERT #t (Finding, Test, Want, Got, Detail, Passed)
SELECT 'finding9', N'condition tree of ' + CAST(LEN(@big) AS NVARCHAR(10)) + N' chars stored intact',
       'accept', CASE WHEN LEN(ConditionJson) = LEN(@big) THEN 'accept' ELSE 'reject' END,
       N'stored ' + CAST(LEN(ConditionJson) AS NVARCHAR(10)) + N' of ' + CAST(LEN(@big) AS NVARCHAR(10))
         + N' chars — NVARCHAR(1000) would have truncated',
       CASE WHEN LEN(ConditionJson) = LEN(@big) THEN 1 ELSE 0 END
FROM cfg.ClassificationRule WHERE DefinitionId = @def AND ExceptionCode = 'ZZ_BIG';
ROLLBACK;
GO

/* =====================================================================
   FINDING 5 · the audit log, tested as real users rather than by
   reading sys.database_permissions
   ===================================================================== */
BEGIN TRAN;
INSERT aud.AuditLog (EntityType, EntityId, Action, PerformedBy)
VALUES ('Dataset', '1', 'Create', 'tests');

IF DATABASE_PRINCIPAL_ID('zz_reader') IS NULL CREATE USER zz_reader WITHOUT LOGIN;
IF DATABASE_PRINCIPAL_ID('zz_app')    IS NULL CREATE USER zz_app    WITHOUT LOGIN;
ALTER ROLE recon_reader ADD MEMBER zz_reader;
ALTER ROLE recon_app    ADD MEMBER zz_app;

DECLARE @err NVARCHAR(300), @n INT;

/* recon_reader must be able to READ the audit log — the capability v0.2
   granted to nobody, which made the promised viewer impossible. */
SET @err = NULL;
BEGIN TRY
    EXECUTE AS USER = 'zz_reader';
    SET @n = (SELECT COUNT(*) FROM aud.AuditLog);
    REVERT;
END TRY
BEGIN CATCH
    SET @err = LEFT(ERROR_MESSAGE(), 250);
    IF USER_NAME() <> 'dbo' REVERT;
END CATCH;
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
VALUES ('finding5', N'recon_reader can SELECT the audit log', 'accept',
        CASE WHEN @err IS NULL THEN 'accept' ELSE 'reject' END,
        COALESCE(@err, N'read ' + CAST(@n AS NVARCHAR(10)) + N' row(s)'),
        CASE WHEN @err IS NULL THEN 1 ELSE 0 END);

/* Nobody may rewrite it. */
SET @err = NULL;
BEGIN TRY
    EXECUTE AS USER = 'zz_reader';
    UPDATE aud.AuditLog SET PerformedBy = 'tampered' WHERE AuditId > 0;
    REVERT;
END TRY
BEGIN CATCH
    SET @err = LEFT(ERROR_MESSAGE(), 250);
    IF USER_NAME() <> 'dbo' REVERT;
END CATCH;
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
VALUES ('finding5', N'recon_reader cannot UPDATE the audit log', 'reject',
        CASE WHEN @err IS NULL THEN 'accept' ELSE 'reject' END, @err,
        CASE WHEN @err IS NULL THEN 0 ELSE 1 END);

SET @err = NULL;
BEGIN TRY
    EXECUTE AS USER = 'zz_app';
    INSERT aud.AuditLog (EntityType, EntityId, Action, PerformedBy)
    VALUES ('MatchRule', '7', 'Update', 'zz_app');
    REVERT;
END TRY
BEGIN CATCH
    SET @err = LEFT(ERROR_MESSAGE(), 250);
    IF USER_NAME() <> 'dbo' REVERT;
END CATCH;
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
VALUES ('finding5', N'recon_app can APPEND to the audit log', 'accept',
        CASE WHEN @err IS NULL THEN 'accept' ELSE 'reject' END, @err,
        CASE WHEN @err IS NULL THEN 1 ELSE 0 END);

SET @err = NULL;
BEGIN TRY
    EXECUTE AS USER = 'zz_app';
    DELETE aud.AuditLog WHERE AuditId > 0;
    REVERT;
END TRY
BEGIN CATCH
    SET @err = LEFT(ERROR_MESSAGE(), 250);
    IF USER_NAME() <> 'dbo' REVERT;
END CATCH;
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
VALUES ('finding5', N'recon_app cannot DELETE audit rows', 'reject',
        CASE WHEN @err IS NULL THEN 'accept' ELSE 'reject' END, @err,
        CASE WHEN @err IS NULL THEN 0 ELSE 1 END);

/* D3 · 'Export' must be loggable: regulators ask who downloaded what. */
SET @err = NULL;
BEGIN TRY
    INSERT aud.AuditLog (EntityType, EntityId, Action, PerformedBy)
    VALUES ('Report', 'EXC_DAILY', 'Export', 'ops.hala');
END TRY
BEGIN CATCH
    SET @err = LEFT(ERROR_MESSAGE(), 250);
END CATCH;
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
VALUES ('D3', N'audit Action = ''Export'' is loggable', 'accept',
        CASE WHEN @err IS NULL THEN 'accept' ELSE 'reject' END, @err,
        CASE WHEN @err IS NULL THEN 1 ELSE 0 END);

/* recon_loader must NOT be able to change configuration. */
SET @err = NULL;
IF DATABASE_PRINCIPAL_ID('zz_loader') IS NULL CREATE USER zz_loader WITHOUT LOGIN;
ALTER ROLE recon_loader ADD MEMBER zz_loader;
BEGIN TRY
    EXECUTE AS USER = 'zz_loader';
    UPDATE cfg.Dataset SET Name = N'hijacked' WHERE Code = 'ZZ_LEFT';
    REVERT;
END TRY
BEGIN CATCH
    SET @err = LEFT(ERROR_MESSAGE(), 250);
    IF USER_NAME() <> 'dbo' REVERT;
END CATCH;
INSERT #t (Finding, Test, Want, Got, Detail, Passed)
VALUES ('D2', N'recon_loader cannot modify configuration', 'reject',
        CASE WHEN @err IS NULL THEN 'accept' ELSE 'reject' END, @err,
        CASE WHEN @err IS NULL THEN 0 ELSE 1 END);

ROLLBACK;
GO

/* ---------------------------------------------------------------------
   Remove the fixture.
   --------------------------------------------------------------------- */
DELETE ops.ReconRun WHERE TriggeredBy = 'tests';
DELETE cfg.MatchCondition WHERE MatchRuleId IN
    (SELECT MatchRuleId FROM cfg.MatchRule WHERE RuleCode LIKE 'ZZ[_]%');
DELETE cfg.MatchRule WHERE RuleCode LIKE 'ZZ[_]%';
DELETE cfg.ReconciliationDefinition WHERE Code LIKE 'ZZ[_]%';
DELETE cfg.DatasetField WHERE DatasetId IN
    (SELECT DatasetId FROM cfg.Dataset WHERE Code LIKE 'ZZ[_]%');
DELETE cfg.Dataset WHERE Code LIKE 'ZZ[_]%';
DELETE cfg.Counterparty WHERE Code = 'ZZ_TEST';
IF DATABASE_PRINCIPAL_ID('zz_reader') IS NOT NULL DROP USER zz_reader;
IF DATABASE_PRINCIPAL_ID('zz_app')    IS NOT NULL DROP USER zz_app;
IF DATABASE_PRINCIPAL_ID('zz_loader') IS NOT NULL DROP USER zz_loader;
GO

/* =====================================================================
   Report
   ===================================================================== */
SELECT Seq, Finding, Test, Want AS Expect, Got,
       CASE WHEN Passed = 1 THEN 'PASS' ELSE '*** FAIL ***' END AS Result
FROM #t ORDER BY Seq;

DECLARE @fail INT = (SELECT COUNT(*) FROM #t WHERE Passed = 0);
SELECT CAST(COUNT(*) AS VARCHAR(10)) + ' tests, '
     + CAST(SUM(CAST(Passed AS INT)) AS VARCHAR(10)) + ' passed, '
     + CAST(@fail AS VARCHAR(10)) + ' failed' AS Summary
FROM #t;

IF @fail > 0
BEGIN
    SELECT Seq, Finding, Test, Detail FROM #t WHERE Passed = 0 ORDER BY Seq;
    DECLARE @m NVARCHAR(200) = CAST(@fail AS NVARCHAR(10)) + ' behavioural test(s) failed';
    THROW 51001, @m, 1;
END
