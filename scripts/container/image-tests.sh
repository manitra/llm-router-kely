#!/usr/bin/env bash
# End-to-end check of the container image: build, first-boot seeding of the persistent
# volume, environment-supplied secrets, and configuration validation. Requires Docker,
# and is not part of scripts/tests.sh because building the image runs a full Native AOT
# publish. The script sits next to the Dockerfile it builds, and its name distinguishes it
# from the repo-level scripts/tests.sh.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
container_dir="$repo_root/scripts/container"
work_dir="$(mktemp -d "$repo_root/scripts/.tmp-container-test.XXXXXX")"
readonly image="llm-router-kely:container-test"
readonly probe_image="llm-router-kely:ignore-probe"
readonly container="routerkely-container-test"
# One-shot runs that are expected to fail at startup use a separate name so they
# never collide with the long-running container.
readonly probe="routerkely-container-test-probe"
# The configuration and identity files live in a Docker volume, which is what an
# orchestrator such as Coolify provides by default and what lets the unprivileged
# container write without the host having to prepare anything.
readonly data_volume="routerkely-container-test-data"
readonly blocked_volume="routerkely-container-test-blocked"
readonly port="${ROUTERKELY_TEST_PORT:-18080}"
readonly admin_key="sk-rk-container-test-admin"
readonly config_path="/data/router-kely.local.json"

