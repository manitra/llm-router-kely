#!/bin/sh
# Prepares the persisted /data volume, seeds the configuration on first boot, then
# starts the router as the unprivileged "app" user.
#
# A bind mount keeps the host directory's ownership, which on Linux is usually root and
# therefore not writable by the runtime user. When the container starts as root this
# script claims the directory and re-executes itself as uid 1654, so the router and every
# file it writes belong to the unprivileged user. Named volumes inherit the ownership
# baked into the image, and platforms that force a non-root user skip the claiming step.
set -eu

app_uid=1654
app_gid=1654
config_path="${ROUTERKELY_CONFIG:-/data/router-kely.local.json}"
config_dir="$(dirname "$config_path")"
template_path="${ROUTERKELY_CONFIG_TEMPLATE:-/app/config/router-kely.local.json.example}"

# The seeded configuration carries placeholder secrets, so refuse to start unless the
# real ones arrive through the environment.
: "${ROUTERKELY_ADMIN_API_KEY:?ROUTERKELY_ADMIN_API_KEY must be set to the administrator API key}"
: "${ROUTERKELY_DEEPSEEK_API_KEY:?ROUTERKELY_DEEPSEEK_API_KEY must be set to the upstream API key}"

# Report the cause instead of a bare mkdir/cp error when the volume is not writable.
abort_unwritable() {
  echo "router-kely: ${1} is not writable by uid $(id -u); chown the mounted volume to ${app_uid}:${app_gid}." >&2
  exit 1
}

if [ "$(id -u)" = "0" ]; then
  mkdir -p "$config_dir" 2>/dev/null || abort_unwritable "$config_dir"
  chown "$app_uid:$app_gid" "$config_dir" 2>/dev/null || abort_unwritable "$config_dir"
  if [ -f "$config_path" ]; then
    chown "$app_uid:$app_gid" "$config_path" 2>/dev/null || abort_unwritable "$config_path"
  fi
  exec su-exec "$app_uid:$app_gid" "$0" "$@"
fi

mkdir -p "$config_dir" 2>/dev/null || abort_unwritable "$config_dir"

if [ ! -f "$config_path" ]; then
  cp "$template_path" "$config_path" 2>/dev/null || abort_unwritable "$config_dir"
  chmod 0600 "$config_path" 2>/dev/null || abort_unwritable "$config_path"
  echo "router-kely: created ${config_path} from the image default." >&2
  echo "router-kely: edit it to change models, prices, quotas, or identity limits, then restart the container." >&2
fi

exec "$@"
