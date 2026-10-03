#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="$(mktemp -d "$repo_root/scripts/.tmp-aot-publish.XXXXXX")"

cleanup() {
  rm -rf -- "$publish_dir"
}
trap cleanup EXIT

dotnet build "$repo_root/RouterKely.slnx" --configuration Release
dotnet test "$repo_root/tests/RouterKely.Unit/RouterKely.Unit.csproj" \
  --configuration Release
dotnet publish "$repo_root/src/RouterKely/RouterKely.csproj" \
  --configuration Release \
  --output "$publish_dir" \
  --nologo

# No --no-build: the harness must be compiled from the current sources, or a stale binary could
# hide a change to the smoke assertions it carries.
dotnet run \
  --project "$repo_root/tests/RouterKely.Performance/RouterKely.Performance.csproj" \
  --configuration Release \
  -- "$repo_root" "$publish_dir"
