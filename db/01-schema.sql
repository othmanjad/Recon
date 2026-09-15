/* =====================================================================
   RECONCILIATION PLATFORM — SQL Server DDL
   v1.0  |  consolidated: v0.1 base + v0.2 delta + v1.0 review fixes
   =====================================================================
   This script REPLACES recon-platform-ddl.sql (v0.1) and
   recon-platform-ddl-v02-delta.sql. Nothing was ever deployed, so the
   delta form is no longer useful: one executable script is.

   Storage model: SLOT-BASED (decision confirmed, design §7).

   Design notes:
     - Schemas: cfg (configuration), ops (operational), stg (staging),
                aud (audit). Separation allows per-schema permissions.
     - All monetary comparison happens on BIGINT MINOR UNITS, scaled by
       the dataset's currency (cfg.Currency.MinorUnits: JOD=3, USD=2).
       No FLOAT anywhere, at any stage.
     - StagingTransaction is partitioned on TxDate (SQL Server supports a
       single partition column). Per-dataset isolation is achieved by
       DatasetId being the LEADING column of the clustered index, not by
       partitioning — this gives equivalent seek behaviour.
     - Load path: SqlBulkCopy with TableLock, BatchSize ~100k, and an
       ORDER hint matching the clustered key (DatasetId, TxDate,
       StagingId). Sorted input into a clustered index is minimally
       logged and avoids page splits. Do NOT attempt to drop indexes for
       a single dataset's load — this is one shared table.
     - Text columns are a minimum of 300 characters throughout. Columns
       whose width carries meaning are excluded: CHAR(3) currency codes,
       CHAR(64) SHA-256 hashes, VARCHAR(45) IP addresses, and the
       enumeration columns guarded by CHECK constraints.

   Run order: 01-schema.sql, 02-seed.sql, 03-roles.sql, 04-database-options.sql
   ===================================================================== */

/* ---------------------------------------------------------------------
   REQUIRED SET OPTIONS — do not remove.

   Filtered indexes and indexes on computed columns cannot be created
   unless QUOTED_IDENTIFIER and ANSI_NULLS are ON. SSMS sets both by
   default; sqlcmd sets QUOTED_IDENTIFIER OFF, so a deployment through
   sqlcmd or a CI pipeline fails on the first filtered index without
   these two lines. Setting them here makes the script independent of
   whichever client runs it.
   --------------------------------------------------------------------- */
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO


CREATE SCHEMA cfg;   -- configuration, portal-managed
GO
CREATE SCHEMA ops;   -- operational data
GO
CREATE SCHEMA stg;   -- staging (high volume)
GO
CREATE SCHEMA aud;   -- audit
GO


/* =====================================================================
   1. PARTITIONING
   Monthly partitions. A scheduled maintenance job must keep >= 3 future
   partitions ahead of need and alert when fewer than 2 exist (E2); if no
   future partition exists at month rollover everything lands in the last
   range and the sliding window breaks.
   Retention decision only changes which partitions stay online.
   ===================================================================== */

CREATE PARTITION FUNCTION pf_ByMonth (DATE)
AS RANGE RIGHT FOR VALUES (
    '2026-01-01','2026-02-01','2026-03-01','2026-04-01','2026-05-01','2026-06-01',
    '2026-07-01','2026-08-01','2026-09-01','2026-10-01','2026-11-01','2026-12-01',
    '2027-01-01','2027-02-01','2027-03-01','2027-04-01','2027-05-01','2027-06-01',
    '2027-07-01','2027-08-01','2027-09-01','2027-10-01','2027-11-01','2027-12-01'
);
GO

-- All partitions to PRIMARY initially. Move to dedicated filegroups if
-- the archive tier lands on different storage.
CREATE PARTITION SCHEME ps_ByMonth
AS PARTITION pf_ByMonth ALL TO ([PRIMARY]);
GO


/* =====================================================================
   2. PLATFORM SETTINGS
   FIX (v1.0, finding 4): retention and housekeeping parameters existed
   only as SQL comments in v0.2 — there was nowhere to store them.
   StagingMonthsOnline is the single largest cost driver in the system.
   ===================================================================== */

CREATE TABLE cfg.PlatformSetting (
    SettingKey          VARCHAR(80)   NOT NULL PRIMARY KEY,
    SettingValue        NVARCHAR(300) NOT NULL,
    DataType            VARCHAR(20)   NOT NULL,   -- Int | Decimal | Bool | String
    Description         NVARCHAR(500) NOT NULL,
    ModifiedAt          DATETIME2(3)  NOT NULL DEFAULT SYSDATETIME(),
    ModifiedBy          NVARCHAR(300) NULL,
    CONSTRAINT CK_PlatformSetting_Type CHECK
(DataType COLLATE Latin1_General_CS_AS IN
 ('Int','Decimal','Bool','String'))
);
GO


/* =====================================================================
   3. CONFIGURATION — currency, counterparties and datasets
   ===================================================================== */

/* Minor units drive every integer amount in the system. JOD has 3
   (fils), USD/EUR have 2 (cents). Hard-coding 3 was finding C4. */
CREATE TABLE cfg.Currency (
    CurrencyCode        CHAR(3) NOT NULL PRIMARY KEY,
    Name                NVARCHAR(300) NOT NULL,
    MinorUnits          TINYINT NOT NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT CK_Currency_MinorUnits CHECK (MinorUnits BETWEEN 0 AND 4)
);
GO

CREATE TABLE cfg.Counterparty (
    CounterpartyId      INT IDENTITY(1,1) PRIMARY KEY,
    Code                VARCHAR(300)  NOT NULL UNIQUE,   -- 'JOPACC'
    Name                NVARCHAR(300) NOT NULL,
    Description         NVARCHAR(500) NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CreatedBy           NVARCHAR(300) NOT NULL,
    ModifiedAt          DATETIME2(3) NULL,
    ModifiedBy          NVARCHAR(300) NULL
);
GO

CREATE TABLE cfg.Dataset (
    DatasetId           INT IDENTITY(1,1) PRIMARY KEY,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    Code                VARCHAR(300)  NOT NULL UNIQUE,   -- 'CLIQ_SESSION', 'OM_TXN'
    Name                NVARCHAR(300) NOT NULL,
    ProviderType        VARCHAR(20)   NOT NULL,          -- File | Sql | Api
    TimeZone            VARCHAR(300)  NOT NULL DEFAULT 'Asia/Amman',
    DefaultCurrency     CHAR(3)       NULL REFERENCES cfg.Currency(CurrencyCode),
    DuplicateKeyFields  NVARCHAR(400) NULL,   -- comma-separated FieldCodes; duplicates flagged pre-match (C3)
    IsActive            BIT NOT NULL DEFAULT 0,          -- activation gate
    ActivatedAt         DATETIME2(3) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CreatedBy           NVARCHAR(300) NOT NULL,
    CONSTRAINT CK_Dataset_Provider CHECK
(ProviderType COLLATE Latin1_General_CS_AS IN
 ('File','Sql','Api'))
);
GO


