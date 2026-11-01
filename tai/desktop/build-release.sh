#!/usr/bin/env bash
# Run with bash from Windows (the installed .NET SDK and PowerShell 7 are required).
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
mode="${1:---online}"
case "$mode" in
  --online|--offline) ;;
  *) echo 'Usage: bash build-release.sh [--online|--offline]' >&2; exit 2 ;;
esac
stamp="$(date -u +%Y%m%dT%H%M%S)-$$"
output="artifacts/release-0.10.0-$stamp"
evidence="artifacts/evidence-0.10.0-$stamp"
# Every invocation creates a fresh output tree; prior releases and bin/Release stay intact.
mkdir -p -- "$output" "$evidence"
restore_args=(restore TAI.Desktop.csproj --packages .packages -p:NuGetAudit=false --disable-parallel)
if [[ "$mode" == '--offline' ]]; then
  # Restrict feeds to this desktop-scoped cache. Missing packages fail without network.
  restore_args+=(--source .packages)
fi
dotnet "${restore_args[@]}" > "$evidence/restore.log" 2>&1 || { cat "$evidence/restore.log"; exit 1; }
dotnet build TAI.Desktop.csproj --no-restore -t:Rebuild -c Release \
  "-p:OutputPath=$output/" -p:NuGetAudit=false \
  "-bl:$evidence/build.binlog" -v:normal > "$evidence/build.log" 2>&1 || { cat "$evidence/build.log"; exit 1; }
dotnet msbuild TAI.Desktop.csproj -p:Configuration=Release -getItem:Compile > "$evidence/compile-items.json"
pwsh -NoProfile -File ./verify-release.ps1 -DesktopDir "$output" -EvidenceDir "$evidence" > "$evidence/verification-console.log" 2>&1 || { cat "$evidence/verification-console.log"; exit 1; }
printf 'PASS: DesktopDir=%s\nEvidenceDir=%s\nMode=%s\n' "$output" "$evidence" "$mode"
