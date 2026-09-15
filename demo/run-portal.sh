#!/usr/bin/env bash
# =====================================================================
# The portal, against the demo database.
#
#   ./demo/run-portal.sh                 # foreground, Ctrl-C to stop
#   ./demo/run-portal.sh --background    # detached, prints the URL
#   ./demo/run-portal.sh --stop          # stop a detached one
#
# Expects ./demo/run-demo.sh to have run first: the portal shows what a
# run produced, and an empty database shows empty screens.
#
# Runs the app in the .NET SDK container on the host network, so it
# reaches SQL Server at 127.0.0.1:1433 and the browser reaches it at
# 127.0.0.1:5080. Nothing is installed on the host.
#
# Sign in as one of the three demo operators to see the access levels
# differ on the same screens:
#
#     cfg.omar    Configure  — edits datasets, rules, fees, grants
#     ops.hala    Operate    — triggers runs, works exceptions
#     read.sami   Read       — sees everything, changes nothing
#     anyone else            — sees nothing at all, which is the correct
#                              default for an account with no grants
# =====================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NAME="${RECON_PORTAL_CONTAINER:-reconportal}"
IMAGE="${RECON_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}"
CACHE="${RECON_NUGET_CACHE:-$HOME/.nuget-recon}"
SA_PW="${RECON_TEST_PASSWORD:-Recon#Verify2026x}"
DB="${RECON_PORTAL_DB:-ReconDemo}"
PORT="${RECON_PORTAL_PORT:-5080}"

CONN="Server=127.0.0.1,1433;Database=$DB;User Id=sa;Password=$SA_PW;TrustServerCertificate=True;Encrypt=False"

if [ "${1:-}" = "--stop" ]; then
    docker rm -f "$NAME" >/dev/null 2>&1 && echo "stopped $NAME" || echo "$NAME was not running"
    exit 0
fi

mkdir -p "$CACHE"

DETACH=()
[ "${1:-}" = "--background" ] && DETACH=(-d)

docker rm -f "$NAME" >/dev/null 2>&1 || true

# The proxy and CA bundle are passed through for the same reason build.sh
# does it: NuGet restore goes through the agent proxy, and TLS must still
# verify.
PROXY_ARGS=()
if [ -n "${HTTPS_PROXY:-}" ]; then
    PROXY_ARGS+=(-e "HTTPS_PROXY=$HTTPS_PROXY" -e "HTTP_PROXY=${HTTP_PROXY:-$HTTPS_PROXY}")
fi
CA_ARGS=()
if [ -f /root/.ccr/ca-bundle.crt ]; then
    CA_ARGS+=(-v /root/.ccr/ca-bundle.crt:/etc/ssl/certs/agent-ca.crt:ro
              -e SSL_CERT_FILE=/etc/ssl/certs/agent-ca.crt)
fi

if [ "${#DETACH[@]}" -gt 0 ]; then
    echo "starting the portal on http://127.0.0.1:$PORT against [$DB]"
fi

docker run --rm "${DETACH[@]+"${DETACH[@]}"}" --name "$NAME" --network host \
    -v "$ROOT:/src" -v "$CACHE:/nuget" \
    "${PROXY_ARGS[@]+"${PROXY_ARGS[@]}"}" "${CA_ARGS[@]+"${CA_ARGS[@]}"}" \
    -e NUGET_PACKAGES=/nuget \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    -e "ConnectionStrings__Recon=$CONN" \
    -e "ASPNETCORE_URLS=http://127.0.0.1:$PORT" \
    -e "ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Development}" \
    -w /src "$IMAGE" \
    dotnet run --project src/Recon.Web --no-launch-profile

if [ "${#DETACH[@]}" -gt 0 ]; then
    printf 'waiting for it to answer'
    for _ in $(seq 1 60); do
        if curl -fsS --noproxy 127.0.0.1 "http://127.0.0.1:$PORT/account/signin" >/dev/null 2>&1; then
            printf ' ready\n'
            echo
            echo "  http://127.0.0.1:$PORT   sign in as cfg.omar, ops.hala or read.sami"
            echo "  ./demo/run-portal.sh --stop   when you are done"
            exit 0
        fi
        printf '.'; sleep 2
    done
    echo
    echo "it did not answer in time; logs:"
    docker logs --tail 40 "$NAME" || true
    exit 1
fi
