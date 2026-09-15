/* =====================================================================
   RECONCILIATION PLATFORM — DATABASE OPTIONS
   v1.0
   ---------------------------------------------------------------------
   FIX (v1.0, finding 7): in v0.2 this statement was COMMENTED OUT in the
   delta script, even though the review calls it "non-negotiable at this
   volume". A commented-out non-negotiable is a non-negotiable that gets
   skipped. It is a real, deliberate deployment step here.

   A5 · Read consistency. The portal queries staging and results while a
   run is inserting 2M rows. Without snapshot isolation on reads, the
   portal blocks behind the loader and Operations concludes the system is
   down.

   WITH ROLLBACK IMMEDIATE terminates open transactions to take the lock,
   so run this during a maintenance window — not on a live loader.
   ===================================================================== */

/* FIX: the database name was hard-coded, so this script only worked for a
   database called ReconPlatform — and the demo script had to skip it and
   repeat the statement with its own name, which is how a "deliberate
   deployment step" becomes two statements that can disagree. It now applies
   to whichever database it is run in, so the portal's installer, the test
   harness and the demo all run the same file. */
DECLARE @sql NVARCHAR(400) =
    N'ALTER DATABASE ' + QUOTENAME(DB_NAME())
    + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;';
EXEC sp_executesql @sql;
GO

/* Recovery model: BULK_LOGGED makes the sorted SqlBulkCopy into the
   clustered index minimally logged. Confirm with the DBA team against
   the backup and point-in-time-recovery policy before applying. */
-- ALTER DATABASE [<this database>] SET RECOVERY BULK_LOGGED;
-- GO

/* Verify:
   SELECT name, is_read_committed_snapshot_on, snapshot_isolation_state_desc,
          recovery_model_desc
   FROM sys.databases WHERE name = DB_NAME();
*/


/* =====================================================================
   A8 · Page compression for partitions older than the current month
   (~60% saving on the text slots). Run from the monthly maintenance job.

   DECLARE @p INT = $PARTITION.pf_ByMonth(DATEADD(MONTH, -1, GETDATE()));
   ALTER TABLE stg.StagingTransaction
       REBUILD PARTITION = @p WITH (DATA_COMPRESSION = PAGE);

   E2 · Partition maintenance — keep >= 3 future monthly partitions and
   alert below 2 (cfg.PlatformSetting: FuturePartitionsMin /
   FuturePartitionsAlertAt). If no future partition exists at month
   rollover, every row lands in the last range and the sliding window
   breaks.

   DECLARE @next DATE = DATEADD(MONTH, 1, <last boundary>);
   ALTER PARTITION SCHEME ps_ByMonth NEXT USED [PRIMARY];
   ALTER PARTITION FUNCTION pf_ByMonth() SPLIT RANGE (@next);

   E3 · Retention — StagingMonthsOnline and ResultsMonthsOnline in
   cfg.PlatformSetting. Archiving is SWITCH PARTITION into an identically
   structured archive table on cheaper storage, never DELETE.
   ===================================================================== */
