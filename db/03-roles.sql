/* =====================================================================
   RECONCILIATION PLATFORM — DATABASE ROLES (D2)
   v1.0  |  run after 01-schema.sql
   ---------------------------------------------------------------------
   The application is NEVER db_owner. Four roles, least privilege.
   ===================================================================== */

/* ---------------------------------------------------------------------
   recon_loader — the ingestion worker.
   Inserts staged rows and parse errors, records run progress.
   --------------------------------------------------------------------- */
CREATE ROLE recon_loader;
GRANT SELECT, INSERT          ON SCHEMA::stg TO recon_loader;
GRANT SELECT, INSERT, UPDATE  ON SCHEMA::ops TO recon_loader;
GRANT SELECT                  ON SCHEMA::cfg TO recon_loader;
GRANT INSERT                  ON SCHEMA::aud TO recon_loader;
GO

/* ---------------------------------------------------------------------
   recon_app — the portal.
   Full CRUD on configuration, read/write on operations, read on staging.
   Append-only on audit: the application must not be able to rewrite
   history it is the subject of.
   --------------------------------------------------------------------- */
CREATE ROLE recon_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::cfg TO recon_app;
GRANT SELECT, INSERT, UPDATE         ON SCHEMA::ops TO recon_app;
GRANT SELECT                         ON SCHEMA::stg TO recon_app;
GRANT INSERT                         ON SCHEMA::aud TO recon_app;
/* FIX (v1.0, finding 5): v0.2 granted INSERT on aud and nothing else, to
   anyone. Design §13 promises an audit log VIEWER and D3 wants export
   actions logged for regulators — but no role could read the table, so
   the viewer could not have worked. SELECT is granted explicitly here,
   while UPDATE and DELETE remain granted to nobody at all. */
GRANT SELECT                         ON SCHEMA::aud TO recon_app;
GO

/* ---------------------------------------------------------------------
   recon_activator — the dataset activation service.
   Creates the readable view and the matching indexes for a dataset when
   Operations activates it.

   NOTE (v1.0, finding 10): v0.2 used GRANT ALTER ON SCHEMA::stg, which
   also permits ALTER TABLE — adding and dropping columns on a table with
   hundreds of millions of rows — far beyond the "views and indexes only"
   intent of design §14. Object-level grants are used instead: the role
   can index the one staging table it must index, and nothing else.
   --------------------------------------------------------------------- */
CREATE ROLE recon_activator;
GRANT CREATE VIEW                                TO recon_activator;
GRANT ALTER ON OBJECT::stg.StagingTransaction    TO recon_activator;  -- CREATE/DROP INDEX on this table
GRANT SELECT ON SCHEMA::cfg                      TO recon_activator;  -- reads the field registry
GRANT SELECT ON SCHEMA::stg                      TO recon_activator;  -- views must reference the table
GRANT INSERT ON SCHEMA::aud                      TO recon_activator;
GO

/* ---------------------------------------------------------------------
   recon_reader — reporting and read-only portal users.
   --------------------------------------------------------------------- */
CREATE ROLE recon_reader;
GRANT SELECT ON SCHEMA::cfg TO recon_reader;
GRANT SELECT ON SCHEMA::ops TO recon_reader;
GRANT SELECT ON SCHEMA::stg TO recon_reader;
GRANT SELECT ON SCHEMA::aud TO recon_reader;
GO

/* ---------------------------------------------------------------------
   Nobody gets UPDATE or DELETE on aud. Deliberate and load-bearing.
   Verify after deployment:

   SELECT dp.name AS principal, p.permission_name, p.state_desc
   FROM sys.database_permissions p
   JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
   WHERE p.major_id = SCHEMA_ID('aud');
   --------------------------------------------------------------------- */
