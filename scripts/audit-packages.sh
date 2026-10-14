#!/usr/bin/env bash
# Fails when any project in the solution resolves a NuGet package with a known vulnerability, direct or
# transitive. GitHub's dependency graph sees only the direct references of a project without a lock
# file, so a vulnerable package pulled in by a dependency raises no alert there; this check does.
# Usage: scripts/audit-packages.sh   (from any directory; restores first)
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet restore Sendway.slnx --verbosity quiet >/dev/null
report=$(dotnet list Sendway.slnx package --vulnerable --include-transitive 2>&1)

if grep -q "has the following vulnerable packages" <<<"$report"; then
  echo "$report"
  echo "::error::A resolved package has a known vulnerability — see the report above."
  exit 1
fi

if ! grep -q "has no vulnerable packages" <<<"$report"; then
  # The listing itself failed (no advisory data, restore problem): say so rather than pass silently.
  echo "$report"
  echo "::error::The vulnerability listing did not complete."
  exit 1
fi

echo "No known vulnerable packages."
