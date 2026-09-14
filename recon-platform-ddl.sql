/* =====================================================================
   RECONCILIATION PLATFORM — SQL Server DDL
   v0.1  |  Storage model: SLOT-BASED (decision confirmed)
   =====================================================================
   Design notes:
     - Schemas: cfg (configuration), ops (operational), stg (staging),
                aud (audit). Separation allows per-schema permissions.
     - All monetary comparison happens on BIGINT fils. No FLOAT anywhere.
     - StagingTransaction is partitioned on TxDate (SQL Server supports a
       single partition column). Per-dataset isolation is achieved by
       DatasetId being the LEADING column of the clustered index, not by
       partitioning — this gives equivalent seek behaviour.
     - Retention is a parameter of the partition function only; it does
       not affect any table structure below.
   ===================================================================== */

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
   Monthly partitions. Extend the range list as time advances (a
   scheduled maintenance job should add future partitions ahead of need).
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
   2. CONFIGURATION — counterparties and datasets
   ===================================================================== */

CREATE TABLE cfg.Counterparty (
    CounterpartyId      INT IDENTITY(1,1) PRIMARY KEY,
    Code                VARCHAR(30)   NOT NULL UNIQUE,   -- 'JOPACC'
    Name                NVARCHAR(200) NOT NULL,
    Description         NVARCHAR(500) NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CreatedBy           NVARCHAR(100) NOT NULL,
    ModifiedAt          DATETIME2(3) NULL,
    ModifiedBy          NVARCHAR(100) NULL
);
GO

CREATE TABLE cfg.Dataset (
    DatasetId           INT IDENTITY(1,1) PRIMARY KEY,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    Code                VARCHAR(50)   NOT NULL UNIQUE,   -- 'CLIQ_SESSION', 'OM_TXN'
    Name                NVARCHAR(200) NOT NULL,
    ProviderType        VARCHAR(20)   NOT NULL,          -- File | Sql | Api
    TimeZone            VARCHAR(50)   NOT NULL DEFAULT 'Asia/Amman',
    IsActive            BIT NOT NULL DEFAULT 0,          -- activation gate
    ActivatedAt         DATETIME2(3) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CreatedBy           NVARCHAR(100) NOT NULL,
    CONSTRAINT CK_Dataset_Provider CHECK (ProviderType IN ('File','Sql','Api'))
);
GO


/* ---------------------------------------------------------------------
   2.1 FIELD REGISTRY — the heart of the dynamic design.
   StorageSlot is the physical column in stg.StagingTransaction.
   Nothing outside the provider layer ever references a slot directly.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.DatasetField (
    DatasetFieldId      INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    FieldCode           VARCHAR(60)   NOT NULL,   -- stable internal code
    DisplayLabel        NVARCHAR(150) NOT NULL,   -- what Operations sees
    DataType            VARCHAR(20)   NOT NULL,   -- String|Integer|Decimal|DateTime|Boolean
    FieldRole           VARCHAR(30)   NULL,       -- Reference|Amount|Currency|Date|Direction|Status|Party|Other
    StorageSlot         VARCHAR(20)   NOT NULL,   -- 'Text1' | 'Num3' | 'Date2' ...
    IsMatchable         BIT NOT NULL DEFAULT 1,   -- may appear in a rule  <-- security boundary
    IsIndexed           BIT NOT NULL DEFAULT 0,   -- maintain an index on it
    IsRequired          BIT NOT NULL DEFAULT 0,
    DisplayOrder        INT NOT NULL DEFAULT 0,
    CONSTRAINT UQ_DatasetField_Code UNIQUE (DatasetId, FieldCode),
    CONSTRAINT UQ_DatasetField_Slot UNIQUE (DatasetId, StorageSlot),   -- one field per slot
    CONSTRAINT CK_DatasetField_Type CHECK
        (DataType IN ('String','Integer','Decimal','DateTime','Boolean'))
);
GO

/* Slot catalogue — tells the UI which slots are free and of what type. */
CREATE TABLE cfg.StorageSlotCatalogue (
    SlotName            VARCHAR(20) PRIMARY KEY,   -- 'Text1'
    SlotType            VARCHAR(20) NOT NULL,      -- String|Integer|Decimal|DateTime|Boolean
    MaxLength           INT NULL
);
GO