/* ---------------------------------------------------------------------
   3.1 SLOT CATALOGUE
   Tells the UI which slots exist, of what type, and whether a slot is
   reservable as a field's own storage or only as a normalization
   companion.

   FIX (v1.0, finding 2): IsNormalizedOnly separates the two pools. In
   v0.2 a normalized companion slot could silently collide with another
   field's StorageSlot, because UQ_DatasetField_Slot guarded StorageSlot
   alone. The parse-time normalizer would then overwrite a mapped field.
   Text21..Text30 are now companion-only and are never offered as a
   StorageSlot.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.StorageSlotCatalogue (
    SlotName            VARCHAR(20) NOT NULL PRIMARY KEY,   -- 'Text1'
    SlotType            VARCHAR(20) NOT NULL,               -- String|Integer|Decimal|DateTime|Boolean
    MaxLength           INT NULL,
    IsNormalizedOnly    BIT NOT NULL DEFAULT 0,             -- companion pool, never a StorageSlot
    CONSTRAINT CK_SlotCatalogue_Type CHECK
(SlotType COLLATE Latin1_General_CS_AS IN
 ('String','Integer','Decimal','DateTime','Boolean'))
);
GO


/* ---------------------------------------------------------------------
   3.2 FIELD REGISTRY — the heart of the dynamic design.
   StorageSlot is the physical column in stg.StagingTransaction.
   Nothing outside the provider layer ever references a slot directly.

   IsMatchable is the security boundary: rule conditions resolve field
   names against this table only, never from free user text.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.DatasetField (
    DatasetFieldId      INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    FieldCode           VARCHAR(300)  NOT NULL,   -- stable internal code
    DisplayLabel        NVARCHAR(300) NOT NULL,   -- what Operations sees
    DataType            VARCHAR(20)   NOT NULL,   -- String|Integer|Decimal|DateTime|Boolean
    FieldRole           VARCHAR(30)   NULL,       -- see CK_DatasetField_Role
    StorageSlot         VARCHAR(20)   NOT NULL REFERENCES cfg.StorageSlotCatalogue(SlotName),
    IsMatchable         BIT NOT NULL DEFAULT 1,   -- may appear in a rule  <-- security boundary
    IsIndexed           BIT NOT NULL DEFAULT 0,   -- maintain an index on it
    IsRequired          BIT NOT NULL DEFAULT 0,
    NormalizeForMatch   BIT NOT NULL DEFAULT 0,   -- populates a companion slot at parse time (A2)
    NormalizedSlot      VARCHAR(20)   NULL REFERENCES cfg.StorageSlotCatalogue(SlotName),
    DisplayOrder        INT NOT NULL DEFAULT 0,
    CONSTRAINT UQ_DatasetField_Code UNIQUE (DatasetId, FieldCode),
    CONSTRAINT UQ_DatasetField_Slot UNIQUE (DatasetId, StorageSlot),
    -- NOTE: the companion slot's uniqueness is a FILTERED index below, not
    -- a UNIQUE constraint. SQL Server treats NULLs as equal in a unique
    -- constraint, so UNIQUE (DatasetId, NormalizedSlot) would allow only
    -- ONE field per dataset without a companion slot — and most fields
    -- have none.
    CONSTRAINT CK_DatasetField_Type CHECK
(DataType COLLATE Latin1_General_CS_AS IN
 ('String','Integer','Decimal','DateTime','Boolean')),
    -- FIX (finding, C7): FieldRole was an uncontrolled comment. The
    -- engine reads semantics from this column (it looks for the field
    -- whose role is Amount, never for a field named "Amount"), so a typo
    -- here silently disables control totals and fee calculation.
    CONSTRAINT CK_DatasetField_Role CHECK (FieldRole IS NULL OR FieldRole IN
        ('Reference','OriginalReference','Amount','Currency','Date',
         'Direction','Status','Party','TransactionType','Other')),
    -- A normalized companion only makes sense for a string field.
    CONSTRAINT CK_DatasetField_Norm CHECK
        (NormalizeForMatch = 0 OR (NormalizedSlot IS NOT NULL AND DataType = 'String')),
    CONSTRAINT CK_DatasetField_NormSlotSet CHECK
        (NormalizedSlot IS NULL OR NormalizeForMatch = 1)
);
GO

/* FIX (finding 2, part 2): one dataset cannot point two fields at the
   same companion slot. Filtered, so the many fields with no companion are
   unaffected. */
CREATE UNIQUE INDEX UX_DatasetField_NormSlot
    ON cfg.DatasetField (DatasetId, NormalizedSlot)
    WHERE NormalizedSlot IS NOT NULL;
GO

/* Enforces the two-pool rule that a CHECK constraint cannot express:
   StorageSlot must come from the reservable pool, NormalizedSlot from
   the companion pool. Without this, finding 2 remains open. */
CREATE TRIGGER cfg.TR_DatasetField_SlotPool
ON cfg.DatasetField
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN cfg.StorageSlotCatalogue c ON c.SlotName = i.StorageSlot
        WHERE c.IsNormalizedOnly = 1
    )
    BEGIN
        THROW 50001, 'StorageSlot must come from the reservable pool; this slot is companion-only.', 1;
    END

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN cfg.StorageSlotCatalogue c ON c.SlotName = i.NormalizedSlot
        WHERE c.IsNormalizedOnly = 0
    )
    BEGIN
        THROW 50002, 'NormalizedSlot must come from the companion pool (Text21..Text30).', 1;
    END

    -- The slot type must match the field's declared type.
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN cfg.StorageSlotCatalogue c ON c.SlotName = i.StorageSlot
        WHERE c.SlotType <> i.DataType
    )
    BEGIN
        THROW 50003, 'StorageSlot type does not match the field DataType.', 1;
    END
END
GO


/* ---------------------------------------------------------------------
   3.3 FILE SOURCES — versioned and effective-dated, so a layout change
   never breaks the ability to re-read historical files.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.FileFormatDefinition (
    FileFormatId        INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    FormatType          VARCHAR(20)  NOT NULL,     -- Csv|Xml|Json|FixedWidth
    Version             INT          NOT NULL DEFAULT 1,
    EffectiveFrom       DATE         NOT NULL,
    EffectiveTo         DATE         NULL,
    Delimiter           VARCHAR(300) NULL,         -- Csv
    TextQualifier       VARCHAR(300) NULL,
    Encoding            VARCHAR(300) NOT NULL DEFAULT 'UTF-8',
    HasHeader           BIT          NOT NULL DEFAULT 1,
    SkipLeadingLines    INT          NOT NULL DEFAULT 0,
    SkipTrailingLines   INT          NOT NULL DEFAULT 0,
    RecordPath          NVARCHAR(400) NULL,        -- XPath / JsonPath record root
    FileNamePattern     NVARCHAR(400) NULL,        -- regex for validation
    MaxParseErrors      INT          NOT NULL DEFAULT 1000,  -- E4: abort the file beyond this
    CONSTRAINT CK_FileFormat_Type CHECK
(FormatType COLLATE Latin1_General_CS_AS IN
 ('Csv','Xml','Json','FixedWidth')),
    CONSTRAINT CK_FileFormat_Effective CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom),
    CONSTRAINT UQ_FileFormat_Version UNIQUE (DatasetId, Version)
);
GO

CREATE TABLE cfg.FieldMapping (
    FieldMappingId      INT IDENTITY(1,1) PRIMARY KEY,
    FileFormatId        INT NOT NULL REFERENCES cfg.FileFormatDefinition(FileFormatId),
    DatasetFieldId      INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    SourcePath          NVARCHAR(400) NOT NULL,    -- column name/ordinal | XPath | JsonPath | start:length
    ParseFormat         NVARCHAR(300) NULL,        -- 'yyyyMMddHHmmss', '#,##0.000'
    -- FIX (v1.0, B7): was a pipe-delimited string 'Trim|Upper|...', which
    -- is fragile to parse and impossible to validate. Now a JSON array:
    -- [{"op":"Trim"},{"op":"Substring","start":0,"length":12}]
    TransformChainJson  NVARCHAR(MAX) NULL,
    DefaultValue        NVARCHAR(300) NULL,
    IsRequired          BIT NOT NULL DEFAULT 0,
    CONSTRAINT UQ_FieldMapping UNIQUE (FileFormatId, DatasetFieldId),
    CONSTRAINT CK_FieldMapping_Transform CHECK
        (TransformChainJson IS NULL OR ISJSON(TransformChainJson) = 1)
);
GO


/* ---------------------------------------------------------------------
   3.4 SQL SOURCES — view or stored procedure, interchangeable.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.SqlSourceDefinition (
    SqlSourceId         INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    ObjectType          VARCHAR(20)   NOT NULL,    -- View | StoredProcedure
    ObjectName          NVARCHAR(300) NOT NULL,    -- validated against sys.objects on save
    ConnectionName      VARCHAR(300)  NOT NULL,    -- named connection, never a raw string
    -- FIX (D1): was FilterExpression, free SQL text — an injection surface
    -- in the exact place the design promised none. Now the same structured
    -- condition tree used by rules, compiled by the same single class.
    FilterJson          NVARCHAR(MAX) NULL,
    CommandTimeoutSec   INT NOT NULL DEFAULT 600,
    CONSTRAINT CK_SqlSource_Type CHECK
(ObjectType COLLATE Latin1_General_CS_AS IN
 ('View','StoredProcedure')),
    CONSTRAINT CK_SqlSource_Filter CHECK (FilterJson IS NULL OR ISJSON(FilterJson) = 1)
);
GO

CREATE TABLE cfg.SqlSourceParameter (
    SqlSourceParameterId INT IDENTITY(1,1) PRIMARY KEY,
    SqlSourceId         INT NOT NULL REFERENCES cfg.SqlSourceDefinition(SqlSourceId),
    ParameterName       VARCHAR(300) NOT NULL,     -- '@FromDate'
    ValueSource         VARCHAR(40)  NOT NULL,     -- see CHECK
    ConstantValue       NVARCHAR(300) NULL,
    DataType            VARCHAR(20)  NOT NULL,
    CONSTRAINT UQ_SqlSourceParameter UNIQUE (SqlSourceId, ParameterName),
    CONSTRAINT CK_SqlSourceParam_Type CHECK
(DataType COLLATE Latin1_General_CS_AS IN
 ('String','Integer','Decimal','DateTime','Boolean')),
    CONSTRAINT CK_SqlSourceParam_Source CHECK
(ValueSource COLLATE Latin1_General_CS_AS IN
 ('RunDate','RunDateFrom','RunDateTo','SessionRef','Constant')),
    CONSTRAINT CK_SqlSourceParam_Constant CHECK
        (ValueSource <> 'Constant' OR ConstantValue IS NOT NULL)
);
GO

/* Column name -> registry field, for SQL sources (mirrors FieldMapping). */
CREATE TABLE cfg.SqlFieldMapping (
    SqlFieldMappingId   INT IDENTITY(1,1) PRIMARY KEY,
    SqlSourceId         INT NOT NULL REFERENCES cfg.SqlSourceDefinition(SqlSourceId),
    DatasetFieldId      INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    SourceColumn        NVARCHAR(300) NOT NULL,
    TransformChainJson  NVARCHAR(MAX) NULL,
    CONSTRAINT UQ_SqlFieldMapping UNIQUE (SqlSourceId, DatasetFieldId),
    CONSTRAINT CK_SqlFieldMapping_Transform CHECK
        (TransformChainJson IS NULL OR ISJSON(TransformChainJson) = 1)
);
GO


