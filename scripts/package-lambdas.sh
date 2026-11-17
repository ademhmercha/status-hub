#!/usr/bin/env bash
# Builds the five Lambda zips ONCE; the same files are deployed to dev and then prod.
#
#   scripts/package-lambdas.sh                  full build (Checker as Native AOT inside an Amazon Linux 2023 container)
#   scripts/package-lambdas.sh --placeholder    PR plans: Checker is a tiny placeholder (no Docker, ~10x faster)
#
# Output: build/lambda/{api,scheduler,evaluator,notifier,checker}.zip and build/lambda/manifest.json
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/build/lambda"
WORK="$ROOT/build/work"
PLACEHOLDER=false
[[ "${1:-}" == "--placeholder" ]] && PLACEHOLDER=true

rm -rf "$OUT" "$WORK"
mkdir -p "$OUT" "$WORK"

zip_dir() { # zip_dir <dir> <zip>
  (cd "$1" && zip -q -r -X "$2" .)
}

# --- 4 managed-runtime Lambdas (.NET 10, arm64, ReadyToRun for faster cold starts) ------------------------------
declare -A PROJECTS=(
  [api]=StatusHub.Api
  [scheduler]=StatusHub.Functions.Scheduler
  [evaluator]=StatusHub.Functions.Evaluator
  [notifier]=StatusHub.Functions.Notifier
)
for name in "${!PROJECTS[@]}"; do
  echo ">> $name"
  dotnet publish "$ROOT/backend/src/${PROJECTS[$name]}" -c Release -r linux-arm64 --self-contained false \
    -p:PublishReadyToRun=true -p:DebugType=none -p:InformationalVersion="${GITHUB_SHA:-local}" -o "$WORK/$name" --nologo -v:q
  rm -f "$WORK/$name"/appsettings.Development.json
  zip_dir "$WORK/$name" "$OUT/$name.zip"
done

# --- Checker: Native AOT, built on Amazon Linux 2023 so the binary matches the provided.al2023 runtime ------------
mkdir -p "$WORK/checker"
if $PLACEHOLDER; then
  echo ">> checker (placeholder, plan only)"
  printf '#!/bin/sh\nexit 1\n' > "$WORK/checker/bootstrap"
  chmod +x "$WORK/checker/bootstrap"
else
  echo ">> checker (Native AOT in Amazon Linux 2023)"
  docker build --target artifact --output "type=local,dest=$WORK/checker" -f "$ROOT/build-tools/aot/Dockerfile" "$ROOT"
  chmod +x "$WORK/checker/bootstrap"
fi
zip_dir "$WORK/checker" "$OUT/checker.zip"

# --- manifest ---------------------------------------------------------------------------------------------------
{
  echo "{"
  echo "  \"commit\": \"${GITHUB_SHA:-local}\","
  echo "  \"placeholderChecker\": $PLACEHOLDER,"
  echo "  \"files\": {"
  first=true
  for f in api scheduler checker evaluator notifier; do
    $first || echo ","
    first=false
    printf '    "%s": {"bytes": %s, "sha256": "%s"}' "$f" "$(stat -c %s "$OUT/$f.zip" 2>/dev/null || stat -f %z "$OUT/$f.zip")" "$(sha256sum "$OUT/$f.zip" | cut -d' ' -f1)"
  done
  echo
  echo "  }"
  echo "}"
} > "$OUT/manifest.json"

rm -rf "$WORK"
ls -l "$OUT"