/* ---------------------------------------------------------------------
   2.2 FILE SOURCES — versioned and effective-dated, so a layout change
   never breaks the ability to re-read historical files.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.FileFormatDefinition (
    FileFormatId        INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    FormatType          VARCHAR(20)  NOT NULL,     -- Csv|Xml|Json|FixedWidth
    Version             INT          NOT NULL DEFAULT 1,
    EffectiveFrom       DATE         NOT NULL,
    EffectiveTo         DATE         NULL,
    Delimiter           VARCHAR(5)   NULL,         -- Csv
    TextQualifier       VARCHAR(2)   NULL,
    Encoding            VARCHAR(30)  NOT NULL DEFAULT 'UTF-8',
    HasHeader           BIT          NOT NULL DEFAULT 1,
    SkipLeadingLines    INT          NOT NULL DEFAULT 0,
    SkipTrailingLines   INT          NOT NULL DEFAULT 0,
    RecordPath          NVARCHAR(400) NULL,        -- XPath / JsonPath record root
    FileNamePattern     NVARCHAR(200) NULL,        -- regex for validation
    CONSTRAINT CK_FileFormat_Type CHECK
        (FormatType IN ('Csv','Xml','Json','FixedWidth'))
);
GO

CREATE TABLE cfg.FieldMapping (
    FieldMappingId      INT IDENTITY(1,1) PRIMARY KEY,
    FileFormatId        INT NOT NULL REFERENCES cfg.FileFormatDefinition(FileFormatId),
    DatasetFieldId      INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    SourcePath          NVARCHAR(400) NOT NULL,    -- column name/ordinal | XPath | JsonPath | start:length
    ParseFormat         NVARCHAR(60)  NULL,        -- 'yyyyMMddHHmmss', '#,##0.000'
    TransformChain      NVARCHAR(500) NULL,        -- 'Trim|Upper|StripNonAlphanumeric'
    DefaultValue        NVARCHAR(200) NULL,
    IsRequired          BIT NOT NULL DEFAULT 0,
    CONSTRAINT UQ_FieldMapping UNIQUE (FileFormatId, DatasetFieldId)
);
GO


/* ---------------------------------------------------------------------
   2.3 SQL SOURCES — view or stored procedure, interchangeable.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.SqlSourceDefinition (
    SqlSourceId         INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    ObjectType          VARCHAR(20)   NOT NULL,    -- View | StoredProcedure
    ObjectName          NVARCHAR(200) NOT NULL,    -- 'dbo.vw_OM_Transactions'
    ConnectionName      VARCHAR(60)   NOT NULL,    -- named connection, not a raw string
    FilterExpression    NVARCHAR(1000) NULL,       -- optional WHERE for views
    CommandTimeoutSec   INT NOT NULL DEFAULT 600,
    CONSTRAINT CK_SqlSource_Type CHECK (ObjectType IN ('View','StoredProcedure'))
);
GO

CREATE TABLE cfg.SqlSourceParameter (
    SqlSourceParameterId INT IDENTITY(1,1) PRIMARY KEY,
    SqlSourceId         INT NOT NULL REFERENCES cfg.SqlSourceDefinition(SqlSourceId),
    ParameterName       VARCHAR(60)  NOT NULL,     -- '@FromDate'
    ValueSource         VARCHAR(40)  NOT NULL,     -- RunDate|RunDateFrom|RunDateTo|SessionId|Constant
    ConstantValue       NVARCHAR(200) NULL,
    DataType            VARCHAR(20)  NOT NULL
);
GO

/* Column name -> registry field, for SQL sources (mirrors FieldMapping). */
CREATE TABLE cfg.SqlFieldMapping (
    SqlFieldMappingId   INT IDENTITY(1,1) PRIMARY KEY,
    SqlSourceId         INT NOT NULL REFERENCES cfg.SqlSourceDefinition(SqlSourceId),
    DatasetFieldId      INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    SourceColumn        NVARCHAR(128) NOT NULL,
    TransformChain      NVARCHAR(500) NULL,
    CONSTRAINT UQ_SqlFieldMapping UNIQUE (SqlSourceId, DatasetFieldId)
);
GO


/* ---------------------------------------------------------------------
   2.4 ACQUISITION — how a file arrives.
   --------------------------------------------------------------------- */

CREATE TABLE cfg.AcquisitionDefinition (
    AcquisitionId       INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    Method              VARCHAR(20)   NOT NULL,    -- Api | Sftp | Folder | Manual
    Endpoint            NVARCHAR(500) NULL,
    HttpMethod          VARCHAR(10)   NULL,
    CredentialRef       VARCHAR(100)  NULL,        -- key into the secret store, NEVER a secret
    RequestTemplate     NVARCHAR(MAX) NULL,
    StorageRootPath     NVARCHAR(400) NOT NULL,    -- original files live on disk, not in the DB
    RetryCount          INT NOT NULL DEFAULT 3,
    RetryDelaySeconds   INT NOT NULL DEFAULT 60,
    CONSTRAINT CK_Acquisition_Method CHECK (Method IN ('Api','Sftp','Folder','Manual'))
);
GO


