/* =====================================================================
   RECONCILIATION PLATFORM — DDL DELTA v0.2
   Applies the v0.3 design-review fixes over recon-platform-ddl.sql v0.1.
   Run AFTER the v0.1 script. Nothing is deployed yet, so you may instead
   merge these into the base script; the delta form is kept so every
   change is visible for review.
   ===================================================================== */

/* ---------------------------------------------------------------------
   A5 · Read consistency — portal must not block behind the loader
   --------------------------------------------------------------------- */
-- ALTER DATABASE [ReconPlatform] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;


/* ---------------------------------------------------------------------
   C4 · Currency minor units (fils = 3, cents = 2)
   --------------------------------------------------------------------- */
CREATE TABLE cfg.Currency (
    CurrencyCode    CHAR(3) PRIMARY KEY,
    Name            NVARCHAR(60) NOT NULL,
    MinorUnits      TINYINT NOT NULL            -- JOD=3, USD=2, EUR=2
);
INSERT INTO cfg.Currency VALUES ('JOD','Jordanian Dinar',3),('USD','US Dollar',2),('EUR','Euro',2);
GO

ALTER TABLE cfg.Dataset
    ADD DefaultCurrency     CHAR(3) NULL REFERENCES cfg.Currency(CurrencyCode),
        DuplicateKeyFields  NVARCHAR(400) NULL;  -- C3: comma-separated FieldCodes; duplicates flagged pre-match
GO


/* ---------------------------------------------------------------------
   A2 · Normalization moves to parse time
   --------------------------------------------------------------------- */
ALTER TABLE cfg.DatasetField
    ADD NormalizeForMatch   BIT NOT NULL DEFAULT 0,   -- populates a companion slot at load
        NormalizedSlot      VARCHAR(20) NULL;         -- e.g. Text21 holding UPPER/TRIM/stripped Text1
GO

-- Parse-error flood cap (E4)
ALTER TABLE cfg.FileFormatDefinition
    ADD MaxParseErrors      INT NOT NULL DEFAULT 1000;
GO


/* ---------------------------------------------------------------------
   D1 · Structured filters replace free SQL text
   --------------------------------------------------------------------- */
ALTER TABLE cfg.SqlSourceDefinition DROP COLUMN FilterExpression;
ALTER TABLE cfg.SqlSourceDefinition
    ADD FilterJson          NVARCHAR(MAX) NULL;       -- condition-tree schema (B5), same compiler as rules
GO


/* ---------------------------------------------------------------------
   C5 · Matching window
   --------------------------------------------------------------------- */
ALTER TABLE cfg.ReconciliationDefinition
    ADD MatchingWindowDaysBefore  INT NOT NULL DEFAULT 1,
        MatchingWindowDaysAfter   INT NOT NULL DEFAULT 1;
GO


/* ---------------------------------------------------------------------
   C1 · Aggregate matching (one summary line ↔ many rows)
   --------------------------------------------------------------------- */
ALTER TABLE cfg.MatchRule
    ADD MatchMode           VARCHAR(20) NOT NULL DEFAULT 'Row',   -- Row | Aggregate
        LeftGroupByFields   NVARCHAR(400) NULL,   -- FieldCodes; Aggregate mode only
        RightGroupByFields  NVARCHAR(400) NULL,
        AggregateFunction   VARCHAR(20) NULL,     -- Sum | Count | SumAndCount
        CONSTRAINT CK_MatchRule_Mode CHECK (MatchMode IN ('Row','Aggregate'));
GO

-- 'Normalized' is no longer a runtime comparison (A2)
ALTER TABLE cfg.MatchCondition DROP CONSTRAINT CK_MatchCondition_Cmp;
ALTER TABLE cfg.MatchCondition ADD CONSTRAINT CK_MatchCondition_Cmp CHECK (ComparisonType IN
    ('Exact','NumericExact','NumericTolerance','DateExact','DateWithin',
     'StartsWith','EndsWith','Contains'));
GO


/* ---------------------------------------------------------------------
   C7 · Reversal chain support — registry role list is documentation
   only; the compiler supports OriginalReference ↔ Reference conditions.
   C6 · Exclusion rules — rows removed from the working set before pass 1
   --------------------------------------------------------------------- */
