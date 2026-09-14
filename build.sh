#!/usr/bin/env bash
# =====================================================================
# Build and test inside a .NET 8 SDK container.
#
#   ./build.sh               restore + build
#   ./build.sh test          build + run the unit tests
#   ./build.sh test-all      also run the integration tests (needs SQL Server;
#                            start it with ./db/tests/run.sh first)
#   ./build.sh perf          the Phase 1 2M-row spike (needs SQL Server)
#   ./build.sh <args...>     any other dotnet command
#
# Used instead of a local SDK because this environment's network policy
# blocks Microsoft's SDK download host while mcr.microsoft.com is reachable.
# On a machine with the SDK installed, run the same dotnet commands directly.
# =====================================================================
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGE="${RECON_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}"

# NuGet cache lives outside the repo so restores are not repeated per run.
CACHE="${RECON_NUGET_CACHE:-$HOME/.nuget-recon}"
mkdir -p "$CACHE"

# NuGet is not on the sandbox's direct-egress list, so restores go through
# the agent proxy. --network host makes its 127.0.0.1 address reachable from
# inside the container, and the CA bundle is mounted so TLS still verifies —
# never disable verification to get a restore through.
# The integration tests need a live SQL Server; the connection string is
# passed through so the container can reach it on the host network.
TEST_ARGS=()
if [ -n "${RECON_TEST_CONNECTION:-}" ]; then
    TEST_ARGS+=(-e "RECON_TEST_CONNECTION=$RECON_TEST_CONNECTION")
fi
if [ -n "${RECON_PERF_ROWS:-}" ]; then
    TEST_ARGS+=(-e "RECON_PERF_ROWS=$RECON_PERF_ROWS")
fi

PROXY_ARGS=()
if [ -n "${HTTPS_PROXY:-}" ]; then
    PROXY_ARGS+=(-e "HTTPS_PROXY=$HTTPS_PROXY" -e "HTTP_PROXY=${HTTP_PROXY:-$HTTPS_PROXY}")
fi
CA_ARGS=()
if [ -f /root/.ccr/ca-bundle.crt ]; then
    CA_ARGS+=(-v /root/.ccr/ca-bundle.crt:/etc/ssl/certs/agent-ca.crt:ro
              -e SSL_CERT_FILE=/etc/ssl/certs/agent-ca.crt)
fi

dn() {
    docker run --rm --network host \
        -v "$ROOT:/src" -v "$CACHE:/nuget" \
        "${PROXY_ARGS[@]+"${PROXY_ARGS[@]}"}" "${CA_ARGS[@]+"${CA_ARGS[@]}"}" \
        "${TEST_ARGS[@]+"${TEST_ARGS[@]}"}" \
        -e NUGET_PACKAGES=/nuget \
        -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        -e DOTNET_NOLOGO=1 \
        -w /src "$IMAGE" dotnet "$@"
}

case "${1:-build}" in
    build)
        dn build Recon.sln -warnaserror
        ;;
    test)
        dn test tests/Recon.UnitTests/Recon.UnitTests.csproj "${@:2}"
        ;;
    test-all)
        dn test Recon.sln "${@:2}"
        ;;
    test-integration)
        dn test tests/Recon.IntegrationTests/Recon.IntegrationTests.csproj "${@:2}"
        ;;
    perf)
        # The Phase 1 performance spike. Needs a live SQL Server and takes
        # minutes, so it is excluded from the normal suites by RECON_PERF_ROWS.
        : "${RECON_PERF_ROWS:=2000000}"
        export RECON_PERF_ROWS
        TEST_ARGS+=(-e "RECON_PERF_ROWS=$RECON_PERF_ROWS")
        dn test tests/Recon.IntegrationTests/Recon.IntegrationTests.csproj \
            --filter "FullyQualifiedName~PerformanceSpike" \
            --logger "console;verbosity=detailed" "${@:2}"
        ;;
    *)
        dn "$@"
        ;;
esac
