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
readonly port="${ROUTERKELY_TEST_PORT:-18080}"
readonly admin_key="sk-rk-container-test-admin"
readonly config_path="$work_dir/router-kely.local.json"

cleanup() {
  docker rm --force "$container" "$probe" >/dev/null 2>&1 || true
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

echo "==> Asserting the container runs unprivileged with a read-only root filesystem"
[[ "$(docker inspect --format '{{.Config.User}}' "$image")" == "1654:1654" ]] ||
  fail "the image does not declare a non-root user"
[[ "$(docker exec "$container" id -u)" == "1654" ]] || fail "the router process is not running as uid 1654"
docker exec "$container" sh -c 'touch /app/probe' 2>/dev/null &&
  fail "the container root filesystem is writable"

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
for _ in $(seq 1 60); do
  curl --fail --silent "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1 && break
  sleep 1
done
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

echo "==> Asserting a mis-owned volume is reported clearly"
misowned="$work_dir/misowned"
mkdir -p "$misowned"
chmod 0500 "$misowned"
docker rm --force "$container" "$probe" >/dev/null 2>&1 || true
container_env --detach --name "$probe" --volume "$misowned:/data" >/dev/null
if wait_for_exit "$probe"; then
  docker logs "$probe" >"$work_dir/misowned.log" 2>&1 || true
  grep -q "not writable" "$work_dir/misowned.log" ||
    fail "unwritable volume did not produce a clear diagnostic"
else
  # Desktop Docker backends (macOS, Windows) present bind mounts as writable
  # regardless of the host mode, so the diagnostic cannot be triggered there.
  echo "    note: this Docker backend does not enforce host directory permissions; check skipped"
fi

echo "All container tests passed."