/* ---------------------------------------------------------------------
   3.5 ACQUISITION — how a file arrives.
   Secrets are NEVER stored here; CredentialRef is a key into the secret
   store (D4), and RequestTemplate must not embed credentials.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.AcquisitionDefinition (
    AcquisitionId       INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    Method              VARCHAR(20)   NOT NULL,    -- Api | Sftp | Folder | Manual
    Endpoint            NVARCHAR(500) NULL,
    HttpMethod          VARCHAR(300)  NULL,
    CredentialRef       VARCHAR(300)  NULL,        -- key into the secret store, NEVER a secret
    RequestTemplate     NVARCHAR(MAX) NULL,
    StorageRootPath     NVARCHAR(400) NOT NULL,    -- original files live on disk, not in the DB
    RetryCount          INT NOT NULL DEFAULT 3,
    RetryDelaySeconds   INT NOT NULL DEFAULT 60,
    CONSTRAINT CK_Acquisition_Method CHECK
(Method COLLATE Latin1_General_CS_AS IN
 ('Api','Sftp','Folder','Manual'))
);
GO


/* =====================================================================
   4. RECONCILIATION DEFINITIONS AND RULES
   ===================================================================== */

CREATE TABLE cfg.ReconciliationDefinition (
    DefinitionId        INT IDENTITY(1,1) PRIMARY KEY,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    Code                VARCHAR(300)  NOT NULL UNIQUE,
    Name                NVARCHAR(300) NOT NULL,
    LeftDatasetId       INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    RightDatasetId      INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    -- C5: which date range the providers pull for a business date.
    -- Late arrivals mean the window is not simply "= BusinessDate".
    MatchingWindowDaysBefore INT NOT NULL DEFAULT 1,
    MatchingWindowDaysAfter  INT NOT NULL DEFAULT 1,
    Version             INT NOT NULL DEFAULT 1,     -- informational; reproducibility comes from the run snapshot
    IsActive            BIT NOT NULL DEFAULT 0,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CreatedBy           NVARCHAR(300) NOT NULL,
    CONSTRAINT CK_ReconDef_Sides CHECK (LeftDatasetId <> RightDatasetId),
    CONSTRAINT CK_ReconDef_Window CHECK
        (MatchingWindowDaysBefore >= 0 AND MatchingWindowDaysAfter >= 0)
);
GO

CREATE TABLE cfg.MatchRule (
    MatchRuleId         INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    RuleCode            VARCHAR(300)  NOT NULL,
    Name                NVARCHAR(300) NOT NULL,
    Sequence            INT NOT NULL,               -- pass order
    -- FIX (v1.0, finding 9): these were NVARCHAR(1000) while every other
    -- column holding the same condition-tree schema was NVARCHAR(MAX).
    -- One schema, one compiler — and one of them silently truncated.
    LeftFilterJson      NVARCHAR(MAX) NULL,
    RightFilterJson     NVARCHAR(MAX) NULL,
    MatchMode           VARCHAR(20) NOT NULL DEFAULT 'Row',   -- Row | Aggregate (C1)
    LeftGroupByFields   NVARCHAR(400) NULL,   -- FieldCodes; Aggregate mode only
    RightGroupByFields  NVARCHAR(400) NULL,
    AggregateFunction   VARCHAR(20) NULL,     -- Sum | Count | SumAndCount
    Cardinality         VARCHAR(20) NOT NULL DEFAULT 'OneToOne',
    OnMultipleMatch     VARCHAR(20) NOT NULL DEFAULT 'MarkAmbiguous',
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_MatchRule UNIQUE (DefinitionId, Sequence),
    CONSTRAINT UQ_MatchRule_Code UNIQUE (DefinitionId, RuleCode),
    CONSTRAINT CK_MatchRule_Card CHECK
(Cardinality COLLATE Latin1_General_CS_AS IN
 ('OneToOne','OneToMany')),
    CONSTRAINT CK_MatchRule_Multi CHECK
(OnMultipleMatch COLLATE Latin1_General_CS_AS IN
 ('MarkAmbiguous','TakeEarliest','Fail')),
    CONSTRAINT CK_MatchRule_Mode CHECK
(MatchMode COLLATE Latin1_General_CS_AS IN
 ('Row','Aggregate')),
    CONSTRAINT CK_MatchRule_AggFn CHECK
        (AggregateFunction IS NULL OR AggregateFunction IN ('Sum','Count','SumAndCount')),
    -- Aggregate mode without a group key is a full-dataset sum by accident.
    CONSTRAINT CK_MatchRule_AggFields CHECK
        (MatchMode = 'Row' OR (LeftGroupByFields IS NOT NULL AND AggregateFunction IS NOT NULL)),
    CONSTRAINT CK_MatchRule_LeftFilter  CHECK (LeftFilterJson  IS NULL OR ISJSON(LeftFilterJson)  = 1),
    CONSTRAINT CK_MatchRule_RightFilter CHECK (RightFilterJson IS NULL OR ISJSON(RightFilterJson) = 1)
);
GO

/* The field pairs the user picks in the UI. Both sides resolve to the
   field registry — never to free text. This is what makes a user-built
   rule builder safe against SQL injection.

   'Normalized' is deliberately absent (A2): normalizing inside a join
   predicate is non-sargable and defeats every index. A field flagged
   NormalizeForMatch gets a companion slot filled at parse time, and the
   rule compares that companion with 'Exact'. */
CREATE TABLE cfg.MatchCondition (
    MatchConditionId    INT IDENTITY(1,1) PRIMARY KEY,
    MatchRuleId         INT NOT NULL REFERENCES cfg.MatchRule(MatchRuleId),
    LeftFieldId         INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    RightFieldId        INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    ComparisonType      VARCHAR(30) NOT NULL,
    ToleranceValue      BIGINT NULL,                -- minor units, or numeric span
    ToleranceUnit       VARCHAR(20) NULL,           -- MinorUnit|Minute|Hour|Day
    /* Compare the parse-time normalized companions rather than the raw fields.

       FIX (found by execution): this was not modelled at all, so the engine
       inferred it from the fields — "both sides have a companion, therefore
       compare the companions". That silently turned pass 1, the clean
       reference match, into a second normalized pass: every condition on a
       normalizable field used the companion whether the rule asked or not.
       Whether to normalize is the rule's intent, not a property of the
       fields, and the portal's rule builder already offered it as a
       per-condition switch. */
    UseNormalized       BIT NOT NULL DEFAULT 0,
    Sequence            INT NOT NULL DEFAULT 1,
    CONSTRAINT CK_MatchCondition_Cmp CHECK
(ComparisonType COLLATE Latin1_General_CS_AS IN
        ('Exact','NumericExact','NumericTolerance',
         'DateExact','DateWithin','StartsWith','EndsWith','Contains')),
    CONSTRAINT CK_MatchCondition_Unit CHECK
        (ToleranceUnit IS NULL OR ToleranceUnit IN ('MinorUnit','Minute','Hour','Day')),
    -- A tolerance comparison without a tolerance is an exact match the
    -- user did not ask for.
    CONSTRAINT CK_MatchCondition_Tolerance CHECK
        (ComparisonType NOT IN ('NumericTolerance','DateWithin')
         OR (ToleranceValue IS NOT NULL AND ToleranceUnit IS NOT NULL))
);
GO

/* C6: rows that must not match (e.g. Status = RJCT) leave the working
   set before pass 1 and are reported separately — they never become
   exceptions. */
CREATE TABLE cfg.ExclusionRule (
    ExclusionRuleId     INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    Name                NVARCHAR(300) NOT NULL,
    ConditionJson       NVARCHAR(MAX) NOT NULL,
    ReasonCode          VARCHAR(300) NOT NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT CK_ExclusionRule_Json CHECK (ISJSON(ConditionJson) = 1)
);
GO
CREATE INDEX IX_ExclusionRule_Dataset ON cfg.ExclusionRule (DatasetId) WHERE IsActive = 1;
GO

