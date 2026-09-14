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

ALTER DATABASE [ReconPlatform] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

/* Recovery model: BULK_LOGGED makes the sorted SqlBulkCopy into the
   clustered index minimally logged. Confirm with the DBA team against
   the backup and point-in-time-recovery policy before applying. */
-- ALTER DATABASE [ReconPlatform] SET RECOVERY BULK_LOGGED;
-- GO

/* Verify:
   SELECT name, is_read_committed_snapshot_on, snapshot_isolation_state_desc,
          recovery_model_desc
   FROM sys.databases WHERE name = 'ReconPlatform';
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
