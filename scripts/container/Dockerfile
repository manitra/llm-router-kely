# syntax=docker/dockerfile:1
#
# The build context is the repository root, so build with:
#   docker build -f scripts/container/Dockerfile .
# Ignore rules live in scripts/container/Dockerfile.dockerignore, which BuildKit
# picks up because it sits next to this file.

# --- Build --------------------------------------------------------------------
# Alpine keeps the published image small and, unlike the chiseled variants, it
# keeps a busybox shell that the entrypoint and health check need.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /src

# Native AOT links a native executable, so it needs clang, the musl toolchain and zlib.
RUN apk add --no-cache clang build-base zlib-dev

# Translate Docker's architecture names (amd64/arm64) into .NET runtime identifiers.
# Alpine is musl-based, so the musl runtime identifiers are required.
RUN case "${TARGETARCH:-$(apk --print-arch)}" in \
      amd64|x86_64) echo linux-musl-x64 > /tmp/rid ;; \
      arm64|aarch64) echo linux-musl-arm64 > /tmp/rid ;; \
      *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac

# Restore in its own layer so source edits do not invalidate the package cache.
COPY src/RouterKely.Core/RouterKely.Core.csproj src/RouterKely.Core/
COPY src/RouterKely/RouterKely.csproj src/RouterKely/
RUN dotnet restore src/RouterKely/RouterKely.csproj --runtime "$(cat /tmp/rid)"

COPY src/ src/
RUN dotnet publish src/RouterKely/RouterKely.csproj \
      --configuration Release \
      --runtime "$(cat /tmp/rid)" \
      --no-restore \
      --output /out \
      --nologo

# --- Runtime ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime

# /data is the only writable location: it keeps the configuration file the operator
# edits and the identity file the admin UI rewrites. The directory is owned by the
# runtime user so a named volume inherits working permissions.
# ROUTERKELY_LISTEN_URL overrides the listen URL from the configuration file and
# must stay reachable from the platform's reverse proxy.
ENV ROUTERKELY_CONFIG=/data/router-kely.local.json \
    ROUTERKELY_LISTEN_URL=http://0.0.0.0:8080 \
    ROUTERKELY_HEALTH_URL=http://127.0.0.1:8080/health/ready

# su-exec lets the entrypoint hand a bind-mounted volume to the runtime user and then
# drop privileges; it is the only addition to the base image (65 KiB).
RUN apk add --no-cache su-exec \
 && mkdir -p /data \
 && chown app:app /data \
 && chmod 0700 /data

WORKDIR /app
COPY --from=build /out/ ./
COPY config/router-kely.local.json.example /app/config/router-kely.local.json.example
COPY scripts/container/entrypoint.sh scripts/container/healthcheck.sh /usr/local/bin/
RUN chmod 0755 /usr/local/bin/entrypoint.sh /usr/local/bin/healthcheck.sh

VOLUME ["/data"]
EXPOSE 8080

# The image deliberately has no USER directive. The entrypoint starts as root only to
# take ownership of a host-mounted /data directory, then drops to the unprivileged
# "app" user (uid 1654) before the router process starts. Starting the router itself as
# root is never required, and platforms that force a non-root user still work as long
# as the volume is writable by uid 1654.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/usr/local/bin/healthcheck.sh"]

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["/app/RouterKely"]
