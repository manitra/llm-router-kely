#!/usr/bin/env bash
# End-to-end check of the container wrapper: image build, first-boot seeding of the
# persistent volume, environment-supplied secrets, and configuration validation.
# Requires Docker, and is not part of scripts/tests.sh because building the image
# runs a full Native AOT publish.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d "$repo_root/scripts/.tmp-container-test.XXXXXX")"
readonly image="llm-router-kely:container-test"
readonly container="routerkely-container-test"
# One-shot runs that are expected to fail at startup use a separate name so they
# never collide with the long-running container.
readonly probe="routerkely-container-test-probe"
readonly named_container="routerkely-container-test-named"
readonly named_volume="routerkely-container-test-data"
readonly blocked_volume="routerkely-container-test-blocked"
readonly port="${ROUTERKELY_TEST_PORT:-18080}"
readonly admin_key="sk-rk-container-test-admin"
readonly config_path="$work_dir/router-kely.local.json"

cleanup() {
  docker rm --force "$container" "$probe" "$named_container" >/dev/null 2>&1 || true
  docker volume rm --force "$named_volume" "$blocked_volume" >/dev/null 2>&1 || true
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

fail() {
  echo "container test failed: $*" >&2
  exit 1
}

container_env() {
  docker run "$@" \
    --env ROUTERKELY_ADMIN_API_KEY="$admin_key" \
    --env ROUTERKELY_DEEPSEEK_API_KEY=sk-container-test-upstream \
    "$image"
}

start_container() {
  container_env --detach --name "$container" \
    --publish "127.0.0.1:$port:8080" \
    --volume "$work_dir:/data" \
    --read-only \
    --security-opt no-new-privileges:true >/dev/null
}

is_running() {
  [[ "$(docker inspect --format '{{.State.Running}}' "$1")" == "true" ]]
}

# Returns 0 once the named container stops, 1 if it is still running after 30 seconds.
wait_for_exit() {
  for _ in $(seq 1 30); do
    is_running "$1" || return 0
    sleep 1
  done
  return 1
}

wait_for_ready() {
  for _ in $(seq 1 60); do
    curl --fail --silent "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1 && return 0
    is_running "$1" || return 1
    sleep 1
  done
  return 1
}

# The router is PID 1, so its uid proves the entrypoint dropped privileges. Checking
# /proc is required because the image intentionally has no USER directive, which means
# docker exec itself still runs as root.
process_uid() {
  docker exec "$1" sh -c "grep '^Uid:' /proc/1/status | awk '{print \$2}'"
}

# Reproduces a data directory that belongs to root, which is what a bind mount looks
# like on Linux (Coolify's persistent storage included) and what Docker Desktop hides by
# presenting bind mounts as writable regardless of ownership. The marker file is
# essential: Docker re-initializes an *empty* named volume from the image, which would
# restore the directory to the runtime user and hide the behaviour under test.
prepare_root_owned_volume() {
  docker rm --force "$probe" >/dev/null 2>&1 || true
  docker volume rm --force "$blocked_volume" >/dev/null 2>&1 || true
  docker volume create "$blocked_volume" >/dev/null
  docker run --rm --user 0:0 --entrypoint sh --volume "$blocked_volume:/data" "$image" \
    -c 'chown 0:0 /data && chmod 0700 /data && touch /data/.keep' >/dev/null
}

# BuildKit only applies the ignore file that sits next to the Dockerfile and is named
# after it, so this guards against the rules silently going unused after a rename.
assert_build_context_is_filtered() {
  echo "==> Asserting the build context excludes bin, obj, dotfiles, and local secrets"
  local ignore_file="$repo_root/scripts/container/Dockerfile.dockerignore"
  local probe_dir="$work_dir/ignore-probe"
  if [[ ! -f "$ignore_file" ]]; then
    fail "$ignore_file is missing, so BuildKit would send bin/, obj/, and local secrets as build context"
  fi
  mkdir -p "$probe_dir/ctx/src/RouterKely/obj" "$probe_dir/ctx/.git" \
    "$probe_dir/ctx/config" "$probe_dir/ctx/scripts"
  cp "$ignore_file" "$probe_dir/Dockerfile.dockerignore"
  cat > "$probe_dir/Dockerfile" <<'DOCKERFILE'
FROM alpine:3.20
COPY . /ctx
RUN set -eu; \
    for leaked in src/RouterKely/obj/compiled.bin .git/index config/router-kely.local.json scripts/.tmp-run.log; do \
      if [ -e "/ctx/${leaked}" ]; then echo "leaked into context: ${leaked}" >&2; exit 1; fi; \
    done; \
    for kept in scripts/tests.sh config/router-kely.local.json.example; do \
      if [ ! -e "/ctx/${kept}" ]; then echo "wrongly excluded: ${kept}" >&2; exit 1; fi; \
    done
DOCKERFILE
  touch "$probe_dir/ctx/src/RouterKely/obj/compiled.bin" \
    "$probe_dir/ctx/.git/index" \
    "$probe_dir/ctx/config/router-kely.local.json" \
    "$probe_dir/ctx/config/router-kely.local.json.example" \
    "$probe_dir/ctx/scripts/.tmp-run.log" \
    "$probe_dir/ctx/scripts/tests.sh"
  docker build --quiet --file "$probe_dir/Dockerfile" "$probe_dir/ctx" >/dev/null ||
    fail "the ignore rules in Dockerfile.dockerignore are not applied correctly"
}

# Coolify needs the repository root as the build context, because the Dockerfile
# reads the sources, the configuration template, and the entrypoint scripts.
assert_compose_build_context() {
  echo "==> Asserting the compose file builds the relocated Dockerfile from the repo root"
  local resolved
  resolved="$(ROUTERKELY_ADMIN_API_KEY=probe ROUTERKELY_DEEPSEEK_API_KEY=probe \
    docker compose --file "$repo_root/scripts/container/compose.yml" config 2>/dev/null)" ||
    fail "the compose file is not valid"
  grep -qE "dockerfile: .*scripts/container/Dockerfile" <<< "$resolved" ||
    fail "the compose file does not resolve the Dockerfile path"
  grep -q "context: $repo_root" <<< "$resolved" ||
    fail "the compose file does not use the repository root as the build context"
}

assert_build_context_is_filtered
assert_compose_build_context

echo "==> Building $image"
docker build --file "$repo_root/scripts/container/Dockerfile" --tag "$image" "$repo_root"

echo "==> Starting container"
start_container

echo "==> Waiting for readiness"
for _ in $(seq 1 60); do
  curl --fail --silent "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1 && break
  if ! is_running "$container"; then
    docker logs "$container" >&2
    fail "container exited before becoming ready"
  fi
  sleep 1
done
curl --fail --silent "http://127.0.0.1:$port/health/ready" >/dev/null || fail "readiness endpoint never answered"

echo "==> Asserting the configuration file was seeded into the volume"
[[ -f "$config_path" ]] || fail "$config_path was not seeded"

echo "==> Asserting the router runs unprivileged on a read-only root filesystem"
# A bind-mounted directory owned by the host user is the case that regresses silently:
# the container starts as root only to claim /data, then re-executes as uid 1654.
[[ "$(process_uid "$container")" == "1654" ]] ||
  fail "the router process runs as uid $(process_uid "$container") instead of 1654"
docker exec "$container" sh -c 'touch /app/probe' 2>/dev/null &&
  fail "the container root filesystem is writable"
content="$(cat "$config_path")" || fail "the seeded configuration is unreadable on the host"
grep -q '"routerKely"' <<< "$content" || fail "the seeded configuration is not the expected template"
grep -q '"maxConcurrentRequests": 256' <<< "$content" || fail "the seeded configuration was modified"

echo "==> Asserting the image health check succeeds"
docker exec "$container" /usr/local/bin/healthcheck.sh || fail "the health check script failed"

echo "==> Asserting the environment key is accepted and the file placeholder is not"
curl --fail --silent --output /dev/null \
  --header "Authorization: Bearer $admin_key" \
  "http://127.0.0.1:$port/v1/models" || fail "environment administrator key was rejected"
status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  --header "Authorization: Bearer sk-rk-local-change-me" \
  "http://127.0.0.1:$port/v1/models")"