CREATE TABLE cfg.ClassificationRule (
    ClassificationRuleId INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    ExceptionCode       VARCHAR(300)  NOT NULL,     -- FAILED_INWARD, MISSING_IN_CLIQ ...
    DisplayName         NVARCHAR(300) NOT NULL,
    AppliesToSide       VARCHAR(10)   NOT NULL,     -- Left | Right | Both
    ConditionJson       NVARCHAR(MAX) NOT NULL,     -- structured condition tree
    ActionType          VARCHAR(30)   NOT NULL DEFAULT 'ReportOnly',
    Severity            VARCHAR(20)   NOT NULL DEFAULT 'Normal',
    Sequence            INT NOT NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    -- ActionType is ReportOnly in this phase. AutoPost exists as a seam
    -- for future Failed Inward posting; it is not implemented.
    CONSTRAINT CK_Classification_Action CHECK
(ActionType COLLATE Latin1_General_CS_AS IN
 ('ReportOnly','AutoClose','AutoPost')),
    CONSTRAINT CK_Classification_Side CHECK
(AppliesToSide COLLATE Latin1_General_CS_AS IN
 ('Left','Right','Both')),
    CONSTRAINT CK_Classification_Severity CHECK
(Severity COLLATE Latin1_General_CS_AS IN
 ('Low','Normal','High','Critical')),
    CONSTRAINT CK_Classification_Json CHECK (ISJSON(ConditionJson) = 1),
    CONSTRAINT UQ_Classification_Seq UNIQUE (DefinitionId, Sequence)
);
GO

CREATE TABLE cfg.ControlTotalDefinition (
    ControlTotalId      INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    CheckCode           VARCHAR(300)  NOT NULL,     -- DEBIT_TOTAL, CREDIT_COUNT ...
    DisplayName         NVARCHAR(300) NOT NULL,
    -- FIX (finding 9): widened from NVARCHAR(1000) to MAX, same as every
    -- other structured-spec column.
    SourceAExpressionJson NVARCHAR(MAX) NOT NULL,   -- structured aggregate spec
    SourceBExpressionJson NVARCHAR(MAX) NOT NULL,
    Scope               VARCHAR(20) NOT NULL DEFAULT 'Run',        -- Run | BusinessDate | Period
    SourceTypeA         VARCHAR(20) NOT NULL DEFAULT 'Staging',    -- Staging|MatchResult|RunAggregate|Dataset
    SourceTypeB         VARCHAR(20) NOT NULL DEFAULT 'Dataset',
    PeriodDays          INT NULL,                                  -- Period scope: window length
    -- FIX (C4): was ToleranceFils. The design doc already called this
    -- ToleranceMinor; the DDL had not followed. Default 0 = exact.
    ToleranceMinor      BIGINT NOT NULL DEFAULT 0,
    FailRunOnMismatch   BIT NOT NULL DEFAULT 1,     -- a net difference fails the run
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_ControlTotal_Code UNIQUE (DefinitionId, CheckCode),
    CONSTRAINT CK_ControlTotal_Scope CHECK
(Scope COLLATE Latin1_General_CS_AS IN
 ('Run','BusinessDate','Period')),
    CONSTRAINT CK_ControlTotal_SrcA CHECK
(SourceTypeA COLLATE Latin1_General_CS_AS IN
 ('Staging','MatchResult','RunAggregate','Dataset')),
    CONSTRAINT CK_ControlTotal_SrcB CHECK
(SourceTypeB COLLATE Latin1_General_CS_AS IN
 ('Staging','MatchResult','RunAggregate','Dataset')),
    -- FIX (v1.0, found by execution): the original read
    --   (Scope <> 'Period' OR PeriodDays > 0)
    -- With PeriodDays NULL that is FALSE OR UNKNOWN = UNKNOWN, and a CHECK
    -- only rejects on FALSE — so a Period-scoped check with no window was
    -- accepted. Three-valued logic needs the NULL stated explicitly.
    CONSTRAINT CK_ControlTotal_Period CHECK
        (Scope <> 'Period' OR (PeriodDays IS NOT NULL AND PeriodDays > 0)),
    CONSTRAINT CK_ControlTotal_JsonA CHECK (ISJSON(SourceAExpressionJson) = 1),
    CONSTRAINT CK_ControlTotal_JsonB CHECK (ISJSON(SourceBExpressionJson) = 1)
);
GO


/* =====================================================================
   5. SCHEDULING AND ALERTING
   ===================================================================== */

CREATE TABLE cfg.ScheduleDefinition (
    ScheduleId          INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    CronExpression      VARCHAR(300)  NOT NULL,
    TimeZone            VARCHAR(300)  NOT NULL DEFAULT 'Asia/Amman',
    ExpectedFileByTime  TIME NULL,                  -- drives "file not received" alerts
    IsEnabled           BIT NOT NULL DEFAULT 1
);
GO

CREATE TABLE cfg.AlertPolicy (
    AlertPolicyId       INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    EventType           VARCHAR(40)  NOT NULL,
    ThresholdValue      DECIMAL(18,3) NULL,
    Channel             VARCHAR(20)  NOT NULL,      -- Email|Sms|Dashboard
    Recipients          NVARCHAR(1000) NULL,
    IsEnabled           BIT NOT NULL DEFAULT 1,
    -- FIX (E6): the design promised a match-distribution-drift signal but
    -- left the event type as an uncontrolled comment. Constrained now, so
    -- a typo cannot silently disable an alert.
    CONSTRAINT CK_AlertPolicy_Event CHECK
(EventType COLLATE Latin1_General_CS_AS IN
        ('FileNotReceived','RunFailed','ControlTotalMismatch','ThresholdBreach',
         'MatchDistributionDrift','PartitionShortage','ParseErrorLimit')),
    CONSTRAINT CK_AlertPolicy_Channel CHECK
(Channel COLLATE Latin1_General_CS_AS IN
 ('Email','Sms','Dashboard'))
);
GO


/* =====================================================================
   6. FEES AND INTERCHANGE
   All amounts in MINOR UNITS of the schedule's currency (C4).
   ===================================================================== */

CREATE TABLE cfg.FeeSchedule (
    FeeScheduleId       INT IDENTITY(1,1) PRIMARY KEY,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    Code                VARCHAR(300)  NOT NULL,
    Name                NVARCHAR(300) NOT NULL,
    Direction           VARCHAR(20)   NOT NULL,     -- Inward | Outward
    TransactionType     VARCHAR(300)  NULL,
    FeeParty            VARCHAR(20)   NOT NULL,     -- Revenue (to OM) | Cost (by OM)
    -- FIX (finding 11): was an unconstrained CHAR(3); now a real FK, so a
    -- fee schedule cannot reference a currency whose minor units are
    -- unknown to the parser.
    CurrencyCode        CHAR(3)       NOT NULL DEFAULT 'JOD'
                            REFERENCES cfg.Currency(CurrencyCode),
    -- STILL OPEN (C8): HalfUp is a guess. Obtain the counterparty's
    -- stated rounding rule before Phase 5 — fee development must not
    -- start without it, or netting will never tie out.
    RoundingMode        VARCHAR(20)   NOT NULL DEFAULT 'HalfUp',
    EffectiveFrom       DATE          NOT NULL,
    EffectiveTo         DATE          NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_FeeSchedule_Code UNIQUE (CounterpartyId, Code),
    CONSTRAINT CK_FeeSchedule_Party CHECK
(FeeParty COLLATE Latin1_General_CS_AS IN
 ('Revenue','Cost')),
    CONSTRAINT CK_FeeSchedule_Direction CHECK
(Direction COLLATE Latin1_General_CS_AS IN
 ('Inward','Outward')),
    CONSTRAINT CK_FeeSchedule_Round CHECK
(RoundingMode COLLATE Latin1_General_CS_AS IN
 ('HalfUp','HalfEven','Truncate')),
    CONSTRAINT CK_FeeSchedule_Effective CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom)
);
GO

CREATE TABLE cfg.FeeTier (
    FeeTierId           INT IDENTITY(1,1) PRIMARY KEY,
    FeeScheduleId       INT NOT NULL REFERENCES cfg.FeeSchedule(FeeScheduleId),
    AmountFromMinor     BIGINT NOT NULL,
    AmountToMinor       BIGINT NULL,                -- NULL = open-ended top tier
    CalculationType     VARCHAR(30) NOT NULL,       -- Fixed|Percentage|FixedPlusPercentage
    FixedAmountMinor    BIGINT NOT NULL DEFAULT 0,
    Percentage          DECIMAL(9,6) NOT NULL DEFAULT 0,
    MinFeeMinor         BIGINT NULL,
    MaxFeeMinor         BIGINT NULL,
    CONSTRAINT CK_FeeTier_Calc CHECK
(CalculationType COLLATE Latin1_General_CS_AS IN
 ('Fixed','Percentage','FixedPlusPercentage')),
    CONSTRAINT CK_FeeTier_Band CHECK (AmountToMinor IS NULL OR AmountToMinor > AmountFromMinor),
    CONSTRAINT CK_FeeTier_Cap CHECK
        (MinFeeMinor IS NULL OR MaxFeeMinor IS NULL OR MaxFeeMinor >= MinFeeMinor)
);
GO

/* Whether fees apply at all for a given dataset/transaction type —
   confirmed that some reconciliation reports carry fees and some do not. */
