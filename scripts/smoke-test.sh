#!/usr/bin/env bash
# Post-deployment smoke test. Usage: scripts/smoke-test.sh <api_base_url> [frontend_url]
# Fails (exit 1) if the API or the frontend do not answer as expected.
set -euo pipefail

API="${1:?usage: smoke-test.sh <api_base_url> [frontend_url]}"
API="${API%/}"
FRONT="${2:-}"

retry() { # retry <attempts> <delay> <cmd...>  (a fresh deployment needs a few seconds)
  local n=$1 d=$2; shift 2
  for ((i=1; i<=n; i++)); do "$@" && return 0; sleep "$d"; done
  return 1
}

status() { curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$@"; }

expect() { # expect <description> <expected code> <url...>
  local desc=$1 want=$2; shift 2
  local got; got=$(status "$@")
  if [[ "$got" == "$want" ]]; then echo "OK    $desc ($got)"; else echo "FAIL  $desc: expected $want, got $got"; return 1; fi
}

fail=0
retry 12 5 bash -c "[[ \$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 '$API/health') == 200 ]]" || true
expect "GET /health" 200 "$API/health" || fail=1
expect "GET /demo/target" 200 "$API/demo/target" || fail=1
expect "public status page, unknown slug -> 404 (route + Lambda + DynamoDB reachable)" 404 "$API/api/v1/status/smoke-test-does-not-exist" || fail=1
expect "private route without token -> 401 (JWT authorizer active)" 401 "$API/api/v1/monitors" || fail=1
expect "OpenAPI document" 200 "$API/openapi/v1.json" || fail=1

if [[ -n "$FRONT" ]]; then
  FRONT="${FRONT%/}"
  expect "frontend index" 200 "$FRONT/" || fail=1
  expect "frontend SPA route falls back to index.html" 200 "$FRONT/status/smoke-test" || fail=1
  expect "frontend runtime config" 200 "$FRONT/config.json" || fail=1
fi

[[ $fail -eq 0 ]] && echo "Smoke test passed." || { echo "Smoke test FAILED."; exit 1; }
