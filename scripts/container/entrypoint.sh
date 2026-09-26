#!/bin/sh
# Seeds the persisted configuration on first boot, then starts the router.
# Runs as the unprivileged "app" user, so the volume must be writable by it: a fresh
# named volume inherits the ownership baked into the image, while a bind mount has to
# be chowned to 1654:1654 by the operator.
set -eu

config_path="${ROUTERKELY_CONFIG:-/data/router-kely.local.json}"
config_dir="$(dirname "$config_path")"
template_path="${ROUTERKELY_CONFIG_TEMPLATE:-/app/config/router-kely.local.json.example}"

# The seeded configuration carries placeholder secrets, so refuse to start unless the
# real ones arrive through the environment.
: "${ROUTERKELY_ADMIN_API_KEY:?ROUTERKELY_ADMIN_API_KEY must be set to the administrator API key}"
: "${ROUTERKELY_DEEPSEEK_API_KEY:?ROUTERKELY_DEEPSEEK_API_KEY must be set to the upstream API key}"

# Report the cause instead of a bare mkdir/cp error when the volume is not writable.
abort_unwritable() {
  echo "router-kely: ${1} is not writable by uid $(id -u); chown the mounted volume to 1654:1654." >&2
  exit 1
}

mkdir -p "$config_dir" 2>/dev/null || abort_unwritable "$config_dir"

if [ ! -f "$config_path" ]; then
  cp "$template_path" "$config_path" 2>/dev/null || abort_unwritable "$config_dir"
  chmod 0600 "$config_path" 2>/dev/null || abort_unwritable "$config_path"
  echo "router-kely: created ${config_path} from the image default." >&2
  echo "router-kely: edit it to change models, prices, quotas, or identity limits, then restart the container." >&2
fi

exec "$@"
