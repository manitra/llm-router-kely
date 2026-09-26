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
# The executable embeds config/router-kely.local.json.example as its first-start default.
# The ignore file excludes the local secret files, so only the template is copied.
COPY config/ config/
RUN dotnet publish src/RouterKely/RouterKely.csproj \
      --configuration Release \
      --runtime "$(cat /tmp/rid)" \
      --no-restore \
      --output /out \
      --nologo

# --- Runtime ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime

# /data is the only writable location: it keeps the configuration file the operator
# edits and the identity file the admin UI rewrites. It is owned by the runtime user so
# a Docker volume mounted here inherits working permissions.
# ROUTERKELY_HEALTH_URL is the readiness URL the image health check probes.
#
# ASPNETCORE_FORWARDEDHEADERS_ENABLED makes the process honour X-Forwarded-Proto. The
# platform reverse proxy terminates TLS, so without it Request.Scheme stays "http" while
# the browser's Origin is "https", and every admin login fails the same-origin check
# with 403. Trusting the immediate proxy is correct here because the container is only
# reachable through it.
ENV ROUTERKELY_CONFIG=/data/router-kely.local.json \
    ROUTERKELY_HEALTH_URL=http://127.0.0.1:8080/health/ready \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

RUN mkdir -p /data \
 && chown app:app /data \
 && chmod 0700 /data

WORKDIR /app
COPY --from=build /out/ ./
COPY scripts/container/entrypoint.sh scripts/container/healthcheck.sh /usr/local/bin/
RUN chmod 0755 /usr/local/bin/entrypoint.sh /usr/local/bin/healthcheck.sh

VOLUME ["/data"]
EXPOSE 8080

# The container is unprivileged for its whole lifetime: it never starts as root, so it
# cannot take ownership of a mount. The volume therefore has to be writable by this
# user already, which a Docker volume is because it inherits the ownership above.
USER 1654:1654

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/usr/local/bin/healthcheck.sh"]

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["/app/RouterKely"]