/* =====================================================================
   3. RECONCILIATION DEFINITIONS AND RULES
   ===================================================================== */

CREATE TABLE cfg.ReconciliationDefinition (
    DefinitionId        INT IDENTITY(1,1) PRIMARY KEY,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    Code                VARCHAR(60)   NOT NULL UNIQUE,
    Name                NVARCHAR(200) NOT NULL,
    LeftDatasetId       INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    RightDatasetId      INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    Version             INT NOT NULL DEFAULT 1,     -- runs record which version executed
    IsActive            BIT NOT NULL DEFAULT 0,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CreatedBy           NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE cfg.MatchRule (
    MatchRuleId         INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    RuleCode            VARCHAR(40)   NOT NULL,
    Name                NVARCHAR(200) NOT NULL,
    Sequence            INT NOT NULL,               -- pass order
    LeftFilter          NVARCHAR(1000) NULL,        -- structured filter (JSON), not free SQL
    RightFilter         NVARCHAR(1000) NULL,
    Cardinality         VARCHAR(20) NOT NULL DEFAULT 'OneToOne',
    OnMultipleMatch     VARCHAR(20) NOT NULL DEFAULT 'MarkAmbiguous',
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_MatchRule UNIQUE (DefinitionId, Sequence),
    CONSTRAINT CK_MatchRule_Card CHECK (Cardinality IN ('OneToOne','OneToMany')),
    CONSTRAINT CK_MatchRule_Multi CHECK
        (OnMultipleMatch IN ('MarkAmbiguous','TakeEarliest','Fail'))
);
GO

/* The field pairs the user picks in the UI. Both sides resolve to the
   field registry — never to free text. This is what makes a user-built
   rule builder safe against SQL injection. */
CREATE TABLE cfg.MatchCondition (
    MatchConditionId    INT IDENTITY(1,1) PRIMARY KEY,
    MatchRuleId         INT NOT NULL REFERENCES cfg.MatchRule(MatchRuleId),
    LeftFieldId         INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    RightFieldId        INT NOT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    ComparisonType      VARCHAR(30) NOT NULL,
    ToleranceValue      BIGINT NULL,                -- fils, or numeric span
    ToleranceUnit       VARCHAR(20) NULL,           -- Fils|Minute|Hour|Day
    Sequence            INT NOT NULL DEFAULT 1,
    CONSTRAINT CK_MatchCondition_Cmp CHECK (ComparisonType IN
        ('Exact','Normalized','NumericExact','NumericTolerance',
         'DateExact','DateWithin','StartsWith','EndsWith','Contains'))
);
GO

CREATE TABLE cfg.ClassificationRule (
    ClassificationRuleId INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    ExceptionCode       VARCHAR(40)   NOT NULL,     -- FAILED_INWARD, MISSING_IN_CLIQ ...
    DisplayName         NVARCHAR(200) NOT NULL,
    AppliesToSide       VARCHAR(10)   NOT NULL,     -- Left | Right | Both
    ConditionJson       NVARCHAR(MAX) NOT NULL,     -- structured condition tree
    ActionType          VARCHAR(30)   NOT NULL DEFAULT 'ReportOnly',
    Severity            VARCHAR(20)   NOT NULL DEFAULT 'Normal',
    Sequence            INT NOT NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    -- ActionType is ReportOnly in this phase. AutoPost exists as a seam
    -- for future Failed Inward posting; it is not implemented.
    CONSTRAINT CK_Classification_Action CHECK
        (ActionType IN ('ReportOnly','AutoClose','AutoPost')),
    CONSTRAINT CK_Classification_Side CHECK (AppliesToSide IN ('Left','Right','Both'))
);
GO

CREATE TABLE cfg.ControlTotalDefinition (
    ControlTotalId      INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    CheckCode           VARCHAR(40)   NOT NULL,     -- DEBIT_TOTAL, CREDIT_COUNT ...
    DisplayName         NVARCHAR(200) NOT NULL,
    SourceAExpression   NVARCHAR(1000) NOT NULL,    -- structured aggregate spec
    SourceBExpression   NVARCHAR(1000) NOT NULL,
    ToleranceFils       BIGINT NOT NULL DEFAULT 0,  -- default: exact
    FailRunOnMismatch   BIT NOT NULL DEFAULT 1      -- a net difference fails the run
);
GO


/* =====================================================================
   4. SCHEDULING AND ALERTING
   ===================================================================== */

CREATE TABLE cfg.ScheduleDefinition (
    ScheduleId          INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    CronExpression      VARCHAR(100)  NOT NULL,
    TimeZone            VARCHAR(50)   NOT NULL DEFAULT 'Asia/Amman',
    ExpectedFileByTime  TIME NULL,                  -- drives "file not received" alerts
    IsEnabled           BIT NOT NULL DEFAULT 1
);
GO

CREATE TABLE cfg.AlertPolicy (
    AlertPolicyId       INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    EventType           VARCHAR(40)  NOT NULL,      -- FileNotReceived|RunFailed|ControlTotalMismatch|ThresholdBreach
    ThresholdValue      DECIMAL(18,3) NULL,
    Channel             VARCHAR(20)  NOT NULL,      -- Email|Sms|Dashboard
    Recipients          NVARCHAR(1000) NULL,
    IsEnabled           BIT NOT NULL DEFAULT 1
);
GO


/* =====================================================================
   5. FEES AND INTERCHANGE
   ===================================================================== */

CREATE TABLE cfg.FeeSchedule (
    FeeScheduleId       INT IDENTITY(1,1) PRIMARY KEY,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    Code                VARCHAR(60)   NOT NULL,
    Name                NVARCHAR(200) NOT NULL,
    Direction           VARCHAR(20)   NOT NULL,     -- Inward | Outward
    TransactionType     VARCHAR(40)   NULL,
    FeeParty            VARCHAR(20)   NOT NULL,     -- Revenue (to OM) | Cost (by OM)
    Currency            CHAR(3)       NOT NULL DEFAULT 'JOD',
    RoundingMode        VARCHAR(20)   NOT NULL DEFAULT 'HalfUp',  -- confirm with counterparty
    EffectiveFrom       DATE          NOT NULL,
    EffectiveTo         DATE          NULL,
    IsActive            BIT NOT NULL DEFAULT 1,
    CONSTRAINT CK_FeeSchedule_Party CHECK (FeeParty IN ('Revenue','Cost')),
    CONSTRAINT CK_FeeSchedule_Round CHECK (RoundingMode IN ('HalfUp','HalfEven','Truncate'))
);
GO

CREATE TABLE cfg.FeeTier (
    FeeTierId           INT IDENTITY(1,1) PRIMARY KEY,
    FeeScheduleId       INT NOT NULL REFERENCES cfg.FeeSchedule(FeeScheduleId),
    AmountFromFils      BIGINT NOT NULL,
    AmountToFils        BIGINT NULL,                -- NULL = open-ended top tier
    CalculationType     VARCHAR(30) NOT NULL,       -- Fixed|Percentage|FixedPlusPercentage
    FixedAmountFils     BIGINT NOT NULL DEFAULT 0,
    Percentage          DECIMAL(9,6) NOT NULL DEFAULT 0,
    MinFeeFils          BIGINT NULL,
    MaxFeeFils          BIGINT NULL,
    CONSTRAINT CK_FeeTier_Calc CHECK
        (CalculationType IN ('Fixed','Percentage','FixedPlusPercentage'))
);
GO

/* Whether fees apply at all for a given dataset/transaction type —
   confirmed that some reconciliation reports carry fees and some do not. */
CREATE TABLE cfg.FeeApplicability (
    FeeApplicabilityId  INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    TransactionType     VARCHAR(40) NULL,           -- NULL = all types
    IsFeeApplicable     BIT NOT NULL,
    FeeScheduleId       INT NULL REFERENCES cfg.FeeSchedule(FeeScheduleId)
);
GO


/* =====================================================================
   6. REPORT DEFINITIONS
   ===================================================================== */

CREATE TABLE cfg.ReportDefinition (
    ReportDefinitionId  INT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    Code                VARCHAR(60)   NOT NULL,
    Name                NVARCHAR(200) NOT NULL,
    SheetName           NVARCHAR(100) NOT NULL,
    DataScope           VARCHAR(40)   NOT NULL,     -- Matched|Unmatched|Exceptions|ControlTotals|All
    FilterJson          NVARCHAR(MAX) NULL,
    SortJson            NVARCHAR(500) NULL,
    IncludeSubtotals    BIT NOT NULL DEFAULT 0,
    IsActive            BIT NOT NULL DEFAULT 1
);
GO

CREATE TABLE cfg.ReportColumn (
    ReportColumnId      INT IDENTITY(1,1) PRIMARY KEY,
    ReportDefinitionId  INT NOT NULL REFERENCES cfg.ReportDefinition(ReportDefinitionId),
    DatasetFieldId      INT NULL REFERENCES cfg.DatasetField(DatasetFieldId),
    ComputedField       VARCHAR(60)   NULL,         -- ExceptionCode, MatchedByRuleCode ...
    Header              NVARCHAR(150) NOT NULL,
    DisplayFormat       NVARCHAR(60)  NULL,
    ColumnWidth         INT NULL,
    Sequence            INT NOT NULL,
    CONSTRAINT CK_ReportColumn_Source CHECK
        (DatasetFieldId IS NOT NULL OR ComputedField IS NOT NULL)
);
GO


/* =====================================================================
   7. OPERATIONAL — runs and files
   ===================================================================== */

CREATE TABLE ops.ReconRun (
    RunId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    DefinitionId        INT NOT NULL REFERENCES cfg.ReconciliationDefinition(DefinitionId),
    DefinitionVersion   INT NOT NULL,               -- reproducibility
    BusinessDate        DATE NOT NULL,
    SessionRef          NVARCHAR(100) NULL,
    RunType             VARCHAR(20) NOT NULL DEFAULT 'Scheduled',  -- Scheduled|Manual|Rerun|Sandbox
    SupersedesRunId     BIGINT NULL REFERENCES ops.ReconRun(RunId), -- reruns never overwrite
    Status              VARCHAR(20) NOT NULL DEFAULT 'Pending',
    StartedAt           DATETIME2(3) NULL,
    CompletedAt         DATETIME2(3) NULL,
    LeftRowCount        BIGINT NULL,
    RightRowCount       BIGINT NULL,
    MatchedCount        BIGINT NULL,
    UnmatchedCount      BIGINT NULL,
    AmbiguousCount      BIGINT NULL,
    ErrorMessage        NVARCHAR(MAX) NULL,
    TriggeredBy         NVARCHAR(100) NOT NULL,
    CONSTRAINT CK_ReconRun_Status CHECK
        (Status IN ('Pending','Running','Completed','Failed','Cancelled'))
);
GO
CREATE INDEX IX_ReconRun_Def_Date ON ops.ReconRun (DefinitionId, BusinessDate DESC);
GO

CREATE TABLE ops.ReconRunStep (
    RunStepId           BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId               BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    StepName            VARCHAR(40) NOT NULL,       -- Acquire|Parse|Stage|Match|Classify|ControlTotals|Report
    MatchRuleId         INT NULL REFERENCES cfg.MatchRule(MatchRuleId),
    Status              VARCHAR(20) NOT NULL,
    StartedAt           DATETIME2(3) NULL,
    CompletedAt         DATETIME2(3) NULL,
    RowsProcessed       BIGINT NULL,
    RowsMatched         BIGINT NULL,                -- per-pass distribution = data quality signal
    GeneratedSql        NVARCHAR(MAX) NULL,         -- persisted for audit
    ErrorMessage        NVARCHAR(MAX) NULL
);
GO
CREATE INDEX IX_ReconRunStep_Run ON ops.ReconRunStep (RunId, StepName);
GO

CREATE TABLE ops.SourceFile (
    SourceFileId        BIGINT IDENTITY(1,1) NOT NULL,
    DatasetId           INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    FileName            NVARCHAR(400) NOT NULL,
    StoragePath         NVARCHAR(1000) NOT NULL,    -- filesystem/object store, NOT a blob column
    FileHash            CHAR(64) NOT NULL,          -- SHA-256: idempotency + integrity
    FileSizeBytes       BIGINT NOT NULL,
    BusinessDate        DATE NOT NULL,
    SessionRef          NVARCHAR(100) NULL,
    ReceivedAt          DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    ProcessedAt         DATETIME2(3) NULL,
    Status              VARCHAR(20) NOT NULL DEFAULT 'Received',
    RowCount            BIGINT NULL,
    ErrorCount          BIGINT NULL,
    CONSTRAINT PK_SourceFile PRIMARY KEY (SourceFileId, BusinessDate),
    CONSTRAINT CK_SourceFile_Status CHECK
        (Status IN ('Received','Parsing','Parsed','Failed','Duplicate'))
) ON ps_ByMonth(BusinessDate);
GO
-- Same content received twice is a duplicate, not a second run.
CREATE UNIQUE INDEX UX_SourceFile_Hash
    ON ops.SourceFile (DatasetId, FileHash, BusinessDate) ON ps_ByMonth(BusinessDate);
GO


/* =====================================================================
   8. STAGING — the high-volume table (~2M rows/day)
   SLOT-BASED storage. Field names are resolved through cfg.DatasetField.
   Per-dataset views (section 8.1) restore readability.
   ===================================================================== */

CREATE TABLE stg.StagingTransaction (
    StagingId           BIGINT IDENTITY(1,1) NOT NULL,
    DatasetId           INT    NOT NULL,
    RunId               BIGINT NOT NULL,
    SourceFileId        BIGINT NULL,
    RawRowNumber        INT    NULL,
    TxDate              DATE   NOT NULL,            -- partition column

    /* ---- text slots ---- */
    Text1  NVARCHAR(200) NULL, Text2  NVARCHAR(200) NULL, Text3  NVARCHAR(200) NULL,
    Text4  NVARCHAR(200) NULL, Text5  NVARCHAR(200) NULL, Text6  NVARCHAR(200) NULL,
    Text7  NVARCHAR(200) NULL, Text8  NVARCHAR(200) NULL, Text9  NVARCHAR(200) NULL,
    Text10 NVARCHAR(200) NULL, Text11 NVARCHAR(200) NULL, Text12 NVARCHAR(200) NULL,
    Text13 NVARCHAR(200) NULL, Text14 NVARCHAR(200) NULL, Text15 NVARCHAR(200) NULL,
    Text16 NVARCHAR(200) NULL, Text17 NVARCHAR(200) NULL, Text18 NVARCHAR(200) NULL,
    Text19 NVARCHAR(200) NULL, Text20 NVARCHAR(200) NULL, Text21 NVARCHAR(200) NULL,
    Text22 NVARCHAR(200) NULL, Text23 NVARCHAR(200) NULL, Text24 NVARCHAR(200) NULL,
    Text25 NVARCHAR(200) NULL, Text26 NVARCHAR(200) NULL, Text27 NVARCHAR(200) NULL,
    Text28 NVARCHAR(200) NULL, Text29 NVARCHAR(200) NULL, Text30 NVARCHAR(200) NULL,

    /* ---- integer slots: amounts in FILS, counts, codes ---- */
    Num1  BIGINT NULL, Num2  BIGINT NULL, Num3  BIGINT NULL, Num4  BIGINT NULL,
    Num5  BIGINT NULL, Num6  BIGINT NULL, Num7  BIGINT NULL, Num8  BIGINT NULL,
    Num9  BIGINT NULL, Num10 BIGINT NULL, Num11 BIGINT NULL, Num12 BIGINT NULL,
    Num13 BIGINT NULL, Num14 BIGINT NULL, Num15 BIGINT NULL,

    /* ---- decimal slots: display/source amounts. NEVER used for matching ---- */
    Dec1 DECIMAL(18,3) NULL, Dec2 DECIMAL(18,3) NULL, Dec3 DECIMAL(18,3) NULL,
    Dec4 DECIMAL(18,3) NULL, Dec5 DECIMAL(18,3) NULL,

    /* ---- date slots ---- */
    Date1 DATETIME2(3) NULL, Date2 DATETIME2(3) NULL, Date3 DATETIME2(3) NULL,
    Date4 DATETIME2(3) NULL, Date5 DATETIME2(3) NULL, Date6 DATETIME2(3) NULL,
    Date7 DATETIME2(3) NULL, Date8 DATETIME2(3) NULL,

    /* ---- boolean slots ---- */
    Flag1 BIT NULL, Flag2 BIT NULL, Flag3 BIT NULL, Flag4 BIT NULL, Flag5 BIT NULL,

    /* ---- result of matching ---- */
    MatchStatus         VARCHAR(20) NOT NULL DEFAULT 'Unmatched',
    MatchedWithId       BIGINT NULL,
    MatchedByRuleId     INT NULL,
    ExceptionCode       VARCHAR(40) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),

    CONSTRAINT CK_Staging_MatchStatus CHECK
        (MatchStatus IN ('Unmatched','Matched','Ambiguous','AutoClosed','Excluded'))
) ON ps_ByMonth(TxDate);
GO

