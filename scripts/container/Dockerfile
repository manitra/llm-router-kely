# syntax=docker/dockerfile:1
#
# The build context is the repository root, so build with:
#   docker build -f scripts/container/Dockerfile .
# Ignore rules live in scripts/container/Dockerfile.dockerignore, which BuildKit
# picks up because it sits next to this file.
#
# RUNNING THE PUBLISHED IMAGE
# The image has no built-in defaults for secrets and no bundled data. A deployment
# must provide both, or the container exits at startup:
#
#   docker run -d --name router-kely -p 8080:8080 \
#     -v router-kely-data:/data \
#     -e ROUTERKELY_ADMIN_API_KEY=sk-rk_... \
#     -e ROUTERKELY_DEEPSEEK_API_KEY=sk-... \
#     ghcr.io/manitra/llm-router-kely:latest
#
#   1. ROUTERKELY_ADMIN_API_KEY (required) - the administrator bearer key.
#      ROUTERKELY_DEEPSEEK_API_KEY (required) - the upstream credential.
#      Both are read at startup and never written to disk. The router refuses to
#      start without them and reports every missing variable in one message.
#   2. A volume mounted at /data - holds the configuration file and the identity
#      file the admin UI rewrites. A named Docker volume needs no preparation: it
#      inherits the ownership of the image's /data directory. A host bind mount
#      must be chowned to 1654:1654 first, because the container runs unprivileged
#      and never takes ownership of a mount.
#   3. Port 8080 - the internal HTTP listener. Terminate TLS in front of it.
#
# On the first start the router creates /data/router-kely.local.json from its
# embedded default; edit it and restart to change the configuration.

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

# Labels surface in the registry UI and in `docker inspect`, so an operator who finds
# the image without the README still sees what a deployment has to provide.
LABEL org.opencontainers.image.title="LLM Router Kely" \
      org.opencontainers.image.description="OpenAI-compatible LLM router. Requires ROUTERKELY_ADMIN_API_KEY and ROUTERKELY_DEEPSEEK_API_KEY, a volume mounted at /data, and port 8080." \
      org.opencontainers.image.source="https://github.com/manitra/llm-router-kely" \
      org.opencontainers.image.url="https://github.com/manitra/llm-router-kely#deploy" \
      org.opencontainers.image.licenses="MIT"

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

# The entrypoint and the health check are written here rather than copied from the
# repository so the container directory holds only the build and deployment artifacts.
#
# Entrypoint: the container runs as uid 1654 and never as root, so it cannot take
# ownership of a mount and must not modify the host directory either. The volume
# therefore has to be writable by that user already, and this only verifies it before
# the router does its own work (creating the configuration file when absent, then
# reporting every referenced variable that is unset).
#
# Health check: busybox wget, because the runtime image has no curl. It fails on any
# non-success status, which is what makes the container health check meaningful.
RUN printf '%s\n' \
      '#!/bin/sh' \
      'set -eu' \
      'config_path="${ROUTERKELY_CONFIG:-/data/router-kely.local.json}"' \
      'config_dir="$(dirname "$config_path")"' \
      'abort_unwritable() {' \
      '  echo "router-kely: ${1} is not writable by uid $(id -u)." >&2' \
      '  echo "router-kely: mount a Docker volume, or chown the host directory to 1654:1654." >&2' \
      '  exit 1' \
      '}' \
      'mkdir -p "$config_dir" 2>/dev/null || abort_unwritable "$config_dir"' \
      '[ -w "$config_dir" ] || abort_unwritable "$config_dir"' \
      'exec "$@"' \
      > /usr/local/bin/entrypoint.sh \
 && printf '%s\n' \
      '#!/bin/sh' \
      'set -eu' \
      'wget -q -T 5 -O - "${ROUTERKELY_HEALTH_URL:-http://127.0.0.1:8080/health/ready}" | grep -q '"'"'"status":"ready"'"'"'' \
      > /usr/local/bin/healthcheck.sh \
 && chmod 0755 /usr/local/bin/entrypoint.sh /usr/local/bin/healthcheck.sh

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