CREATE TABLE cfg.FeeApplicability (
    FeeApplicabilityId  INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    TransactionType     VARCHAR(300) NULL,          -- NULL = all types
    IsFeeApplicable     BIT NOT NULL,
    FeeScheduleId       INT NULL REFERENCES cfg.FeeSchedule(FeeScheduleId),
    CONSTRAINT CK_FeeApplicability_Schedule CHECK
        (IsFeeApplicable = 0 OR FeeScheduleId IS NOT NULL)
);
GO
/* One ruling per (dataset, transaction type). Two contradictory rows —
   one saying fees apply, one saying they do not — would make fee
   calculation depend on row order. TransactionType is nullable (NULL =
   all types), so this needs two filtered indexes rather than one
   constraint. */
CREATE UNIQUE INDEX UX_FeeApplicability_Type
    ON cfg.FeeApplicability (DatasetId, TransactionType)
    WHERE TransactionType IS NOT NULL;
GO
CREATE UNIQUE INDEX UX_FeeApplicability_Default
    ON cfg.FeeApplicability (DatasetId)
    WHERE TransactionType IS NULL;
GO


/* =====================================================================
   7. REPORT DEFINITIONS
   ===================================================================== */

CREATE TABLE cfg.ReportDefinition (
    ReportDefinitionId  INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    Code                VARCHAR(300)  NOT NULL,
    Name                NVARCHAR(300) NOT NULL,
    SheetName           NVARCHAR(300) NOT NULL,
    DataScope           VARCHAR(40)   NOT NULL,     -- Matched|Unmatched|Exceptions|ControlTotals|All
    FilterJson          NVARCHAR(MAX) NULL,
    SortJson            NVARCHAR(MAX) NULL,
    -- E1: a worksheet holds 1,048,576 rows; a 2M-row day does not fit.
    -- Full-transaction scopes default to CSV with a streaming writer;
    -- Excel splits sheets automatically at the row cap.
    OutputFormat        VARCHAR(20)   NOT NULL DEFAULT 'Xlsx',
    IncludeSubtotals    BIT NOT NULL DEFAULT 0,
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_ReportDefinition_Code UNIQUE (DefinitionId, Code),
    CONSTRAINT CK_Report_Scope CHECK
(DataScope COLLATE Latin1_General_CS_AS IN
 ('Matched','Unmatched','Exceptions','ControlTotals','All')),
    CONSTRAINT CK_Report_Format CHECK
(OutputFormat COLLATE Latin1_General_CS_AS IN
 ('Xlsx','Csv')),
    CONSTRAINT CK_Report_FilterJson CHECK (FilterJson IS NULL OR ISJSON(FilterJson) = 1),
    CONSTRAINT CK_Report_SortJson CHECK (SortJson IS NULL OR ISJSON(SortJson) = 1)
);
GO

CREATE TABLE cfg.ReportColumn (
    ReportColumnId      INT IDENTITY(1,1) PRIMARY KEY,
    ReportDefinitionId  INT NOT NULL REFERENCES cfg.ReportDefinition(ReportDefinitionId),
    DatasetFieldId      INT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    ComputedField       VARCHAR(300)  NULL,         -- ExceptionCode, MatchedByRuleCode ...
    Header              NVARCHAR(300) NOT NULL,
    DisplayFormat       NVARCHAR(300) NULL,
    ColumnWidth         INT NULL,
    Sequence            INT NOT NULL,
    CONSTRAINT CK_ReportColumn_Source CHECK
        (DatasetFieldId IS NOT NULL OR ComputedField IS NOT NULL),
    CONSTRAINT UQ_ReportColumn_Seq UNIQUE (ReportDefinitionId, Sequence)
);
GO


/* =====================================================================
   8. ACCESS CONTROL — scoped per counterparty (design §13)
   ===================================================================== */

CREATE TABLE cfg.UserCounterpartyAccess (
    AccessId            INT IDENTITY(1,1) PRIMARY KEY,
    UserName            NVARCHAR(300) NOT NULL,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    AccessLevel         VARCHAR(20) NOT NULL,       -- Read|Operate|Configure
    GrantedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    GrantedBy           NVARCHAR(300) NOT NULL,
    CONSTRAINT UQ_UserCounterparty UNIQUE (UserName, CounterpartyId),
    CONSTRAINT CK_AccessLevel CHECK
(AccessLevel COLLATE Latin1_General_CS_AS IN
 ('Read','Operate','Configure'))
);
GO


/* =====================================================================
   9. OPERATIONAL — runs and files
   ===================================================================== */

CREATE TABLE ops.ReconRun (
    RunId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    DefinitionVersion   INT NOT NULL,               -- informational label only
    BusinessDate        DATE NOT NULL,
    SessionRef          NVARCHAR(300) NULL,
    RunType             VARCHAR(20) NOT NULL DEFAULT 'Scheduled',
    Status              VARCHAR(20) NOT NULL DEFAULT 'Pending',

    /* B1 · Reproducibility. DefinitionVersion alone proves nothing: rules
       are edited in place, so version 3 today is not version 3 last month.
       The full effective definition — rules, conditions, classifications,
       control totals, both field registries, fee schedules in effect — is
       serialized here at run start, and THE COMPILER READS FROM THIS
       SNAPSHOT, never from live config. */
    DefinitionSnapshotJson NVARCHAR(MAX) NULL,

    /* Supersession. A rerun never overwrites: it is a NEW run, and the
       prior one is marked IsCurrent = 0 but stays intact and queryable.
       Two distinct links, deliberately not merged:
         SupersedesRunId — the run this one replaces (Rerun and Rematch)
         SourceRunId     — the run whose STAGED ROWS this one reuses
                           (Rematch only; a Rerun re-acquires and re-stages) */
    SupersedesRunId     BIGINT NULL REFERENCES ops.ReconRun(RunId),
    SourceRunId         BIGINT NULL REFERENCES ops.ReconRun(RunId),
    IsCurrent           BIT NOT NULL DEFAULT 1,

    /* FIX (v1.0, finding 1): which run's staged rows does this run read?
       In v0.2 this rule existed nowhere, so every query that filtered
       staging by RunId would read nothing at all on a Rematch. It is now
       stated once, in the schema, and every consumer uses this column. */
    StagingRunId        AS (ISNULL(SourceRunId, RunId)) PERSISTED,

    StartedAt           DATETIME2(3) NULL,
    CompletedAt         DATETIME2(3) NULL,
    LeftRowCnt          BIGINT NULL,
    RightRowCnt         BIGINT NULL,
    MatchedCnt          BIGINT NULL,
    UnmatchedCnt        BIGINT NULL,
    AmbiguousCnt        BIGINT NULL,
    ErrorMessage        NVARCHAR(MAX) NULL,
    TriggeredBy         NVARCHAR(300) NOT NULL,

    CONSTRAINT CK_ReconRun_Status CHECK
(Status COLLATE Latin1_General_CS_AS IN
 ('Pending','Running','Completed','Failed','Cancelled','Rejected','Resuming')),
    -- FIX (v1.0, finding 8): RunType never had a CHECK, yet
    -- UX_ReconRun_Current filters on RunType <> 'Sandbox'. A typo such as
    -- 'sandbox' would silently pull a sandbox run into the uniqueness
    -- scope and into period aggregates.
    CONSTRAINT CK_ReconRun_Type CHECK
(RunType COLLATE Latin1_General_CS_AS IN
 ('Scheduled','Manual','Rerun','Rematch','Sandbox')),
    -- A Rematch without a source run has nothing to rematch.
    CONSTRAINT CK_ReconRun_Rematch CHECK
        (RunType <> 'Rematch' OR SourceRunId IS NOT NULL),
    CONSTRAINT CK_ReconRun_NoSelfRef CHECK
        (SourceRunId <> RunId AND SupersedesRunId <> RunId)
);
GO
CREATE INDEX IX_ReconRun_Def_Date ON ops.ReconRun (DefinitionId, BusinessDate DESC)
    INCLUDE (Status, RunType, IsCurrent);
GO
/* One current run per definition/date/session. Sandbox runs are excluded:
   they write to the same staging table but are never authoritative (E5),
   and a nightly job purges them after 7 days. */
CREATE UNIQUE INDEX UX_ReconRun_Current
    ON ops.ReconRun (DefinitionId, BusinessDate, SessionRef)
    WHERE IsCurrent = 1 AND RunType <> 'Sandbox';
GO
CREATE INDEX IX_ReconRun_Staging ON ops.ReconRun (StagingRunId);
GO

