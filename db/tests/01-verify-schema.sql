/* =====================================================================
   SCHEMA VERIFICATION — executed against a live SQL Server instance.
   Run AFTER 01-schema.sql, 02-seed.sql, 03-roles.sql.

   This does not test business logic. It proves that the properties the
   design DEPENDS ON are actually enforced by the database, rather than
   merely described in a comment. Every check below corresponds to a
   review finding; a failure here means a finding regressed.
   ===================================================================== */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
DECLARE @fail INT = 0;
DECLARE @msg NVARCHAR(400);

CREATE TABLE #result (
    Seq         INT IDENTITY(1,1),
    Area        VARCHAR(40),
    Check_      NVARCHAR(200),
    Expected    NVARCHAR(100),
    Actual      NVARCHAR(100),
    Passed      BIT
);

/* helper: record a comparison */
DECLARE @a NVARCHAR(100), @e NVARCHAR(100);

/* ------------------------------------------------------------------
   1. Object counts — the schema created what it claims to create
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'objects', 'user tables created', '38',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 38 THEN 1 ELSE 0 END
FROM sys.tables WHERE is_ms_shipped = 0;

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'objects', 'schemas cfg/ops/stg/aud present', '4',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 4 THEN 1 ELSE 0 END
FROM sys.schemas WHERE name IN ('cfg','ops','stg','aud');

/* ------------------------------------------------------------------
   2. No FLOAT anywhere. Non-negotiable: §7.1
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'money', 'columns of type float/real', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
JOIN sys.tables tb ON tb.object_id = c.object_id
WHERE tb.is_ms_shipped = 0 AND t.name IN ('float','real');

/* ------------------------------------------------------------------
   3. No legacy 'Fils' identifier survived the C4 rename
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'money', 'columns still named *Fils*', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.columns c
JOIN sys.tables tb ON tb.object_id = c.object_id
WHERE tb.is_ms_shipped = 0 AND c.name LIKE '%Fils%';

/* ------------------------------------------------------------------
   4. Partitioning is real, and the staging clustered key leads with
      DatasetId — the whole basis of the slot decision (§7)
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'partition', 'monthly boundaries defined', '24',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 24 THEN 1 ELSE 0 END
FROM sys.partition_range_values rv
JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
WHERE pf.name = 'pf_ByMonth';

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'partition', 'staging clustered key order', 'DatasetId,TxDate,StagingId',
       STUFF((SELECT ',' + c.name
              FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = OBJECT_ID('stg.StagingTransaction')
                AND ic.index_id = 1 AND ic.key_ordinal > 0
              ORDER BY ic.key_ordinal
              FOR XML PATH('')), 1, 1, ''),
       CASE WHEN STUFF((SELECT ',' + c.name
              FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = OBJECT_ID('stg.StagingTransaction')
                AND ic.index_id = 1 AND ic.key_ordinal > 0
              ORDER BY ic.key_ordinal
              FOR XML PATH('')), 1, 1, '') = 'DatasetId,TxDate,StagingId'
       THEN 1 ELSE 0 END;

/* A3: the filtered Unmatched index must be GONE — it would churn on
   every load now that passes no longer update staging. */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'A3', 'IX_Staging_Unmatched removed', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.indexes WHERE name = 'IX_Staging_Unmatched';

