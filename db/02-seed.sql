/* =====================================================================
   RECONCILIATION PLATFORM — SEED DATA
   v1.0  |  run after 01-schema.sql
   ===================================================================== */

/* ---------------------------------------------------------------------
   Currencies. MinorUnits drives every integer amount in the system.
   --------------------------------------------------------------------- */
INSERT INTO cfg.Currency (CurrencyCode, Name, MinorUnits) VALUES
    ('JOD', N'Jordanian Dinar', 3),
    ('USD', N'US Dollar',       2),
    ('EUR', N'Euro',            2),
    ('SAR', N'Saudi Riyal',     2),
    ('AED', N'UAE Dirham',      2);
GO


/* ---------------------------------------------------------------------
   Slot catalogue.

   Text1..Text20   reservable  — a field's own storage
   Text21..Text30  companion   — normalized values written at parse time

   The split is finding 2's fix: in v0.2 both pools were the same 30
   columns and nothing stopped a normalized companion from overwriting a
   mapped field. Widening later means ALTER TABLE on a table with
   hundreds of millions of rows, so the pools are generous now — empty
   columns cost almost nothing in SQL Server.
   --------------------------------------------------------------------- */

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength, IsNormalizedOnly)
SELECT 'Text' + CAST(n AS VARCHAR(2)), 'String', 300,
       CASE WHEN n > 20 THEN 1 ELSE 0 END
FROM (SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength, IsNormalizedOnly)
SELECT 'Num' + CAST(n AS VARCHAR(2)), 'Integer', NULL, 0
FROM (SELECT TOP (15) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength, IsNormalizedOnly)
SELECT 'Dec' + CAST(n AS VARCHAR(2)), 'Decimal', NULL, 0
FROM (SELECT TOP (5) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength, IsNormalizedOnly)
SELECT 'Date' + CAST(n AS VARCHAR(2)), 'DateTime', NULL, 0
FROM (SELECT TOP (8) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;

INSERT INTO cfg.StorageSlotCatalogue (SlotName, SlotType, MaxLength, IsNormalizedOnly)
SELECT 'Flag' + CAST(n AS VARCHAR(2)), 'Boolean', NULL, 0
FROM (SELECT TOP (5) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
      FROM sys.all_objects) x;
GO


/* ---------------------------------------------------------------------
   Platform settings.
   FIX (finding 4): in v0.2 these values existed only as SQL comments,
   with no table to hold them.
   --------------------------------------------------------------------- */

INSERT INTO cfg.PlatformSetting (SettingKey, SettingValue, DataType, Description) VALUES
    ('StagingMonthsOnline',     '3',    'Int',
        N'Months of stg.StagingTransaction kept online. The single largest cost in the system; RunAggregate and exception key snapshots make a short value safe.'),
    ('ResultsMonthsOnline',     '84',   'Int',
        N'Months of results, exceptions and aggregates kept online. OPEN: confirm the regulatory retention period — 84 is a 7-year placeholder, not a decision.'),
    ('SandboxPurgeDays',        '7',    'Int',
        N'Sandbox runs are purged by a nightly job after this many days (E5).'),
    ('FuturePartitionsMin',     '3',    'Int',
        N'Maintenance job keeps at least this many future monthly partitions ahead (E2).'),
    ('FuturePartitionsAlertAt', '2',    'Int',
        N'Raise a PartitionShortage alert when fewer than this many future partitions exist.'),
    ('BulkCopyBatchSize',       '100000','Int',
        N'SqlBulkCopy BatchSize. Rows must arrive sorted on (DatasetId, TxDate, StagingId).'),
    ('MaxIndexesPerDataset',    '4',    'Int',
        N'Cap on generated nonclustered matching indexes per dataset (A6). Every extra index is a 2M-row maintenance cost per load.'),
    ('ExcelMaxRowsPerSheet',    '1000000','Int',
        N'Sheet split threshold. A worksheet holds 1,048,576 rows; a 2M-row day does not fit (E1).'),
    ('MatchDriftThresholdPct',  '15',   'Decimal',
        N'Raise MatchDistributionDrift when the share of matches falling to the last pass exceeds this percentage.'),
    ('StagingLoadBudgetSeconds','120',  'Int',
        N'Phase 1 performance-spike budget for loading 2M synthetic rows. Exceeding it triggers the partition-switch escalation path (A1).');
GO