CREATE TABLE cfg.ExclusionRule (
    ExclusionRuleId INT IDENTITY(1,1) PRIMARY KEY,
    DatasetId       INT NOT NULL REFERENCES cfg.Dataset(DatasetId),
    Name            NVARCHAR(200) NOT NULL,
    ConditionJson   NVARCHAR(MAX) NOT NULL,       -- condition-tree schema
    ReasonCode      VARCHAR(40) NOT NULL,         -- reported, never an exception
    IsActive        BIT NOT NULL DEFAULT 1
);
GO


/* ---------------------------------------------------------------------
   C1 · Control totals with scope and source
   --------------------------------------------------------------------- */
ALTER TABLE cfg.ControlTotalDefinition
    ADD Scope           VARCHAR(20) NOT NULL DEFAULT 'Run',        -- Run | BusinessDate | Period
        SourceTypeA     VARCHAR(20) NOT NULL DEFAULT 'Staging',    -- Staging | MatchResult | RunAggregate | Dataset
        SourceTypeB     VARCHAR(20) NOT NULL DEFAULT 'Dataset',
        PeriodDays      INT NULL,                                  -- Period scope: window length
        CONSTRAINT CK_ControlTotal_Scope CHECK (Scope IN ('Run','BusinessDate','Period')),
        CONSTRAINT CK_ControlTotal_SrcA CHECK (SourceTypeA IN ('Staging','MatchResult','RunAggregate','Dataset')),
        CONSTRAINT CK_ControlTotal_SrcB CHECK (SourceTypeB IN ('Staging','MatchResult','RunAggregate','Dataset'));
GO


/* ---------------------------------------------------------------------
   B1 · Reproducibility  |  B4 · Rerun types  |  C1 · IsCurrent
   --------------------------------------------------------------------- */
ALTER TABLE ops.ReconRun
    ADD DefinitionSnapshotJson  NVARCHAR(MAX) NULL,   -- full effective definition at run start
        IsCurrent               BIT NOT NULL DEFAULT 1, -- superseded runs → 0; period aggregates use only 1
        SourceRunId             BIGINT NULL REFERENCES ops.ReconRun(RunId); -- Rematch reuses staged rows
GO
ALTER TABLE ops.ReconRun DROP CONSTRAINT CK_ReconRun_Status;
ALTER TABLE ops.ReconRun ADD CONSTRAINT CK_ReconRun_Status CHECK
    (Status IN ('Pending','Running','Completed','Failed','Cancelled','Rejected','Resuming'));
GO
-- RunType now: Scheduled | Manual | Rerun | Rematch | Sandbox
CREATE UNIQUE INDEX UX_ReconRun_Current
    ON ops.ReconRun (DefinitionId, BusinessDate, SessionRef)
    WHERE IsCurrent = 1 AND RunType <> 'Sandbox';
GO


/* ---------------------------------------------------------------------
   C1 · Run aggregates — the durable totals you return to later.
   Survives staging archival. Primary-key read, never a rescan.
   --------------------------------------------------------------------- */
CREATE TABLE ops.RunAggregate (
    RunAggregateId  BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunId           BIGINT NOT NULL REFERENCES ops.ReconRun(RunId),
    DefinitionId    INT NOT NULL,
    BusinessDate    DATE NOT NULL,
    DatasetId       INT NOT NULL,
    Side            VARCHAR(10) NOT NULL,          -- Left | Right
    GroupKey        NVARCHAR(200) NOT NULL DEFAULT '*',  -- '*' = whole dataset; else e.g. 'Direction=Inward'
    MatchStatus     VARCHAR(20) NOT NULL DEFAULT '*',    -- '*' = all; Matched | Unmatched | Excluded | Duplicate
    RowCount        BIGINT NOT NULL,
    AmountMinorSum  BIGINT NOT NULL,
    CurrencyCode    CHAR(3) NOT NULL,
    CreatedAt       DATETIME2(3) NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT UQ_RunAggregate UNIQUE (RunId, DatasetId, Side, GroupKey, MatchStatus)
);
CREATE INDEX IX_RunAggregate_Period ON ops.RunAggregate (DefinitionId, BusinessDate, DatasetId, GroupKey);
GO