CREATE TABLE ops.ReconRunStep (
    RunStepId           BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId               BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    StepName            VARCHAR(40) NOT NULL,
    MatchRuleId         INT NULL REFERENCES cfg.MatchRule(MatchRuleId),
    /* Which side the step ran against, for the stages that run once per
       dataset: Exclude, Duplicates and Classify.

       FIX (found by execution): without this a step's identity was
       (RunId, StepName, MatchRuleId), so "Classify Left" and "Classify
       Right" were the same checkpoint. The second call found the first
       Completed and skipped it — correctly, by the resume logic's own rules —
       so the right side's exclusions, duplicate detection and classification
       never ran at all. A silent half-reconciliation, produced by a
       checkpoint design that was right about everything except what
       identifies a step. */
    Side                VARCHAR(10) NULL,
    Status              VARCHAR(20) NOT NULL,
    StartedAt           DATETIME2(3) NULL,
    CompletedAt         DATETIME2(3) NULL,
    RowsProcessed       BIGINT NULL,
    RowsMatched         BIGINT NULL,                -- per-pass distribution = data quality signal
    GeneratedSql        NVARCHAR(MAX) NULL,         -- persisted for audit
    ErrorMessage        NVARCHAR(MAX) NULL,
    -- B3 · Each step is a checkpoint; Resume re-executes from the first
    -- non-completed step. Acquire and Parse are idempotent by file hash;
    -- a pass is idempotent by (RunId, MatchRuleId).
    /* FIX (found by executing a sandbox dry-run): 'Reset' was missing.
       A Rematch or Sandbox run reads another run's staged rows, and those
       rows still carry that run's verdict in the MatchStatus cache. Every
       statement before pass 1 filters on MatchStatus = 'Unmatched', so a
       replay over already-matched rows excluded nothing, detected no
       duplicates and matched nothing — while the cached status from the
       earlier run made the aggregates look like a complete success. The
       reset step re-opens those rows for the run about to process them,
       which is what the cache's contract already says: it reflects the most
       recent run over the rows, and ops.MatchResult is the per-run record. */
    CONSTRAINT CK_RunStep_Name CHECK
(StepName COLLATE Latin1_General_CS_AS IN
        ('Acquire','Parse','Stage','Reset','Exclude','Duplicates','Match',
         'Classify','AutoClose','ControlTotals','Aggregate','Fees','Report')),
    CONSTRAINT CK_RunStep_Status CHECK
(Status COLLATE Latin1_General_CS_AS IN
 ('Pending','Running','Completed','Failed','Skipped')),
    CONSTRAINT CK_RunStep_Side CHECK
        (Side IS NULL OR Side COLLATE Latin1_General_CS_AS IN ('Left','Right'))
);
GO
CREATE INDEX IX_ReconRunStep_Run ON ops.ReconRunStep (RunId, StepName);
GO
/* A step is identified by run, name, rule and side. Making that a UNIQUE
   index rather than a convention means a second attempt at a step cannot
   create a duplicate row and then resume from whichever one it read first.
   NULLs compare equal in a unique index, which is exactly right here: there
   is one Aggregate step per run, not one per NULL. */
CREATE UNIQUE INDEX UX_ReconRunStep_Identity
    ON ops.ReconRunStep (RunId, StepName, MatchRuleId, Side);
GO

CREATE TABLE ops.SourceFile (
    SourceFileId        BIGINT IDENTITY(1,1) NOT NULL,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    FileName            NVARCHAR(400) NOT NULL,
    StoragePath         NVARCHAR(1000) NOT NULL,    -- filesystem/object store, NOT a blob column
    FileHash            CHAR(64) NOT NULL,          -- SHA-256: idempotency + integrity
    FileSizeBytes       BIGINT NOT NULL,
    BusinessDate        DATE NOT NULL,
    SessionRef          NVARCHAR(300) NULL,
    ReceivedAt          DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    ProcessedAt         DATETIME2(3) NULL,
    Status              VARCHAR(20) NOT NULL DEFAULT 'Received',
    RowCnt              BIGINT NULL,
    ErrorCnt            BIGINT NULL,
    CONSTRAINT PK_SourceFile PRIMARY KEY (SourceFileId, BusinessDate),
    CONSTRAINT CK_SourceFile_Status CHECK
(Status COLLATE Latin1_General_CS_AS IN
 ('Received','Parsing','Parsed','Failed','Duplicate'))
) ON ps_ByMonth(BusinessDate);
GO
-- Same content received twice is a duplicate, not a second run.
CREATE UNIQUE INDEX UX_SourceFile_Hash
    ON ops.SourceFile (DatasetId, FileHash, BusinessDate) ON ps_ByMonth(BusinessDate);
GO


/* =====================================================================
   10. STAGING — the high-volume table (~2M rows/day)
   SLOT-BASED storage. Field names are resolved through cfg.DatasetField;
   nothing outside the provider layer references a slot directly. Per-
   dataset readable views (generated at activation) restore readability.
   ===================================================================== */

CREATE TABLE stg.StagingTransaction (
    StagingId           BIGINT IDENTITY(1,1) NOT NULL,
    DatasetId           INT    NOT NULL,
    /* FIX (v1.0, finding 1): renamed from RunId. This is the run that
       STAGED the row — not the run that last matched it. A Rematch reuses
       these rows, so the two are different runs and the old single name
       conflated them. Join against ops.ReconRun.StagingRunId. */
    LoadRunId           BIGINT NOT NULL,
    SourceFileId        BIGINT NULL,
    RawRowNumber        INT    NULL,
    TxDate              DATE   NOT NULL,            -- partition column

    /* ---- text slots (Text1..Text20 reservable, Text21..Text30 are
            normalization companions — see cfg.StorageSlotCatalogue) ---- */
    Text1  NVARCHAR(300) NULL, Text2  NVARCHAR(300) NULL, Text3  NVARCHAR(300) NULL,
    Text4  NVARCHAR(300) NULL, Text5  NVARCHAR(300) NULL, Text6  NVARCHAR(300) NULL,
    Text7  NVARCHAR(300) NULL, Text8  NVARCHAR(300) NULL, Text9  NVARCHAR(300) NULL,
    Text10 NVARCHAR(300) NULL, Text11 NVARCHAR(300) NULL, Text12 NVARCHAR(300) NULL,
    Text13 NVARCHAR(300) NULL, Text14 NVARCHAR(300) NULL, Text15 NVARCHAR(300) NULL,
    Text16 NVARCHAR(300) NULL, Text17 NVARCHAR(300) NULL, Text18 NVARCHAR(300) NULL,
    Text19 NVARCHAR(300) NULL, Text20 NVARCHAR(300) NULL, Text21 NVARCHAR(300) NULL,
    Text22 NVARCHAR(300) NULL, Text23 NVARCHAR(300) NULL, Text24 NVARCHAR(300) NULL,
    Text25 NVARCHAR(300) NULL, Text26 NVARCHAR(300) NULL, Text27 NVARCHAR(300) NULL,
    Text28 NVARCHAR(300) NULL, Text29 NVARCHAR(300) NULL, Text30 NVARCHAR(300) NULL,

    /* ---- integer slots: amounts in MINOR UNITS, counts, codes ---- */
    Num1  BIGINT NULL, Num2  BIGINT NULL, Num3  BIGINT NULL, Num4  BIGINT NULL,
    Num5  BIGINT NULL, Num6  BIGINT NULL, Num7  BIGINT NULL, Num8  BIGINT NULL,
    Num9  BIGINT NULL, Num10 BIGINT NULL, Num11 BIGINT NULL, Num12 BIGINT NULL,
    Num13 BIGINT NULL, Num14 BIGINT NULL, Num15 BIGINT NULL,

    /* ---- decimal slots: display/source amounts. NEVER used for matching,
            joining or summing — that is always the integer minor unit ---- */
    Dec1 DECIMAL(18,3) NULL, Dec2 DECIMAL(18,3) NULL, Dec3 DECIMAL(18,3) NULL,
    Dec4 DECIMAL(18,3) NULL, Dec5 DECIMAL(18,3) NULL,

    /* ---- date slots ---- */
    Date1 DATETIME2(3) NULL, Date2 DATETIME2(3) NULL, Date3 DATETIME2(3) NULL,
    Date4 DATETIME2(3) NULL, Date5 DATETIME2(3) NULL, Date6 DATETIME2(3) NULL,
    Date7 DATETIME2(3) NULL, Date8 DATETIME2(3) NULL,

    /* ---- boolean slots ---- */
    Flag1 BIT NULL, Flag2 BIT NULL, Flag3 BIT NULL, Flag4 BIT NULL, Flag5 BIT NULL,

    /* ---- cached outcome of matching ------------------------------------
       These four columns are a CACHE, not the record of truth. Passes write
       only to ops.MatchResult (A3); this is set once, in a single set-based
       update at the end of the run.

       FIX (v1.0, finding 1): ResultRunId stamps WHICH run produced the
       cached values. Without it, a Rematch writing its outcome onto rows
       owned by the source run silently destroyed that run's row-level
       results while the design claimed "reruns never overwrite". The
       authoritative per-run record is ops.MatchResult (partitioned per run
       and never rewritten) plus ops.RunAggregate; this cache only ever
       reflects the most recent run over these rows. */
    ResultRunId         BIGINT NULL,
    MatchStatus         VARCHAR(20) NOT NULL DEFAULT 'Unmatched',
    MatchedWithId       BIGINT NULL,
    MatchedByRuleId     INT NULL,
    ExceptionCode       VARCHAR(300) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),

    CONSTRAINT CK_Staging_MatchStatus CHECK
