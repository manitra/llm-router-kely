#!/usr/bin/env bash
# Renders the CI job summary from a scripts/tests.sh log and, when requested,
# writes the Shields-compatible performance badge JSON.
#
# The summary always shows what the log contains, so it stays useful on failed
# runs; the badge JSON is only written when every required metric was parsed.
set -euo pipefail

usage() {
  echo "usage: $0 <tests-log> [--badges <output-dir>]" >&2
  exit 2
}

[[ $# -ge 1 ]] || usage
log="$1"
shift
badge_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --badges)
      [[ $# -ge 2 ]] || usage
      badge_dir="$2"
      shift 2
      ;;
    *)
      usage
      ;;
  esac
done
[[ -f "$log" ]] || { echo "missing test log: $log" >&2; exit 1; }

trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

# Last value matched in the test log by the given sed expression.
extract() {
  sed -nE "$1" "$log" | tail -1 || true
}

value_or_dash() { [[ -n "$1" ]] && printf '%s' "$1" || printf -- '—'; }

# "0.5 0.6 0.7" -> "0.5 | 0.6 | 0.7"; empty -> three dashes for a 3-column row.
cells() {
  local value
  value="$(trim "${1:-}")"
  [[ -n "$value" ]] && printf '%s' "${value// / | }" || printf -- '— | — | —'
}

icon() {
  case "${1:-}" in
    PASS) printf '✅' ;;
    MISS) printf '❌' ;;
    *) printf '❔' ;;
  esac
}

runtime="$(extract 's/.*runtime:[[:space:]]+(.*)/\1/p')"
scenario="$(extract 's/.*scenario:[[:space:]]+(.*)/\1/p')"
read -r direct_p50 direct_p95 direct_p99 <<<"$(extract 's/.*direct:[[:space:]]+p50 ([0-9.]+) ms \| p95 ([0-9.]+) ms \| p99 ([0-9.]+) ms.*/\1 \2 \3/p')" || true
read -r router_p50 router_p95 router_p99 <<<"$(extract 's/.*router:[[:space:]]+p50 ([0-9.]+) ms \| p95 ([0-9.]+) ms \| p99 ([0-9.]+) ms.*/\1 \2 \3/p')" || true
read -r overhead_p50 overhead_p95 overhead_p99 <<<"$(extract 's/.*overhead:[[:space:]]+p50 ([0-9.]+) ms \| p95 ([0-9.]+) ms \| p99 ([0-9.]+) ms.*/\1 \2 \3/p')" || true
throughput="$(extract 's/.*throughput:[[:space:]]+([0-9,]+) .*/\1/p')"
read -r alloc_value alloc_limit alloc_result <<<"$(extract 's/.*allocation:[[:space:]]+([0-9,]+) B\/routed request \| limit <= ([0-9,]+) B => (PASS|MISS).*/\1 \2 \3/p')" || true
read -r memory_value memory_limit memory_result <<<"$(extract 's/.*memory:[[:space:]]+([0-9.]+) MiB idle working set after load \| limit < ([0-9.]+) MiB => (PASS|MISS).*/\1 \2 \3/p')" || true
read -r binary_value binary_limit binary_result <<<"$(extract 's/.*binary:[[:space:]]+([0-9.]+) MiB \| limit <= ([0-9.]+) MiB => (PASS|MISS).*/\1 \2 \3/p')" || true
read -r files_value files_limit files_result <<<"$(extract 's/.*files:[[:space:]]+([0-9,]+) published \| required ([0-9]+) => (PASS|MISS).*/\1 \2 \3/p')" || true
constraint_result="$(extract 's/.*constraint:.*=> (PASS|MISS).*/\1/p')"
admin_result="$(extract 's/.*admin UI:[[:space:]]+(PASS).*/\1/p')"
concurrency_result="$(extract 's/.*concurrency:[[:space:]]+(PASS).*/\1/p')"

unit_line="$(grep -E '^(Passed|Failed)! *-' "$log" | tail -1 || true)"
unit_parsed=0
unit_status=""
unit_failed=""
unit_passed=""
unit_skipped=""
unit_total=""
unit_duration=""
if [[ -n "$unit_line" ]]; then
  unit_parsed=1
  if [[ "$unit_line" == Failed* ]]; then unit_status="failed"; else unit_status="passed"; fi
  rest="${unit_line#*Failed:}";  unit_failed="$(trim "${rest%%,*}")"
  rest="${rest#*Passed:}";       unit_passed="$(trim "${rest%%,*}")"
  rest="${rest#*Skipped:}";      unit_skipped="$(trim "${rest%%,*}")"
  rest="${rest#*Total:}";        unit_total="$(trim "${rest%%,*}")"
  duration_raw="${rest#*Duration:}"
  unit_duration=""
  [[ "$duration_raw" != "$rest" ]] && unit_duration="$(trim "${duration_raw%% - *}")"
fi

unit_failures=()
while IFS= read -r name; do
  [[ -n "$name" ]] && unit_failures+=("$name")
done < <(grep -E '^[[:space:]]+Failed ' "$log" | sed -E 's/^[[:space:]]+Failed ([^ ]+).*/\1/' | sort -u || true)