[[ "$status" == "401" ]] || fail "configuration placeholder key returned $status instead of 401"

echo "==> Asserting an edited configuration survives a restart"
docker rm --force "$container" >/dev/null
sed 's/"maxConcurrentRequests": 256/"maxConcurrentRequests": 128/' "$config_path" > "$config_path.edited"
mv "$config_path.edited" "$config_path"
start_container
wait_for_ready "$container" || {
  docker logs "$container" >&2
  fail "the container did not become ready after the configuration was edited"
}
grep -q '"maxConcurrentRequests": 128' "$config_path" ||
  fail "the container overwrote the operator's configuration file"

echo "==> Asserting startup fails without the required secrets"
docker rm --force "$container" "$probe" >/dev/null 2>&1 || true
docker run --detach --name "$probe" "$image" >/dev/null
wait_for_exit "$probe" || fail "container started without ROUTERKELY_ADMIN_API_KEY and ROUTERKELY_DEEPSEEK_API_KEY"
docker logs "$probe" >"$work_dir/missing-secrets.log" 2>&1 || true
grep -q "ROUTERKELY_ADMIN_API_KEY" "$work_dir/missing-secrets.log" ||
  fail "missing-secret failure did not name the missing variable"
docker rm --force "$probe" >/dev/null

echo "==> Asserting an edited configuration in the volume is validated on startup"
sed 's/"maxConcurrentRequests": 128/"maxConcurrentRequests": 0/' "$config_path" > "$config_path.edited"
mv "$config_path.edited" "$config_path"
start_container
wait_for_exit "$container" || fail "container kept running despite an invalid MaxConcurrentRequests"
docker logs "$container" >"$work_dir/invalid-config.log" 2>&1 || true
grep -q "MaxConcurrentRequests" "$work_dir/invalid-config.log" ||
  fail "the invalid configuration was rejected without naming MaxConcurrentRequests"