cleanup() {
  docker rm --force "$container" "$probe" >/dev/null 2>&1 || true
  docker volume rm --force "$data_volume" "$blocked_volume" >/dev/null 2>&1 || true
  docker rmi --force "$probe_image" >/dev/null 2>&1 || true
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

fail() {
  echo "container test failed: $*" >&2
  exit 1
}

echo "==> Environment"
docker version --format '    docker client {{.Client.Version}} / server {{.Server.Version}}' 2>/dev/null ||
  echo "    docker: unavailable"
docker compose version 2>/dev/null | sed 's/^/    /' ||
  echo "    docker compose: unavailable"
df -h / | sed 's/^/    /'

container_env() {
  docker run "$@" \
    --env ROUTERKELY_ADMIN_API_KEY="$admin_key" \
    --env ROUTERKELY_DEEPSEEK_API_KEY=sk-container-test-upstream \
    "$image"
}

start_container() {
  container_env --detach --name "$container" \
    --publish "127.0.0.1:$port:8080" \
    --volume "$data_volume:/data" \
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

# The router is PID 1, so its uid is the uid that serves traffic.
process_uid() {
  docker exec "$1" sh -c "grep '^Uid:' /proc/1/status | awk '{print \$2}'"
}

# The persisted files are read and edited through a throwaway container, which is how an
# operator would edit them on the host, and which keeps these checks independent of the
# router container's state.
read_config() {
  docker run --rm --volume "$data_volume:/data" --entrypoint cat "$image" "$config_path"
}

edit_config() {
  docker run --rm --volume "$data_volume:/data" --entrypoint sed "$image" -i "$1" "$config_path"
}

volume_has_config() {
  docker run --rm --volume "$data_volume:/data" --entrypoint sh "$image" \
    -c "test -f '$config_path'"
}

# Reproduces a volume the router cannot write to, which is also what a host bind mount
# not chowned to 1654 looks like. The marker file is essential: Docker re-initializes an
# *empty* named volume from the image, which would restore the directory to the runtime
# user and hide the behaviour under test.
prepare_unusable_volume() {
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
  local ignore_file="$container_dir/Dockerfile.dockerignore"
  local probe_dir="$work_dir/ignore-probe"
  if [[ ! -f "$ignore_file" ]]; then
    fail "$ignore_file is missing, so BuildKit would send bin/, obj/, and local secrets as build context"
  fi
  # Reuse the base the real build pulls anyway. Pulling a separate image from Docker Hub
  # would make this check, and therefore CI, depend on a rate-limited public registry.
  local probe_base
  probe_base="$(sed -n 's/^FROM \(.*\) AS runtime$/\1/p' "$container_dir/Dockerfile" | head -1)"
  [[ -n "$probe_base" ]] || fail "could not read the runtime base image from the Dockerfile"
  mkdir -p "$probe_dir/ctx/src/RouterKely/obj" "$probe_dir/ctx/.git" \
    "$probe_dir/ctx/config" "$probe_dir/ctx/scripts"
  cp "$ignore_file" "$probe_dir/Dockerfile.dockerignore"
  cat > "$probe_dir/Dockerfile" <<DOCKERFILE
FROM ${probe_base}
COPY . /ctx
RUN set -eu; \\
    for leaked in src/RouterKely/obj/compiled.bin .git/index config/router-kely.local.json scripts/.tmp-run.log; do \\
      if [ -e "/ctx/\${leaked}" ]; then echo "leaked into context: \${leaked}" >&2; exit 1; fi; \\
    done; \\
    for kept in scripts/tests.sh config/router-kely.local.json.example; do \\
      if [ ! -e "/ctx/\${kept}" ]; then echo "wrongly excluded: \${kept}" >&2; exit 1; fi; \\
    done
DOCKERFILE
  touch "$probe_dir/ctx/src/RouterKely/obj/compiled.bin" \
    "$probe_dir/ctx/.git/index" \
    "$probe_dir/ctx/config/router-kely.local.json" \
    "$probe_dir/ctx/config/router-kely.local.json.example" \
    "$probe_dir/ctx/scripts/.tmp-run.log" \
    "$probe_dir/ctx/scripts/tests.sh"
  if ! docker build --file "$probe_dir/Dockerfile" --tag "$probe_image" "$probe_dir/ctx" \
      >"$work_dir/ignore-probe.log" 2>&1; then
    cat "$work_dir/ignore-probe.log" >&2
    fail "the ignore rules in Dockerfile.dockerignore are not applied correctly"
  fi
}

# Coolify is the only Compose consumer, and it builds the Dockerfile from the repository
# root so any branch can be deployed. The file must not hard-code a GHCR image, and it
# must declare both variable names for Coolify's env form and the /data volume. A
# ${VAR:?} guard here would abort the deploy at interpolation time, before Coolify
# injects its stored variables, so that is rejected too.
assert_coolify_compose() {
  echo "==> Asserting the Coolify compose file builds from the repository root"
  if ! docker compose version >/dev/null 2>&1; then
    echo "    note: docker compose is unavailable; Coolify compose check skipped"
    return 0
  fi
  local file="$container_dir/coolify.compose.yml"
  local resolved
  if ! resolved="$(docker compose --file "$file" config 2>"$work_dir/coolify.stderr")"; then
    cat "$work_dir/coolify.stderr" >&2
    fail "the Coolify compose file is not valid"
  fi
  grep -qE "dockerfile: .*scripts/container/Dockerfile" <<< "$resolved" ||
    fail "the Coolify compose file does not resolve the Dockerfile path"
  grep -q "context: $repo_root" <<< "$resolved" ||
    fail "the Coolify compose file does not use the repository root as the build context"
  grep -qE "image: ghcr.io/" <<< "$resolved" &&
    fail "the Coolify compose file pulls a published image; it must build from source"
  grep -q "ROUTERKELY_ADMIN_API_KEY" <<< "$resolved" ||
    fail "the Coolify compose file does not declare ROUTERKELY_ADMIN_API_KEY"
  grep -q "ROUTERKELY_DEEPSEEK_API_KEY" <<< "$resolved" ||
    fail "the Coolify compose file does not declare ROUTERKELY_DEEPSEEK_API_KEY"
  grep -qE "target: /data" <<< "$resolved" ||
    fail "the Coolify compose file does not persist /data"
  # Only inspect directives: the file's comment explains the guard, so grep would
  # otherwise match the explanation rather than a real interpolation.
  grep -v '^[[:space:]]*#' "$file" | grep -q ':?' &&
    fail "the Coolify compose file uses a \${VAR:?} guard that breaks Coolify interpolation"
  return 0
}

assert_build_context_is_filtered
assert_coolify_compose

echo "==> Building $image"
docker build --file "$container_dir/Dockerfile" --tag "$image" "$repo_root"

# The router must never run as root, and the image must not start as root either, because
# a root entrypoint that claims the mounted volume would take the host directory away
# from its owner.
image_user="$(docker inspect --format '{{.Config.User}}' "$image")"
[[ "$image_user" == "1654:1654" ]] || fail "the image declares user '$image_user' instead of 1654:1654"

echo "==> Starting the container on a fresh volume"
docker volume rm --force "$data_volume" >/dev/null 2>&1 || true
docker volume create "$data_volume" >/dev/null
start_container
wait_for_ready "$container" || {
  docker logs "$container" >&2
  fail "the container did not become ready"
}

echo "==> Asserting the configuration file was created in the volume on first start"
volume_has_config || fail "$config_path was not created"
content="$(read_config)" || fail "$config_path is unreadable"
grep -q '"routerKely"' <<< "$content" || fail "the created configuration is not the expected default"
grep -q '"maxConcurrentRequests": 256' <<< "$content" || fail "the created configuration was modified"
grep -q '${ROUTERKELY_ADMIN_API_KEY}' <<< "$content" ||
  fail "the created configuration does not reference ROUTERKELY_ADMIN_API_KEY"
grep -q '${ROUTERKELY_DEEPSEEK_API_KEY}' <<< "$content" ||
  fail "the created configuration does not reference ROUTERKELY_DEEPSEEK_API_KEY"
if grep -q "$admin_key" <<< "$content"; then
  fail "the created configuration contains the administrator key in plaintext"
fi

echo "==> Asserting the router is unprivileged on a read-only root filesystem"
[[ "$(process_uid "$container")" == "1654" ]] ||
  fail "the router process runs as uid $(process_uid "$container") instead of 1654"
docker exec "$container" sh -c 'touch /app/probe' 2>/dev/null &&
  fail "the container root filesystem is writable"

echo "==> Asserting the image health check succeeds"
docker exec "$container" /usr/local/bin/healthcheck.sh || fail "the health check script failed"

echo "==> Asserting the environment key is accepted and an unknown key is rejected"
curl --fail --silent --output /dev/null \
  --header "Authorization: Bearer $admin_key" \
  "http://127.0.0.1:$port/v1/models" || fail "environment administrator key was rejected"
status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  --header "Authorization: Bearer sk-rk-not-a-configured-key" \
  "http://127.0.0.1:$port/v1/models")"
[[ "$status" == "401" ]] || fail "an unconfigured key returned $status instead of 401"

echo "==> Asserting an edited configuration survives a restart"
edit_config 's/"maxConcurrentRequests": 256/"maxConcurrentRequests": 128/'
docker rm --force "$container" >/dev/null 2>&1 || true
start_container
wait_for_ready "$container" || {
  docker logs "$container" >&2
  fail "the container did not become ready after the configuration was edited"
}
grep -q '"maxConcurrentRequests": 128' <<< "$(read_config)" ||
  fail "the container overwrote the operator's configuration file"

echo "==> Asserting startup fails with one exhaustive report of the missing secrets"
docker rm --force "$probe" >/dev/null 2>&1 || true
docker run --detach --name "$probe" "$image" >/dev/null
wait_for_exit "$probe" || fail "container started without ROUTERKELY_ADMIN_API_KEY and ROUTERKELY_DEEPSEEK_API_KEY"
docker logs "$probe" >"$work_dir/missing-secrets.log" 2>&1 || true
grep -q "ROUTERKELY_ADMIN_API_KEY" "$work_dir/missing-secrets.log" ||
  fail "missing-secret failure did not name ROUTERKELY_ADMIN_API_KEY"
grep -q "ROUTERKELY_DEEPSEEK_API_KEY" "$work_dir/missing-secrets.log" ||
  fail "missing-secret failure did not name ROUTERKELY_DEEPSEEK_API_KEY in the same report"
docker rm --force "$probe" >/dev/null

echo "==> Asserting an edited configuration in the volume is validated on startup"
edit_config 's/"maxConcurrentRequests": 128/"maxConcurrentRequests": 0/'
docker rm --force "$container" >/dev/null 2>&1 || true
start_container
wait_for_exit "$container" || fail "container kept running despite an invalid MaxConcurrentRequests"
docker logs "$container" >"$work_dir/invalid-config.log" 2>&1 || true
grep -q "MaxConcurrentRequests" "$work_dir/invalid-config.log" ||
  fail "the invalid configuration was rejected without naming MaxConcurrentRequests"
docker rm --force "$container" >/dev/null 2>&1 || true

# A Docker volume is what an orchestrator provides by default and is writable by the
# runtime user because it inherits the image's /data ownership, so no host preparation is
# needed. A host bind mount is not: the container never runs as root and must not take
# the directory over, so the check below covers both the unrecoverable case and the
# diagnostic that tells the operator to chown the directory.
echo "==> Asserting a volume the router cannot write to is reported clearly"
prepare_unusable_volume
container_env --detach --name "$probe" --volume "$blocked_volume:/data" >/dev/null
if wait_for_exit "$probe"; then
  docker logs "$probe" >"$work_dir/unusable.log" 2>&1 || true
  grep -q "not writable" "$work_dir/unusable.log" ||
    fail "an unusable volume did not produce a clear diagnostic"
  grep -q "chown the host directory" "$work_dir/unusable.log" ||
    fail "the diagnostic did not explain the remedy"
else
  fail "the container kept running with a volume it cannot write to"
fi
docker rm --force "$probe" >/dev/null
docker volume rm --force "$blocked_volume" >/dev/null

echo "All container tests passed."