(MatchStatus COLLATE Latin1_General_CS_AS IN
 ('Unmatched','Matched','Ambiguous','AutoClosed','Excluded','Duplicate'))
) ON ps_ByMonth(TxDate);
GO

/* DatasetId leads the clustered key: every query targets one dataset, so
   SQL Server seeks its range and ignores the rest. Together with monthly
   partitioning on TxDate this gives the same practical isolation as
   separate per-dataset tables — which is what made the slot decision
   defensible in the first place (design §7).

   The bulk load must supply rows in THIS order (SqlBulkCopy ORDER hint):
   sorted input into a clustered index is minimally logged and avoids page
   splits. There is no "load into a heap first, then index" path — this is
   one shared table with a permanent clustered index, and v0.1's comment
   claiming otherwise was blocker A1. */
CREATE CLUSTERED INDEX CIX_Staging
    ON stg.StagingTransaction (DatasetId, TxDate, StagingId)
    ON ps_ByMonth(TxDate);
GO

CREATE UNIQUE NONCLUSTERED INDEX UX_Staging_Id
    ON stg.StagingTransaction (StagingId, TxDate)
    ON ps_ByMonth(TxDate);
GO

/* Rows staged by a given run — used by the Rematch path and by cleanup.
   The v0.1 filtered index on MatchStatus = 'Unmatched' is deliberately
   GONE (A3): passes no longer update staging, so it would only have
   churned on every load. */
CREATE NONCLUSTERED INDEX IX_Staging_LoadRun
    ON stg.StagingTransaction (LoadRunId, DatasetId, TxDate)
    ON ps_ByMonth(TxDate);
GO

/* ---------------------------------------------------------------------
   Matching indexes are generated at dataset ACTIVATION from the active
   rule set, never at run time (A6): one nonclustered index per
   (dataset, field) where the field appears in a pass-1 or pass-2
   condition, CAPPED AT 4 PER DATASET. Every extra index is a 2M-row
   maintenance cost on every load, and the UI shows which fields are
   indexed. Example for a dataset whose primary reference sits in Text1:

   CREATE NONCLUSTERED INDEX IX_Staging_DS1_Text1
       ON stg.StagingTransaction (DatasetId, Text1, TxDate)
       INCLUDE (StagingId, Num1)
       ON ps_ByMonth(TxDate);

   8.1 PER-DATASET READABLE VIEWS — also generated at activation from
   cfg.DatasetField. These remove the only real drawback of slot storage:
   raw tables nobody can read. Creating a view is not a data-structure
   change, so it does not require the DDL permissions that generated
   tables would. Example (illustrative — the platform generates these):

   CREATE VIEW stg.v_CliQ_Session AS
   SELECT StagingId, LoadRunId, ResultRunId, SourceFileId, TxDate,
          Text1 AS EndToEndId,
          Text2 AS TransactionId,
          Text3 AS CreditorAccount,
          Num1  AS AmountMinor,
          Date1 AS TransactionDateTime,
          MatchStatus, MatchedWithId, ExceptionCode
   FROM stg.StagingTransaction
   WHERE DatasetId = 1;
   --------------------------------------------------------------------- */


