#!/bin/sh
# Verifies the persisted /data volume, then starts the router.
#
# The container runs as uid 1654 and never as root, so it cannot take ownership of a
# mount and must not modify the host directory either. The volume therefore has to be
# writable by that user already:
#   - a Docker volume inherits the ownership of the image's /data directory;
#   - a host bind mount must be chowned to 1654:1654 by the operator.
#
# The router itself creates the configuration file from its built-in default when the
# file is absent, and reports every environment variable the configuration references
# but that is not set. Both behaviours live in the executable so they are identical for
# container and local runs; this script only checks the mount before doing that work.
set -eu

config_path="${ROUTERKELY_CONFIG:-/data/router-kely.local.json}"
config_dir="$(dirname "$config_path")"

abort_unwritable() {
  echo "router-kely: ${1} is not writable by uid $(id -u)." >&2
  echo "router-kely: mount a Docker volume, or chown the host directory to 1654:1654." >&2
  exit 1
}

mkdir -p "$config_dir" 2>/dev/null || abort_unwritable "$config_dir"
[ -w "$config_dir" ] || abort_unwritable "$config_dir"

exec "$@"
