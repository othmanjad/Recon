#!/usr/bin/env bash
# =====================================================================
# The whole platform, end to end, from nothing.
#
#   ./demo/run-demo.sh
#
# Starts SQL Server in a container, builds the schema, creates a CliQ ↔ OM
# configuration, then acquires, parses, stages, matches, classifies and
# proves the totals for one session — printing what happened at each step.
#
# Docker is the only prerequisite. Nothing is installed on the host: both
# SQL Server and the .NET SDK run in containers.
# =====================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTAINER="${RECON_TEST_CONTAINER:-reconsql}"
SA_PW="${RECON_TEST_PASSWORD:-Recon#Verify2026x}"
DB="ReconDemo"
TOOLS=/opt/mssql-tools18/bin/sqlcmd
BUSINESS_DATE="2026-09-13"

sq() { docker exec "$CONTAINER" "$TOOLS" -S localhost -U sa -P "$SA_PW" -C "$@"; }
step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

# ---------------------------------------------------------------------
step "SQL Server"
if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
    docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    docker run -d --name "$CONTAINER" --network host \
        -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SA_PW" -e MSSQL_PID=Developer \
        mcr.microsoft.com/mssql/server:2022-latest >/dev/null
    printf 'starting'
    for _ in $(seq 1 60); do
        if sq -Q "SELECT 1" >/dev/null 2>&1; then printf ' ready\n'; break; fi
        printf '.'; sleep 5
    done
else
    echo "  reusing the running $CONTAINER"
fi
sq -Q "SELECT 1" >/dev/null 2>&1 || { echo "SQL Server did not come up"; exit 1; }

# ---------------------------------------------------------------------
step "Schema"
sq -Q "IF DB_ID('$DB') IS NOT NULL BEGIN
           ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
           DROP DATABASE [$DB];
       END;
       CREATE DATABASE [$DB];" >/dev/null

# 04 applies to whichever database it is run in, so the demo runs the same
# four scripts the portal's installer does rather than repeating one of them.
for f in 01-schema 02-seed 03-roles 04-database-options; do
    docker cp "$ROOT/db/$f.sql" "$CONTAINER:/tmp/demo-$f.sql" >/dev/null
    sq -d "$DB" -b -i "/tmp/demo-$f.sql" >/dev/null
    echo "  db/$f.sql"
done

# ---------------------------------------------------------------------
step "Configuration"
docker cp "$ROOT/demo/01-demo-config.sql" "$CONTAINER:/tmp/demo-config.sql" >/dev/null
sq -d "$DB" -b -W -s " | " -i /tmp/demo-config.sql | grep -viE '^$|rows affected|^-+'

# ---------------------------------------------------------------------
CONN="Server=127.0.0.1,1433;Database=$DB;User Id=sa;Password=$SA_PW;TrustServerCertificate=True;Encrypt=False"

step "Can it be activated?"
"$ROOT/build.sh" run --project src/Recon.Cli -- validate --conn "$CONN" --definition 1

step "Reconcile one session"
"$ROOT/build.sh" run --project src/Recon.Cli -- run \
    --conn "$CONN" --definition 1 --business-date "$BUSINESS_DATE" --session S1 \
    --type Scheduled --by demo \
    --left-file demo/files/CLIQ_SESSION_20260913_S1.csv \
    --right-file demo/files/OM_TXN_20260913.csv || true

# ---------------------------------------------------------------------
step "What the run recorded"
sq -d "$DB" -W -s " | " -Q "
SET NOCOUNT ON;
PRINT '-- exceptions by code --';
SELECT ExceptionCode, Side, COUNT(*) AS Items,
       CAST(SUM(AmountMinor) / 1000.0 AS DECIMAL(18,3)) AS ValueJod
FROM ops.ReconException GROUP BY ExceptionCode, Side ORDER BY ExceptionCode;
PRINT '';
PRINT '-- staging outcome by status --';
SELECT d.Code AS Dataset, s.MatchStatus, COUNT(*) AS Rows_
FROM stg.StagingTransaction AS s JOIN cfg.Dataset AS d ON d.DatasetId = s.DatasetId
GROUP BY d.Code, s.MatchStatus ORDER BY d.Code, s.MatchStatus;
PRINT '';
PRINT '-- parse errors --';
SELECT RawRowNumber AS Line, ErrorType, FieldCode, ErrorMessage
FROM stg.ParseError ORDER BY RawRowNumber;
PRINT '';
PRINT '-- which pass matched what --';
SELECT r.RuleCode, st.RowsMatched AS Matched,
       CAST(DATEDIFF(MILLISECOND, st.StartedAt, st.CompletedAt) / 1000.0 AS DECIMAL(9,2)) AS Seconds
FROM ops.ReconRunStep AS st JOIN cfg.MatchRule AS r ON r.MatchRuleId = st.MatchRuleId
ORDER BY r.Sequence;
" | grep -viE '^$|rows affected|^-+'

step "Done"
cat <<EOF
  The database is still up as [$DB]. Poke at it:

    docker exec -it $CONTAINER $TOOLS -S localhost -U sa -P '$SA_PW' -C -d $DB

  Useful starting points:
    SELECT * FROM ops.ReconRun;                  -- the run and its snapshot
    SELECT * FROM ops.ReconRunStep;              -- every stage, with its SQL
    SELECT * FROM ops.ReconException;            -- the open items
    SELECT * FROM ops.RunAggregate;              -- the durable totals
    SELECT * FROM ops.ControlTotalResult;        -- the balance proof
    SELECT GeneratedSql FROM ops.ReconRunStep WHERE StepName = 'Match';

  The portal reads this same database. Start it and sign in:

    ./demo/run-portal.sh --background     # http://127.0.0.1:5080
    ./demo/run-portal.sh --stop

    cfg.omar    Configure   every form: datasets, rules, fees, settings, grants
    ops.hala    Operate     triggers runs and works exceptions
    read.sami   Read        the same data, no forms
    anyone else             nothing at all, and the screen says why

  To check it rather than look at it:

    cd tests/browser && npm install && node drive-portal.js
EOF