CREATE TABLE stg.ParseError (
    ParseErrorId        BIGINT IDENTITY(1,1) NOT NULL,
    RunId               BIGINT NOT NULL,
    SourceFileId        BIGINT NULL,
    BusinessDate        DATE   NOT NULL,
    RawRowNumber        INT    NULL,
    ErrorType           VARCHAR(40) NOT NULL,
    FieldCode           VARCHAR(300) NULL,
    ErrorMessage        NVARCHAR(1000) NOT NULL,
    RawLine             NVARCHAR(MAX) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    -- E4: a wrong-format file would otherwise yield 2M rows with RawLine.
    -- cfg.FileFormatDefinition.MaxParseErrors caps this per file.
    CONSTRAINT CK_ParseError_Type CHECK
(ErrorType COLLATE Latin1_General_CS_AS IN
 ('MissingRequired','TypeConversion','FormatInvalid','RowShape','Unknown'))
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_ParseError ON stg.ParseError (RunId, BusinessDate, ParseErrorId)
    ON ps_ByMonth(BusinessDate);
GO


/* =====================================================================
   11. RESULTS
   ops.MatchResult is the RECORD OF TRUTH for matching. Passes write only
   here (A3); staging's status columns are a cache written once at the end
   of the run. "Still unmatched" for pass N is an anti-join against this
   table, so a failed pass is retried by deleting its rows — staging is
   never touched mid-run.
   ===================================================================== */

CREATE TABLE ops.MatchResult (
    MatchResultId       BIGINT IDENTITY(1,1) NOT NULL,
    RunId               BIGINT NOT NULL,
    BusinessDate        DATE   NOT NULL,
    LeftStagingId       BIGINT NULL,                -- NULL = exists only on the right
    RightStagingId      BIGINT NULL,                -- NULL = exists only on the left
    MatchRuleId         INT    NULL,                -- which pass matched it
    MatchStatus         VARCHAR(20) NOT NULL,
    AmountDiffMinor     BIGINT NULL,
    CandidateCount      INT NULL,                   -- >1 drives Ambiguous
    -- C1 · Aggregate mode: the group whose sum matched, on each side.
    LeftAggregateKey    NVARCHAR(300) NULL,
    RightAggregateKey   NVARCHAR(300) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT CK_MatchResult_Status CHECK
(MatchStatus COLLATE Latin1_General_CS_AS IN
 ('Matched','Ambiguous','AmountDifference','Unmatched')),
    -- A result that references neither side is not a result.
    CONSTRAINT CK_MatchResult_Sides CHECK
        (LeftStagingId IS NOT NULL OR RightStagingId IS NOT NULL)
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_MatchResult ON ops.MatchResult (RunId, BusinessDate, MatchResultId)
    ON ps_ByMonth(BusinessDate);
GO
/* The per-run anti-join between passes:
   NOT EXISTS (SELECT 1 FROM ops.MatchResult
               WHERE RunId = @run AND LeftStagingId = s.StagingId) */
CREATE NONCLUSTERED INDEX IX_MatchResult_RunLeft
    ON ops.MatchResult (RunId, LeftStagingId, BusinessDate)
    ON ps_ByMonth(BusinessDate);
GO
CREATE NONCLUSTERED INDEX IX_MatchResult_RunRight
    ON ops.MatchResult (RunId, RightStagingId, BusinessDate)
    ON ps_ByMonth(BusinessDate);
GO
/* Row-level lookup: "what happened to this staged row, in any run". */
CREATE NONCLUSTERED INDEX IX_MatchResult_Left
    ON ops.MatchResult (LeftStagingId, BusinessDate)
    ON ps_ByMonth(BusinessDate);
GO

CREATE TABLE ops.ReconException (
    ExceptionId         BIGINT IDENTITY(1,1) NOT NULL,
    RunId               BIGINT NOT NULL,
    DefinitionId        INT NOT NULL,
    BusinessDate        DATE NOT NULL,
    StagingId           BIGINT NULL,
    Side                VARCHAR(10) NOT NULL,       -- Left | Right
    ExceptionCode       VARCHAR(300) NOT NULL,
    AmountMinor         BIGINT NULL,
    CurrencyCode        CHAR(3) NULL REFERENCES cfg.Currency(CurrencyCode),
    Status              VARCHAR(20) NOT NULL DEFAULT 'Open',

    /* C2 · Late arrivals. An open break matched in a later run closes as
       AutoClosed. The auto-close pass joins today's unmatched rows to open
       exceptions BY THESE SNAPSHOTTED KEY VALUES — {FieldCode: value} of
       the matchable fields — because StagingId points into staging, which
       has a much shorter retention than exceptions do (E3). Without this
       the open-items list grows forever and Operations stops trusting it. */
    KeyValuesJson       NVARCHAR(MAX) NULL,

    /* FIX (v1.0, A7): v0.1 had
         AgeDays AS DATEDIFF(DAY, BusinessDate, CAST(SYSDATETIME() AS DATE))
       A non-deterministic computed column: it cannot be persisted or
       indexed, and it is evaluated for every row on every read. Aging is
       computed in the query layer instead — the portal's aging buckets
       filter on BusinessDate, which IS indexed. */

    AssignedTo          NVARCHAR(300) NULL,
    ResolutionCode      VARCHAR(300) NULL,
    ResolutionNote      NVARCHAR(1000) NULL,
    ClosedByRunId       BIGINT NULL,                -- set when a later run auto-closes it
    ClosedAt            DATETIME2(3) NULL,
    ClosedBy            NVARCHAR(300) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT CK_Exception_Status CHECK
(Status COLLATE Latin1_General_CS_AS IN
 ('Open','InProgress','Resolved','AutoClosed','WrittenOff')),
    CONSTRAINT CK_Exception_Side CHECK
(Side COLLATE Latin1_General_CS_AS IN
 ('Left','Right')),
    CONSTRAINT CK_Exception_Keys CHECK (KeyValuesJson IS NULL OR ISJSON(KeyValuesJson) = 1),
    CONSTRAINT CK_Exception_Closed CHECK
        (Status NOT IN ('Resolved','AutoClosed','WrittenOff') OR ClosedAt IS NOT NULL)
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_Exception ON ops.ReconException (DefinitionId, BusinessDate, ExceptionId)
    ON ps_ByMonth(BusinessDate);
GO
CREATE NONCLUSTERED INDEX IX_Exception_Open
    ON ops.ReconException (DefinitionId, ExceptionCode, BusinessDate)
    INCLUDE (AmountMinor, AssignedTo)
    WHERE Status IN ('Open','InProgress')
    ON ps_ByMonth(BusinessDate);
GO

CREATE TABLE ops.ControlTotalResult (
    ControlTotalResultId BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId               BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    ControlTotalId      INT NOT NULL REFERENCES cfg.ControlTotalDefinition(ControlTotalId),
    ValueA              BIGINT NOT NULL,
    ValueB              BIGINT NOT NULL,
    DifferenceMinor     AS (ValueA - ValueB) PERSISTED,
    IsBalanced          BIT NOT NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT UQ_ControlTotalResult UNIQUE (RunId, ControlTotalId)
);
GO
/* A run with fully matched rows but a non-zero net difference is a FAILED
   run (design §11). This index answers "which runs did not balance". */
CREATE INDEX IX_ControlTotalResult_Unbalanced
    ON ops.ControlTotalResult (ControlTotalId, RunId) WHERE IsBalanced = 0;
GO


/* =====================================================================
   12. RUN AGGREGATES — the durable totals you return to later.
   Operations must be able to go back to any past reconciliation, take a
   total and compare it to a summary. This makes "run 4471, matched
   inward total" a primary-key read instead of a rescan of 2M staged rows,
   and it survives staging archival — which is what makes a 3-month
   staging retention safe (E3).
   ===================================================================== */

CREATE TABLE ops.RunAggregate (
    RunAggregateId      BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId               BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    -- FIX (v1.0, finding 11): these two carried no FK in v0.2, alone in an
    -- otherwise consistently referenced schema.
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    BusinessDate        DATE NOT NULL,
    Side                VARCHAR(10) NOT NULL,               -- Left | Right
    GroupKey            NVARCHAR(300) NOT NULL DEFAULT '*',  -- '*' = whole dataset; else 'Direction=Inward'
    MatchStatus         VARCHAR(20) NOT NULL DEFAULT '*',    -- '*' = all
    -- FIX (v1.0, finding 11): was RowCount, which collides with the
    -- reserved ROWCOUNT keyword and needs bracketing forever.
    RowCnt              BIGINT NOT NULL,
    AmountMinorSum      BIGINT NOT NULL,
    CurrencyCode        CHAR(3) NOT NULL REFERENCES cfg.Currency(CurrencyCode),
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT UQ_RunAggregate UNIQUE (RunId, DatasetId, Side, GroupKey, MatchStatus),
    CONSTRAINT CK_RunAggregate_Side CHECK
(Side COLLATE Latin1_General_CS_AS IN
 ('Left','Right')),
    CONSTRAINT CK_RunAggregate_Status CHECK
(MatchStatus COLLATE Latin1_General_CS_AS IN
 ('*','Matched','Unmatched','Ambiguous','Excluded','Duplicate','AutoClosed'))
);
GO
/* Period-scoped control totals sum across a date range. They must use
   ONLY current runs: superseded reruns and sandbox runs are excluded by
   joining ops.ReconRun on IsCurrent = 1 AND RunType <> 'Sandbox'. */
CREATE INDEX IX_RunAggregate_Period
    ON ops.RunAggregate (DefinitionId, BusinessDate, DatasetId, GroupKey)
    INCLUDE (RowCnt, AmountMinorSum, MatchStatus);
GO


/* =====================================================================
   13. FEES CALCULATED
   Transaction-level by design: an aggregate-only figure is one you cannot
   defend. This is what answers "why is our netting 4.120 JOD below
   theirs" by pointing at rows. Rounding is PER TRANSACTION, then summed —
   summing first and rounding last yields a different netting figure than
   the counterparty's.
   ===================================================================== */

CREATE TABLE ops.TransactionFee (
    TransactionFeeId    BIGINT IDENTITY(1,1) NOT NULL,
    RunId               BIGINT NOT NULL,
    BusinessDate        DATE NOT NULL,
    StagingId           BIGINT NOT NULL,
    FeeScheduleId       INT NOT NULL,
    FeeTierId           INT NULL,
    BaseAmountMinor     BIGINT NOT NULL,
    FeeAmountMinor      BIGINT NOT NULL,            -- rounded PER TRANSACTION, then summed
    CurrencyCode        CHAR(3) NOT NULL,
    FeeParty            VARCHAR(20) NOT NULL,       -- Revenue | Cost
    Direction           VARCHAR(20) NOT NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT CK_TransactionFee_Party CHECK
(FeeParty COLLATE Latin1_General_CS_AS IN
 ('Revenue','Cost')),
    CONSTRAINT CK_TransactionFee_Direction CHECK
(Direction COLLATE Latin1_General_CS_AS IN
 ('Inward','Outward'))
    -- OPEN (design §15 Q8): sales tax on fees — applicable, and at which
    -- stage? If it applies, a tax column set lands here and a rate table
    -- in cfg. Do not model it until the answer is known.
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_TransactionFee ON ops.TransactionFee (RunId, BusinessDate, TransactionFeeId)
    ON ps_ByMonth(BusinessDate);
GO

CREATE TABLE ops.InterchangeSummary (
    InterchangeSummaryId BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId               BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    BusinessDate        DATE NOT NULL,
    CurrencyCode        CHAR(3) NOT NULL REFERENCES cfg.Currency(CurrencyCode),
    InwardCnt           BIGINT NOT NULL,
    OutwardCnt          BIGINT NOT NULL,
    InwardAmountMinor   BIGINT NOT NULL,
    OutwardAmountMinor  BIGINT NOT NULL,
    RevenueMinor        BIGINT NOT NULL,
    CostMinor           BIGINT NOT NULL,
    NettingMinor        AS (RevenueMinor - CostMinor) PERSISTED,
    ReportedNettingMinor BIGINT NULL,               -- from the counterparty's fee report
    DifferenceMinor     AS (RevenueMinor - CostMinor - ISNULL(ReportedNettingMinor, 0)) PERSISTED,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT UQ_InterchangeSummary UNIQUE (RunId, CurrencyCode)
);
GO


/* =====================================================================
   14. AUDIT — Phase 1, not deferred with Maker/Checker.
   Users edit rules that decide financial outcomes; "who changed this and
   when" must be answerable from day one. Retrofitting an audit trail
   always costs more than writing it.
   ===================================================================== */

CREATE TABLE aud.AuditLog (
    AuditId             BIGINT IDENTITY(1,1) NOT NULL,
    AuditDate           DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    EntityType          VARCHAR(300) NOT NULL,      -- Dataset|MatchRule|FeeSchedule|Exception ...
    EntityId            NVARCHAR(300) NOT NULL,
    Action              VARCHAR(30)  NOT NULL,
    OldValueJson        NVARCHAR(MAX) NULL,
    NewValueJson        NVARCHAR(MAX) NULL,
    PerformedBy         NVARCHAR(300) NOT NULL,
    PerformedAt         DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    IpAddress           VARCHAR(45)  NULL,          -- width is semantic: IPv6 max length
    Notes               NVARCHAR(1000) NULL,
    -- FIX (v1.0, D3): 'Export' was missing. The audit log captured config
    -- changes but not READS of sensitive exports — who downloaded which
    -- report, when. Regulators ask.
    CONSTRAINT CK_AuditLog_Action CHECK
(Action COLLATE Latin1_General_CS_AS IN
        ('Create','Update','Delete','Activate','Deactivate',
         'Execute','Resume','Close','Reopen','Export','Login'))
) ON ps_ByMonth(AuditDate);
GO
CREATE CLUSTERED INDEX CIX_AuditLog ON aud.AuditLog (AuditDate, EntityType, AuditId)
    ON ps_ByMonth(AuditDate);
GO
/* The portal's audit viewer filters by entity and by user. */
CREATE NONCLUSTERED INDEX IX_AuditLog_Entity
    ON aud.AuditLog (EntityType, EntityId, AuditDate)
    ON ps_ByMonth(AuditDate);
GO


/* =====================================================================
   OPEN ITEMS — carried forward, unchanged by this revision.
   These are business answers, not design work:
     - Retention period (drives cfg.PlatformSetting ResultsMonthsOnline
       and when partition switching to archive begins).
     - FeeSchedule.RoundingMode default is HalfUp; confirm against the
       counterparty's own calculation or netting will never tie out.
     - Sales tax on fees: if applicable, add a tax column set to
       ops.TransactionFee and a rate table to cfg.
     - Sessions per day and their timing (scheduler + "file not received"
       thresholds).
     - Is the primary reference globally unique forever, or reused across
       days? Decides whether pass 1 joins on reference alone or reference
       + date.
   ===================================================================== */
