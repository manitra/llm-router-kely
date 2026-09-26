#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet build "$repo_root/RouterKely.slnx" --configuration Release
dotnet test "$repo_root/tests/RouterKely.Unit/RouterKely.Unit.csproj" \
  --configuration Release \
  --no-build

dotnet run \
  --project "$repo_root/tests/RouterKely.Performance/RouterKely.Performance.csproj" \
  --configuration Release \
  --no-build \
  -- "$repo_root"