/* DatasetId leads the clustered key: every query targets one dataset, so
   SQL Server seeks its range and ignores the rest. This gives the same
   isolation as separate per-dataset tables. */
CREATE CLUSTERED INDEX CIX_Staging
    ON stg.StagingTransaction (DatasetId, TxDate, StagingId)
    ON ps_ByMonth(TxDate);
GO

CREATE UNIQUE NONCLUSTERED INDEX UX_Staging_Id
    ON stg.StagingTransaction (StagingId, TxDate)
    ON ps_ByMonth(TxDate);
GO

/* Unmatched-row scan for each successive pass. Filtered to keep it small. */
CREATE NONCLUSTERED INDEX IX_Staging_Unmatched
    ON stg.StagingTransaction (DatasetId, RunId, TxDate)
    WHERE MatchStatus = 'Unmatched'
    ON ps_ByMonth(TxDate);
GO

/* ---------------------------------------------------------------------
   Matching indexes are created from the ACTIVE RULE SET, not up front.
   The platform generates one per matchable/indexed field. Example for a
   dataset whose primary reference sits in Text1:

   CREATE NONCLUSTERED INDEX IX_Staging_DS1_Text1
       ON stg.StagingTransaction (DatasetId, Text1, TxDate)
       INCLUDE (StagingId, MatchStatus, Num1)
       ON ps_ByMonth(TxDate);

   Build order matters at this volume: bulk load into the heap-like
   structure first, then build indexes — not the reverse.
   --------------------------------------------------------------------- */


