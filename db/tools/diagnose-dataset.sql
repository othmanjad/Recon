/* =====================================================================
   "dataset X has no file format effective on YYYY-MM-DD" — why?

   Answers, in one pass, every reason a run can refuse to parse a
   dataset's file. Read only: it changes nothing.

       sqlcmd -S localhost -U sa -P '...' -d ReconPlatform \
              -v Code="OMReports" BusinessDate="2026-09-20" \
              -i db/tools/diagnose-dataset.sql

   In SSMS, set the two values below by hand instead.
   ===================================================================== */

SET NOCOUNT ON;

DECLARE @Code         VARCHAR(300) = '$(Code)';
DECLARE @BusinessDate DATE         = '$(BusinessDate)';

PRINT '--- 0. WHERE AM I -----------------------------------------------';
PRINT 'A portal pointed at a different database is the commonest cause of';
PRINT '"I created it and it says it does not exist". Compare this name with';
PRINT 'the one the portal shows on /setup.';
SELECT DB_NAME() AS [Database], SUSER_SNAME() AS [Login], @BusinessDate AS BusinessDate;

PRINT '--- 1. DATASETS WHOSE CODE LOOKS LIKE THIS ----------------------';
PRINT 'More than one row here means the definition may point at a different';
PRINT 'dataset than the one you have been editing. A trailing space shows as';
PRINT 'a difference in CodeLength.';
SELECT d.DatasetId, d.Code, LEN(d.Code) AS CodeLength, d.Name,
       d.ProviderType, d.IsActive, c.Code AS Counterparty
FROM cfg.Dataset AS d
JOIN cfg.Counterparty AS c ON c.CounterpartyId = d.CounterpartyId
WHERE d.Code LIKE '%' + @Code + '%'
ORDER BY d.DatasetId;

PRINT '--- 2. ITS FILE FORMATS, AND WHICH ONE IS IN FORCE ---------------';
PRINT 'InForce = 1 is the row the run would use. All zeroes with rows';
PRINT 'present means the business date is outside every range.';
SELECT f.FileFormatId, d.Code AS Dataset, f.Version, f.FormatType,
       f.EffectiveFrom, f.EffectiveTo, f.Delimiter, f.HasHeader,
       f.FileNamePattern, f.MaxParseErrors,
       CASE WHEN f.EffectiveFrom <= @BusinessDate
             AND (f.EffectiveTo IS NULL OR f.EffectiveTo >= @BusinessDate)
            THEN 1 ELSE 0 END AS InForce,
       (SELECT COUNT(*) FROM cfg.FieldMapping AS m
        WHERE m.FileFormatId = f.FileFormatId) AS Mappings
FROM cfg.FileFormatDefinition AS f
JOIN cfg.Dataset AS d ON d.DatasetId = f.DatasetId
WHERE d.Code LIKE '%' + @Code + '%'
ORDER BY d.Code, f.EffectiveFrom DESC, f.Version DESC;

PRINT '--- 3. THE MAPPINGS OF THE FORMAT IN FORCE -----------------------';
PRINT 'No rows means the format stages nothing. SourcePath must match the';
PRINT 'file header exactly.';
SELECT d.Code AS Dataset, fl.FieldCode, fl.FieldRole, fl.DataType,
       fl.StorageSlot, m.SourcePath, m.ParseFormat, m.IsRequired AS MappingRequired,
       fl.IsRequired AS FieldRequired
FROM cfg.FieldMapping AS m
JOIN cfg.FileFormatDefinition AS f ON f.FileFormatId = m.FileFormatId
JOIN cfg.Dataset AS d ON d.DatasetId = f.DatasetId
JOIN cfg.DatasetField AS fl ON fl.DatasetFieldId = m.DatasetFieldId
WHERE d.Code LIKE '%' + @Code + '%'
  AND f.EffectiveFrom <= @BusinessDate
  AND (f.EffectiveTo IS NULL OR f.EffectiveTo >= @BusinessDate)
ORDER BY d.Code, fl.DisplayOrder;

PRINT '--- 4. THE FIELD REGISTRY ---------------------------------------';
PRINT 'Reference, Amount, Currency and Direction are required to activate.';
PRINT 'Date is optional: without it every row is stamped with the business';
PRINT 'date. A Date field marked required rejects rows whose date is empty.';
SELECT d.Code AS Dataset, fl.FieldCode, fl.DisplayLabel, fl.DataType,
       fl.FieldRole, fl.StorageSlot, fl.IsMatchable, fl.IsIndexed, fl.IsRequired,
       CASE WHEN EXISTS (
           SELECT 1 FROM cfg.FieldMapping AS m
           JOIN cfg.FileFormatDefinition AS f2 ON f2.FileFormatId = m.FileFormatId
           WHERE m.DatasetFieldId = fl.DatasetFieldId
             AND f2.EffectiveFrom <= @BusinessDate
             AND (f2.EffectiveTo IS NULL OR f2.EffectiveTo >= @BusinessDate))
       THEN 1 ELSE 0 END AS MappedToday
FROM cfg.DatasetField AS fl
JOIN cfg.Dataset AS d ON d.DatasetId = fl.DatasetId
WHERE d.Code LIKE '%' + @Code + '%'
ORDER BY d.Code, fl.DisplayOrder;

PRINT '--- 5. WHICH DEFINITIONS USE IT ---------------------------------';
PRINT 'This is the dataset the run actually loads. If it is not the one you';
PRINT 'edited, the definition is pointing somewhere else.';
SELECT r.DefinitionId, r.Code AS Definition, r.IsActive,
       l.DatasetId AS LeftId,  l.Code AS LeftDataset,
       rt.DatasetId AS RightId, rt.Code AS RightDataset,
       r.MatchingWindowDaysBefore, r.MatchingWindowDaysAfter
FROM cfg.ReconciliationDefinition AS r
JOIN cfg.Dataset AS l  ON l.DatasetId  = r.LeftDatasetId
JOIN cfg.Dataset AS rt ON rt.DatasetId = r.RightDatasetId
WHERE l.Code LIKE '%' + @Code + '%' OR rt.Code LIKE '%' + @Code + '%'
ORDER BY r.DefinitionId;
