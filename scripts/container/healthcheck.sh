#!/bin/sh
# Readiness probe for the container health check. The runtime image ships busybox
# wget but no curl, and it fails on any non-success status.
set -eu

wget -q -T 5 -O - "${ROUTERKELY_HEALTH_URL:-http://127.0.0.1:8080/health/ready}" |
  grep -q '"status":"ready"'