echo "==> Asserting a named volume works with no host-side preparation"
docker rm --force "$container" "$probe" "$named_container" >/dev/null 2>&1 || true
docker volume rm --force "$named_volume" >/dev/null 2>&1 || true
docker volume create "$named_volume" >/dev/null
container_env --detach --name "$named_container" \
  --publish "127.0.0.1:$port:8080" \
  --volume "$named_volume:/data" \
  --read-only \
  --security-opt no-new-privileges:true >/dev/null
wait_for_ready "$named_container" || {
  docker logs "$named_container" >&2
  fail "the container did not become ready with a named volume"
}
[[ "$(process_uid "$named_container")" == "1654" ]] ||
  fail "the router runs as uid $(process_uid "$named_container") instead of 1654 with a named volume"
# A named volume lives in the daemon's own filesystem, so ownership is authoritative on
# every platform, unlike a Docker Desktop bind mount.
[[ "$(docker exec "$named_container" stat -c '%u' /data/router-kely.local.json)" == "1654" ]] ||
  fail "the seeded configuration in the named volume is not owned by uid 1654"
curl --fail --silent --output /dev/null \
  --header "Authorization: Bearer $admin_key" \
  "http://127.0.0.1:$port/v1/models" || fail "the named volume deployment is not serving requests"
docker rm --force "$named_container" >/dev/null
docker volume rm --force "$named_volume" >/dev/null

# The case that broke on a native Linux daemon and works in production: a mounted
# directory owned by root. The entrypoint must claim it and then drop privileges, or the
# router cannot write the identity file the admin UI rewrites.
echo "==> Asserting a root-owned volume is claimed and handed to the runtime user"
prepare_root_owned_volume
container_env --detach --name "$named_container" \
  --publish "127.0.0.1:$port:8080" \
  --volume "$blocked_volume:/data" \
  --read-only \
  --security-opt no-new-privileges:true >/dev/null
wait_for_ready "$named_container" || {
  docker logs "$named_container" >&2
  fail "the container did not recover a root-owned volume"
}
[[ "$(process_uid "$named_container")" == "1654" ]] ||
  fail "the router runs as uid $(process_uid "$named_container") instead of 1654 after claiming the volume"
[[ "$(docker exec "$named_container" stat -c '%u' /data)" == "1654" ]] ||
  fail "the entrypoint did not take ownership of the root-owned volume"
# The seeded file proves the app itself can write, not just the entrypoint.
[[ "$(docker exec "$named_container" stat -c '%u' /data/router-kely.local.json)" == "1654" ]] ||
  fail "the seeded configuration is not owned by uid 1654 on a root-owned volume"
curl --fail --silent --output /dev/null \
  --header "Authorization: Bearer $admin_key" \
  "http://127.0.0.1:$port/v1/models" || fail "the root-owned volume deployment is not serving requests"
docker rm --force "$named_container" >/dev/null
docker volume rm --force "$blocked_volume" >/dev/null

# Uses a named volume rather than a bind mount so the permission check holds on every
# platform. --user skips the entrypoint's root step, which would otherwise claim the
# directory and hide the failure.
echo "==> Asserting an unwritable volume is reported clearly"
docker rm --force "$container" "$probe" "$named_container" >/dev/null 2>&1 || true
prepare_root_owned_volume
container_env --detach --name "$probe" --user 1654:1654 --volume "$blocked_volume:/data" >/dev/null
if wait_for_exit "$probe"; then
  docker logs "$probe" >"$work_dir/unwritable.log" 2>&1 || true
  grep -q "not writable" "$work_dir/unwritable.log" ||
    fail "an unwritable volume did not produce a clear diagnostic"
else
  fail "the container kept running with a volume it cannot write to"
fi
docker rm --force "$probe" >/dev/null
docker volume rm --force "$blocked_volume" >/dev/null

echo "All container tests passed."
