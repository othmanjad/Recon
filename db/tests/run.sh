#!/usr/bin/env bash
# =====================================================================
# Build the schema on a throwaway SQL Server and run both test suites.
#
#   ./db/tests/run.sh              # start a container, build, test, keep it
#   ./db/tests/run.sh --teardown   # ...then remove the container
#
# Requires Docker. Nothing else — no local SQL Server, no sqlcmd; both
# come from the image.
# =====================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONTAINER="${RECON_TEST_CONTAINER:-reconsql}"
IMAGE="${RECON_TEST_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
SA_PW="${RECON_TEST_PASSWORD:-Recon#Verify2026x}"
DB="ReconPlatform"
TOOLS=/opt/mssql-tools18/bin/sqlcmd
TEARDOWN=0
[ "${1:-}" = "--teardown" ] && TEARDOWN=1

sq() { docker exec "$CONTAINER" "$TOOLS" -S localhost -U sa -P "$SA_PW" -C "$@"; }

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
    step "starting $CONTAINER"
    docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    docker run -d --name "$CONTAINER" --network host \
        -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SA_PW" -e MSSQL_PID=Developer \
        "$IMAGE" >/dev/null
    printf 'waiting for the instance'
    for _ in $(seq 1 60); do
        if sq -Q "SELECT 1" >/dev/null 2>&1; then printf ' ready\n'; break; fi
        printf '.'; sleep 5
    done
    sq -Q "SELECT 1" >/dev/null 2>&1 || { echo "instance never came up"; exit 1; }
else
    step "reusing the running $CONTAINER"
fi

step "copying scripts in"
# Copy each file to a flat path: the container runs as the mssql user and
# cannot overwrite a root-owned directory from an earlier copy.
for f in 01-schema 02-seed 03-roles 04-database-options; do
    docker cp "$ROOT/db/$f.sql" "$CONTAINER:/tmp/$f.sql" >/dev/null
done
for f in 01-verify-schema 02-behaviour; do
    docker cp "$ROOT/db/tests/$f.sql" "$CONTAINER:/tmp/test-$f.sql" >/dev/null
done

step "recreating $DB"
sq -Q "IF DB_ID('$DB') IS NOT NULL BEGIN
           ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
           DROP DATABASE [$DB];
       END;
       CREATE DATABASE [$DB];" >/dev/null

fail=0
for f in 01-schema 02-seed 03-roles 04-database-options; do
    step "$f.sql"
    if out=$(sq -d "$DB" -b -i "/tmp/$f.sql" 2>&1); then
        echo "  ok"
    else
        echo "$out" | grep -iE "^Msg |^Line " | head -10
        fail=1
    fi
done
[ "$fail" -eq 0 ] || { echo; echo "SCHEMA BUILD FAILED"; exit 1; }

for f in 01-verify-schema 02-behaviour; do
    step "tests/$f.sql"
    if out=$(sq -d "$DB" -b -W -s " | " -i "/tmp/test-$f.sql" 2>&1); then
        echo "$out" | grep -E "passed, " || true
    else
        echo "$out" | grep -viE "^$|rows affected|^-+" | grep -E "FAIL|Msg |passed, " | head -30
        fail=1
    fi
done

if [ "$TEARDOWN" -eq 1 ]; then
    step "removing $CONTAINER"
    docker rm -f "$CONTAINER" >/dev/null
fi

echo
if [ "$fail" -eq 0 ]; then
    printf '\033[32mALL GREEN\033[0m — schema built and both suites passed.\n'
else
    printf '\033[31mFAILURES ABOVE\033[0m\n'
    exit 1
fi
