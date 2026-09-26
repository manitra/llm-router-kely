#!/bin/sh
# Verifies the persisted /data volume, seeds the configuration on first boot, then starts
# the router.
#
# The container runs as uid 1654 and never as root, so it cannot take ownership of a
# mount and must not modify the host directory either. The volume therefore has to be
# writable by that user already:
#   - a Docker volume inherits the ownership of the image's /data directory;
#   - a host bind mount must be chowned to 1654:1654 by the operator.
set -eu

config_path="${ROUTERKELY_CONFIG:-/data/router-kely.local.json}"
config_dir="$(dirname "$config_path")"
template_path="${ROUTERKELY_CONFIG_TEMPLATE:-/app/config/router-kely.local.json.example}"

# The seeded configuration carries placeholder secrets, so refuse to start unless the
# real ones arrive through the environment.
: "${ROUTERKELY_ADMIN_API_KEY:?ROUTERKELY_ADMIN_API_KEY must be set to the administrator API key}"
: "${ROUTERKELY_DEEPSEEK_API_KEY:?ROUTERKELY_DEEPSEEK_API_KEY must be set to the upstream API key}"

# Report the remedy instead of a bare mkdir/cp error when the volume is unusable.
abort_unwritable() {
  echo "router-kely: ${1} is not writable by uid $(id -u)." >&2
  echo "router-kely: mount a Docker volume, or chown the host directory to 1654:1654." >&2
  exit 1
}

mkdir -p "$config_dir" 2>/dev/null || abort_unwritable "$config_dir"
[ -w "$config_dir" ] || abort_unwritable "$config_dir"

if [ ! -f "$config_path" ]; then
  cp "$template_path" "$config_path" 2>/dev/null || abort_unwritable "$config_dir"
  chmod 0600 "$config_path" 2>/dev/null || abort_unwritable "$config_path"
  echo "router-kely: created ${config_path} from the image default." >&2
  echo "router-kely: edit it to change models, prices, quotas, or identity limits, then restart the container." >&2
fi

exec "$@"