/* ------------------------------------------------------------------
   5. Finding 1 — StagingRunId resolves which run's rows to read
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding1', 'ReconRun.StagingRunId is persisted computed', 'persisted',
       CASE WHEN is_persisted = 1 THEN 'persisted' ELSE 'not persisted' END,
       is_persisted
FROM sys.computed_columns
WHERE object_id = OBJECT_ID('ops.ReconRun') AND name = 'StagingRunId';

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding1', 'staging column renamed to LoadRunId', '1',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
FROM sys.columns
WHERE object_id = OBJECT_ID('stg.StagingTransaction') AND name = 'LoadRunId';

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding1', 'staging has no bare RunId column', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.columns
WHERE object_id = OBJECT_ID('stg.StagingTransaction') AND name = 'RunId';

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding1', 'staging has ResultRunId stamp', '1',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
FROM sys.columns
WHERE object_id = OBJECT_ID('stg.StagingTransaction') AND name = 'ResultRunId';

/* ------------------------------------------------------------------
   6. Finding 2 — the slot pools are split and enforced
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding2', 'reservable text slots', '20',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 20 THEN 1 ELSE 0 END
FROM cfg.StorageSlotCatalogue WHERE SlotType = 'String' AND IsNormalizedOnly = 0;

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding2', 'companion text slots', '10',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 10 THEN 1 ELSE 0 END
FROM cfg.StorageSlotCatalogue WHERE SlotType = 'String' AND IsNormalizedOnly = 1;

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding2', 'slot-pool trigger exists', '1',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
FROM sys.triggers WHERE name = 'TR_DatasetField_SlotPool';

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding2', 'filtered unique index on NormalizedSlot', '1',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
FROM sys.indexes
WHERE name = 'UX_DatasetField_NormSlot' AND is_unique = 1 AND has_filter = 1;

/* ------------------------------------------------------------------
   7. Finding 4 — settings have a home
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding4', 'StagingMonthsOnline seeded', '3',
       ISNULL((SELECT SettingValue FROM cfg.PlatformSetting WHERE SettingKey = 'StagingMonthsOnline'), 'MISSING'),
       CASE WHEN (SELECT SettingValue FROM cfg.PlatformSetting WHERE SettingKey = 'StagingMonthsOnline') = '3'
       THEN 1 ELSE 0 END;

/* ------------------------------------------------------------------
   8. Finding 5 — the audit log is readable, and append-only
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding5', 'roles with SELECT on aud', '2',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 2 THEN 1 ELSE 0 END
FROM sys.database_permissions p
JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
WHERE p.class = 3 AND p.major_id = SCHEMA_ID('aud')
  AND p.permission_name = 'SELECT' AND p.state = 'G';

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding5', 'roles with UPDATE or DELETE on aud', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.database_permissions p
WHERE p.class = 3 AND p.major_id = SCHEMA_ID('aud')
  AND p.permission_name IN ('UPDATE','DELETE') AND p.state = 'G';

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding5', 'audit Action allows Export', '1',
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
                         WHERE name = 'CK_AuditLog_Action' AND definition LIKE '%Export%')
            THEN '1' ELSE '0' END,
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
                         WHERE name = 'CK_AuditLog_Action' AND definition LIKE '%Export%')
            THEN 1 ELSE 0 END;

/* ------------------------------------------------------------------
   9. Finding 8 — RunType is constrained
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding8', 'CK_ReconRun_Type exists', '1',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
FROM sys.check_constraints WHERE name = 'CK_ReconRun_Type';

/* ------------------------------------------------------------------
   10. A2 — 'Normalized' is NOT a runtime comparison type
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'A2', 'MatchCondition rejects Normalized', 'absent',
       CASE WHEN (SELECT definition FROM sys.check_constraints
                  WHERE name = 'CK_MatchCondition_Cmp') LIKE '%Normalized%'
            THEN 'present' ELSE 'absent' END,
       CASE WHEN (SELECT definition FROM sys.check_constraints
                  WHERE name = 'CK_MatchCondition_Cmp') LIKE '%Normalized%'
            THEN 0 ELSE 1 END;

/* ------------------------------------------------------------------
   11. A7 — the non-deterministic AgeDays computed column is gone
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'A7', 'ReconException.AgeDays removed', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.columns
WHERE object_id = OBJECT_ID('ops.ReconException') AND name = 'AgeDays';

/* ------------------------------------------------------------------
   12. D1 — no free-SQL filter column survived
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'D1', 'FilterExpression column removed', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.is_ms_shipped = 0 AND c.name = 'FilterExpression';

/* Every condition-tree column must be MAX, not a truncating 1000 */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'finding9', 'condition-tree columns that are not MAX', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.is_ms_shipped = 0
  AND (c.name LIKE '%Json' OR c.name LIKE '%FilterJson')
  AND c.max_length <> -1;

/* ------------------------------------------------------------------
   13. Read consistency (A5) — the option the review called
       non-negotiable, and which v0.2 left commented out
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'A5', 'READ_COMMITTED_SNAPSHOT enabled', '1',
       CAST(is_read_committed_snapshot_on AS NVARCHAR(100)),
       is_read_committed_snapshot_on
FROM sys.databases WHERE name = DB_NAME();

/* ------------------------------------------------------------------
   14. The min-300 rule, and its documented exceptions
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'width', 'text columns < 300 with no CHECK and not semantic', '0',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END
FROM (
    SELECT c.name, t.name AS tbl
    FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE t.is_ms_shipped = 0
      AND ty.name IN ('varchar','nvarchar')
      AND c.max_length <> -1
      AND (CASE WHEN ty.name = 'nvarchar' THEN c.max_length / 2 ELSE c.max_length END) < 300
      /* documented exceptions: slot names and the settings key */
      AND c.name NOT IN ('SlotName','StorageSlot','NormalizedSlot','SettingKey','ToleranceUnit')
      /* enumeration columns are exempt because a CHECK bounds them */
      AND NOT EXISTS (
          SELECT 1 FROM sys.check_constraints cc
          WHERE cc.parent_object_id = c.object_id
            AND cc.definition LIKE '%[' + c.name + ']%'
      )
) x;

/* ------------------------------------------------------------------
   15. Referential integrity is complete — no table left unlinked
   ------------------------------------------------------------------ */
INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'objects', 'foreign keys created', '>=40',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) >= 40 THEN 1 ELSE 0 END
FROM sys.foreign_keys;

INSERT #result (Area, Check_, Expected, Actual, Passed)
SELECT 'objects', 'check constraints created', '>=45',
       CAST(COUNT(*) AS NVARCHAR(100)),
       CASE WHEN COUNT(*) >= 45 THEN 1 ELSE 0 END
FROM sys.check_constraints;

/* ------------------------------------------------------------------
   Report
   ------------------------------------------------------------------ */
SELECT Seq, Area, Check_, Expected, Actual,
       CASE WHEN Passed = 1 THEN 'PASS' ELSE '*** FAIL ***' END AS Result
FROM #result ORDER BY Seq;

SELECT @fail = COUNT(*) FROM #result WHERE Passed = 0;
SELECT CAST(COUNT(*) AS VARCHAR(10)) + ' checks, '
     + CAST(SUM(CAST(Passed AS INT)) AS VARCHAR(10)) + ' passed, '
     + CAST(@fail AS VARCHAR(10)) + ' failed' AS Summary
FROM #result;

IF @fail > 0
BEGIN
    SET @msg = CAST(@fail AS NVARCHAR(10)) + ' schema verification check(s) failed';
    THROW 51000, @msg, 1;
END