badge_error=0
if [[ -n "$badge_dir" && ( -z "$unit_total" || -z "$overhead_p50" || -z "$alloc_value" || -z "$binary_value" ) ]]; then
  echo "Could not extract every badge metric from $log" >&2
  badge_error=1
fi

summary="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

{
  printf '## Test results\n\n'
  printf '### Unit tests\n\n'
  if [[ "$unit_parsed" -eq 1 ]]; then
    if [[ "$unit_status" == "passed" ]]; then
      printf '✅ **%s passed**, %s failed, %s skipped · total %s' \
        "$unit_passed" "$unit_failed" "$unit_skipped" "$unit_total"
    else
      printf '❌ **%s failed**, %s passed, %s skipped · total %s' \
        "$unit_failed" "$unit_passed" "$unit_skipped" "$unit_total"
    fi
    [[ -n "$unit_duration" ]] && printf ' · %s' "$unit_duration"
    printf '\n'
    if [[ "${#unit_failures[@]}" -gt 0 ]]; then
      printf '\n<details><summary>Failing tests</summary>\n\n'
      for name in "${unit_failures[@]}"; do printf -- '- `%s`\n' "$name"; done
      printf '\n</details>\n'
    fi
  else
    printf '❔ No unit test result found in the log (the build or test run likely failed before reporting).\n'
  fi

  printf '\n### Performance\n\n'
  printf -- '- **Runtime:** `%s`\n' "$(value_or_dash "$runtime")"
  printf -- '- **Scenario:** `%s`\n\n' "$(value_or_dash "$scenario")"

  printf '| Latency | p50 (ms) | p95 (ms) | p99 (ms) |\n'
  printf '|---|---:|---:|---:|\n'
  printf '| Direct upstream | %s |\n' "$(cells "$direct_p50 $direct_p95 $direct_p99")"
  printf '| Routed through Kely | %s |\n' "$(cells "$router_p50 $router_p95 $router_p99")"
  printf '| Incremental overhead | %s |\n\n' "$(cells "$overhead_p50 $overhead_p95 $overhead_p99")"

  printf '| Check | Measured | Budget | Result |\n'
  printf '|---|---:|---:|:--:|\n'
  printf '| Throughput | %s req/s | — | ℹ️ |\n' "$(value_or_dash "$throughput")"
  printf '| Allocation | %s B/request | ≤ %s B | %s |\n' \
    "$(value_or_dash "$alloc_value")" "$(value_or_dash "$alloc_limit")" "$(icon "$alloc_result")"
  printf '| Idle working set | %s MiB | < %s MiB | %s |\n' \
    "$(value_or_dash "$memory_value")" "$(value_or_dash "$memory_limit")" "$(icon "$memory_result")"
  printf '| Binary size | %s MiB | ≤ %s MiB | %s |\n' \
    "$(value_or_dash "$binary_value")" "$(value_or_dash "$binary_limit")" "$(icon "$binary_result")"
  printf '| Published files | %s | exactly %s | %s |\n' \
    "$(value_or_dash "$files_value")" "$(value_or_dash "$files_limit")" "$(icon "$files_result")"
  printf '| p50 & p99 overhead | p50 %s / p99 %s ms | < 0.250 / < 1.000 ms | %s |\n' \
    "$(value_or_dash "$overhead_p50")" "$(value_or_dash "$overhead_p99")" "$(icon "$constraint_result")"
  printf '| Admin UI smoke | %s | — | %s |\n' "$(value_or_dash "$admin_result")" "$(icon "$admin_result")"
  printf '| Concurrency smoke | %s | — | %s |\n' "$(value_or_dash "$concurrency_result")" "$(icon "$concurrency_result")"
} >>"$summary"

if [[ -n "$badge_dir" ]]; then
  if [[ "$badge_error" -ne 0 ]]; then
    exit 1
  fi

  mkdir -p "$badge_dir"
  alloc_numeric="${alloc_value//,/}"
  p50_color="$(awk -v x="$overhead_p50" 'BEGIN { print (x < 0.25 ? "brightgreen" : x < 0.5 ? "yellow" : "red") }')"
  allocation_color="$(awk -v x="$alloc_numeric" 'BEGIN { print (x <= 8192 ? "brightgreen" : x <= 10240 ? "yellow" : "red") }')"
  binary_color="$(awk -v x="$binary_value" 'BEGIN { print (x <= 20 ? "brightgreen" : x <= 22 ? "yellow" : "red") }')"
  tests_color="red"
  [[ "$unit_status" == "passed" ]] && tests_color="brightgreen"

  printf '{"schemaVersion":1,"label":"p50 overhead","message":"%s ms","color":"%s"}\n' "$overhead_p50" "$p50_color" >"$badge_dir/p50.json"
  printf '{"schemaVersion":1,"label":"alloc / request","message":"%s B","color":"%s"}\n' "$alloc_numeric" "$allocation_color" >"$badge_dir/allocation.json"
  printf '{"schemaVersion":1,"label":"binary size","message":"%s MiB","color":"%s"}\n' "$binary_value" "$binary_color" >"$badge_dir/binary.json"
  printf '{"schemaVersion":1,"label":"unit tests","message":"%s","color":"%s"}\n' "$unit_total" "$tests_color" >"$badge_dir/tests.json"
fi