/* ---------------------------------------------------------------------
   8.1 PER-DATASET READABLE VIEWS — generated from cfg.DatasetField.
   These remove the only real drawback of slot storage: raw tables that
   nobody can read. Creating a view is not a data-structure change, so it
   does not require the DDL permissions that generated tables would.
   Example (illustrative — the platform generates these):

   CREATE VIEW stg.v_CliQ_Session AS
   SELECT StagingId, RunId, SourceFileId, TxDate,
          Text1 AS EndToEndId,
          Text2 AS TransactionId,
          Text3 AS CreditorAccount,
          Num1  AS AmountFils,
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
    ErrorType           VARCHAR(40) NOT NULL,       -- MissingRequired|TypeConversion|FormatInvalid
    FieldCode           VARCHAR(60) NULL,
    ErrorMessage        NVARCHAR(1000) NOT NULL,
    RawLine             NVARCHAR(MAX) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME()
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_ParseError ON stg.ParseError (RunId, BusinessDate, ParseErrorId)
    ON ps_ByMonth(BusinessDate);
GO


/* =====================================================================
   9. RESULTS
   ===================================================================== */

CREATE TABLE ops.MatchResult (
    MatchResultId       BIGINT IDENTITY(1,1) NOT NULL,
    RunId               BIGINT NOT NULL,
    BusinessDate        DATE   NOT NULL,
    LeftStagingId       BIGINT NULL,                -- NULL = exists only on the right
    RightStagingId      BIGINT NULL,                -- NULL = exists only on the left
    MatchRuleId         INT    NULL,                -- which pass matched it
    MatchStatus         VARCHAR(20) NOT NULL,
    AmountDiffFils      BIGINT NULL,
    CandidateCount      INT NULL,                   -- >1 drives Ambiguous
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME()
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_MatchResult ON ops.MatchResult (RunId, BusinessDate, MatchResultId)
    ON ps_ByMonth(BusinessDate);
GO
CREATE NONCLUSTERED INDEX IX_MatchResult_Left ON ops.MatchResult (LeftStagingId, BusinessDate)
    ON ps_ByMonth(BusinessDate);
GO

CREATE TABLE ops.ReconException (
    ExceptionId         BIGINT IDENTITY(1,1) NOT NULL,
    RunId               BIGINT NOT NULL,
    DefinitionId        INT NOT NULL,
    BusinessDate        DATE NOT NULL,
    StagingId           BIGINT NULL,
    Side                VARCHAR(10) NOT NULL,       -- Left | Right
    ExceptionCode       VARCHAR(40) NOT NULL,
    AmountFils          BIGINT NULL,
    Status              VARCHAR(20) NOT NULL DEFAULT 'Open',
    AgeDays             AS DATEDIFF(DAY, BusinessDate, CAST(SYSDATETIME() AS DATE)),
    AssignedTo          NVARCHAR(100) NULL,
    ResolutionCode      VARCHAR(40) NULL,
    ResolutionNote      NVARCHAR(1000) NULL,
    ClosedByRunId       BIGINT NULL,                -- set when a later run auto-closes it
    ClosedAt            DATETIME2(3) NULL,
    ClosedBy            NVARCHAR(100) NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT CK_Exception_Status CHECK
        (Status IN ('Open','InProgress','Resolved','AutoClosed','WrittenOff'))
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_Exception ON ops.ReconException (DefinitionId, BusinessDate, ExceptionId)
    ON ps_ByMonth(BusinessDate);
GO
CREATE NONCLUSTERED INDEX IX_Exception_Open
    ON ops.ReconException (DefinitionId, ExceptionCode, BusinessDate)
    WHERE Status IN ('Open','InProgress')
    ON ps_ByMonth(BusinessDate);
GO

CREATE TABLE ops.ControlTotalResult (
    ControlTotalResultId BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId               BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    ControlTotalId      INT NOT NULL REFERENCES cfg.ControlTotalDefinition(ControlTotalId),
    ValueA              BIGINT NOT NULL,
    ValueB              BIGINT NOT NULL,
    DifferenceFils      AS (ValueA - ValueB) PERSISTED,
    IsBalanced          BIT NOT NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME()
);
GO


/* =====================================================================
   10. FEES CALCULATED
   ===================================================================== */

CREATE TABLE ops.TransactionFee (
    TransactionFeeId    BIGINT IDENTITY(1,1) NOT NULL,
    RunId               BIGINT NOT NULL,
    BusinessDate        DATE NOT NULL,
    StagingId           BIGINT NOT NULL,
    FeeScheduleId       INT NOT NULL,
    FeeTierId           INT NULL,
    BaseAmountFils      BIGINT NOT NULL,
    FeeAmountFils       BIGINT NOT NULL,            -- rounded PER TRANSACTION, then summed
    FeeParty            VARCHAR(20) NOT NULL,       -- Revenue | Cost
    Direction           VARCHAR(20) NOT NULL,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME()
) ON ps_ByMonth(BusinessDate);
GO
CREATE CLUSTERED INDEX CIX_TransactionFee ON ops.TransactionFee (RunId, BusinessDate, TransactionFeeId)
    ON ps_ByMonth(BusinessDate);
GO

CREATE TABLE ops.InterchangeSummary (
    InterchangeSummaryId BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId               BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    BusinessDate        DATE NOT NULL,
    InwardCount         BIGINT NOT NULL,
    OutwardCount        BIGINT NOT NULL,
    InwardAmountFils    BIGINT NOT NULL,
    OutwardAmountFils   BIGINT NOT NULL,
    RevenueFils         BIGINT NOT NULL,
    CostFils            BIGINT NOT NULL,
    NettingFils         AS (RevenueFils - CostFils) PERSISTED,
    ReportedNettingFils BIGINT NULL,                -- from the counterparty's fee report
    DifferenceFils      AS (RevenueFils - CostFils - ISNULL(ReportedNettingFils,0)) PERSISTED,
    CreatedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME()
);
GO


/* =====================================================================
   11. AUDIT — Phase 1, not deferred with Maker/Checker.
   Users edit rules that decide financial outcomes; "who changed this and
   when" must be answerable from day one.
   ===================================================================== */

CREATE TABLE aud.AuditLog (
    AuditId             BIGINT IDENTITY(1,1) NOT NULL,
    AuditDate           DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    EntityType          VARCHAR(60)  NOT NULL,      -- Dataset|MatchRule|FeeSchedule|Exception ...
    EntityId            NVARCHAR(60) NOT NULL,
    Action              VARCHAR(30)  NOT NULL,      -- Create|Update|Delete|Activate|Execute|Close
    OldValueJson        NVARCHAR(MAX) NULL,
    NewValueJson        NVARCHAR(MAX) NULL,
    PerformedBy         NVARCHAR(100) NOT NULL,
    PerformedAt         DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    IpAddress           VARCHAR(45)  NULL,
    Notes               NVARCHAR(1000) NULL
) ON ps_ByMonth(AuditDate);
GO
CREATE CLUSTERED INDEX CIX_AuditLog ON aud.AuditLog (AuditDate, EntityType, AuditId)
    ON ps_ByMonth(AuditDate);
GO


/* =====================================================================
   12. ACCESS CONTROL — scoped per counterparty
   ===================================================================== */

CREATE TABLE cfg.UserCounterpartyAccess (
    AccessId            INT IDENTITY(1,1) PRIMARY KEY,
    UserName            NVARCHAR(100) NOT NULL,
    CounterpartyId      INT NOT NULL REFERENCES cfg.Counterparty(CounterpartyId),
    AccessLevel         VARCHAR(20) NOT NULL,       -- Read|Operate|Configure
    GrantedAt           DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    GrantedBy           NVARCHAR(100) NOT NULL,
    CONSTRAINT UQ_UserCounterparty UNIQUE (UserName, CounterpartyId),
    CONSTRAINT CK_AccessLevel CHECK (AccessLevel IN ('Read','Operate','Configure'))
);
GO


/* =====================================================================
   13. SLOT CATALOGUE SEED
   ===================================================================== */

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength)
SELECT 'Text' + CAST(n AS VARCHAR(2)), 'String', 200
FROM (SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength)
SELECT 'Num' + CAST(n AS VARCHAR(2)), 'Integer', NULL
FROM (SELECT TOP (15) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength)
SELECT 'Dec' + CAST(n AS VARCHAR(2)), 'Decimal', NULL
FROM (SELECT TOP (5) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength)
SELECT 'Date' + CAST(n AS VARCHAR(2)), 'DateTime', NULL
FROM (SELECT TOP (8) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength)
SELECT 'Flag' + CAST(n AS VARCHAR(2)), 'Boolean', NULL
FROM (SELECT TOP (5) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;
GO


/* =====================================================================
   OPEN ITEMS
     - Retention period: sets how many partitions stay online and when
       partition switching to archive begins. Structure is unaffected.
     - FeeSchedule.RoundingMode default is HalfUp; confirm against the
       counterparty's own calculation or netting will never tie out.
     - Sales tax on fees: if applicable, add a tax column set to
       ops.TransactionFee and a rate table to cfg.
   ===================================================================== */