/* ---------------------------------------------------------------------
   A3 · Passes write only to MatchResult; staging updated once at the end.
   Drop the filtered index that would have churned on every pass.
   --------------------------------------------------------------------- */
DROP INDEX IX_Staging_Unmatched ON stg.StagingTransaction;
GO
-- Per-run lookup used by the NOT EXISTS anti-join between passes
CREATE NONCLUSTERED INDEX IX_MatchResult_RunLeft
    ON ops.MatchResult (RunId, LeftStagingId, BusinessDate) ON ps_ByMonth(BusinessDate);
CREATE NONCLUSTERED INDEX IX_MatchResult_RunRight
    ON ops.MatchResult (RunId, RightStagingId, BusinessDate) ON ps_ByMonth(BusinessDate);
GO
ALTER TABLE ops.MatchResult
    ADD LeftAggregateKey    NVARCHAR(200) NULL,   -- Aggregate mode: the group that matched
        RightAggregateKey   NVARCHAR(200) NULL;
GO

ALTER TABLE stg.StagingTransaction DROP CONSTRAINT CK_Staging_MatchStatus;
ALTER TABLE stg.StagingTransaction ADD CONSTRAINT CK_Staging_MatchStatus CHECK
    (MatchStatus IN ('Unmatched','Matched','Ambiguous','AutoClosed','Excluded','Duplicate'));
GO


/* ---------------------------------------------------------------------
   C2 · Exceptions carry their own key snapshot (independent of staging)
   --------------------------------------------------------------------- */
ALTER TABLE ops.ReconException
    ADD KeyValuesJson       NVARCHAR(MAX) NULL,   -- {FieldCode: value} of matchable fields
        CurrencyCode        CHAR(3) NULL;
GO


/* ---------------------------------------------------------------------
   D2 · Least-privilege roles
   --------------------------------------------------------------------- */
CREATE ROLE recon_loader;   GRANT SELECT, INSERT ON SCHEMA::stg TO recon_loader;
                            GRANT SELECT, INSERT, UPDATE ON SCHEMA::ops TO recon_loader;
                            GRANT SELECT ON SCHEMA::cfg TO recon_loader;
CREATE ROLE recon_app;      GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::cfg TO recon_app;
                            GRANT SELECT, INSERT, UPDATE ON SCHEMA::ops TO recon_app;
                            GRANT SELECT ON SCHEMA::stg TO recon_app;
                            GRANT INSERT ON SCHEMA::aud TO recon_app;
CREATE ROLE recon_activator; GRANT CREATE VIEW TO recon_activator;
                            GRANT ALTER ON SCHEMA::stg TO recon_activator;  -- views + index creation on stg only
CREATE ROLE recon_reader;   GRANT SELECT ON SCHEMA::cfg TO recon_reader;
                            GRANT SELECT ON SCHEMA::ops TO recon_reader;
                            GRANT SELECT ON SCHEMA::stg TO recon_reader;
GO


/* ---------------------------------------------------------------------
   E6 · Drift alert event type is just data; documenting the value:
        AlertPolicy.EventType += 'MatchDistributionDrift'
   E2 · Partition maintenance — keep ≥3 future partitions.
        Scheduled job (outline):
          DECLARE @next DATE = DATEADD(MONTH, 1, <last boundary>);
          ALTER PARTITION SCHEME ps_ByMonth NEXT USED [PRIMARY];
          ALTER PARTITION FUNCTION pf_ByMonth() SPLIT RANGE (@next);
        Alert if fewer than 2 future boundaries exist.
   E3 · Retention: StagingMonthsOnline (default 3) and ResultsMonthsOnline
        (= retention period, still open). Archive = SWITCH PARTITION to an
        identically structured archive table on cheaper storage.
   A8 · ALTER TABLE stg.StagingTransaction REBUILD PARTITION = <n>
        WITH (DATA_COMPRESSION = PAGE)  for partitions older than the
        current month.
   --------------------------------------------------------------------- */
