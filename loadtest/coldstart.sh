#!/usr/bin/env bash
# Cold start: Native AOT vs JIT (self-contained, ReadyToRun) for the SAME Checker code, same memory, same role, same region.
# Creates two temporary Lambda functions, forces N cold starts on each, prints a markdown table, then deletes them.
# Run it yourself (it changes your AWS account for a few minutes; no cost to speak of).
#
#   scripts/package-lambdas.sh --jit-checker          # builds build/lambda/checker.zip (AOT) and checker-jit.zip
#   loadtest/coldstart.sh dev [runs=15] [memory=256]
set -euo pipefail

ENVNAME="${1:?usage: coldstart.sh <dev|prod> [runs] [memory]}"
RUNS="${2:-15}"
MEMORY="${3:-256}"
export AWS_PROFILE="${AWS_PROFILE:-teamchat}" AWS_REGION="${AWS_REGION:-eu-north-1}" AWS_PAGER=""
cd "$(dirname "${BASH_SOURCE[0]}")/.."

AOT_ZIP="build/lambda/checker.zip"
JIT_ZIP="build/lambda/checker-jit.zip"
[[ -f "$AOT_ZIP" && -f "$JIT_ZIP" ]] || { echo "missing zips: run scripts/package-lambdas.sh --jit-checker"; exit 1; }

BASE="statushub-${ENVNAME}-checker"
ROLE=$(aws lambda get-function-configuration --function-name "$BASE" --query Role --output text)
ENV_JSON=$(aws lambda get-function-configuration --function-name "$BASE" --query 'Environment.Variables' --output json)
WORK="$(mktemp -d)"
NAMES=("${BASE}-bench-aot" "${BASE}-bench-jit")
ZIPS=("$AOT_ZIP" "$JIT_ZIP")

cleanup() {
  for n in "${NAMES[@]}"; do aws lambda delete-function --function-name "$n" >/dev/null 2>&1 || true; done
  rm -rf "$WORK"
}
trap cleanup EXIT

# One synthetic SQS record: a check of a monitor that does not exist (the Evaluator ignores its result).
cat > "$WORK/event.json" <<EOF
{"Records":[{"messageId":"bench-1","body":"{\"accountId\":\"bench\",\"monitorId\":\"bench\",\"minute\":\"2026-01-01T00:00:00+00:00\",\"url\":\"https://example.com\",\"method\":\"GET\",\"expectedStatus\":200,\"keyword\":null,\"timeoutSeconds\":5}"}]}
EOF

for i in 0 1; do
  aws lambda create-function --function-name "${NAMES[$i]}" --runtime provided.al2023 --handler bootstrap --architectures x86_64 \
    --memory-size "$MEMORY" --timeout 30 --role "$ROLE" --zip-file "fileb://${ZIPS[$i]}" \
    --environment "Variables=$ENV_JSON" >/dev/null
  aws lambda wait function-active-v2 --function-name "${NAMES[$i]}"
  : > "$WORK/${i}.csv"
done

for run in $(seq 1 "$RUNS"); do
  for i in 0 1; do
    # Changing an environment variable discards the warm execution environments: the next invocation is a cold start.
    VARS=$(node -e "const v=JSON.parse(process.argv[1]);v.BENCH_RUN='${run}';console.log(JSON.stringify({Variables:v}))" "$ENV_JSON")
    aws lambda update-function-configuration --function-name "${NAMES[$i]}" --environment "$VARS" >/dev/null
    aws lambda wait function-updated-v2 --function-name "${NAMES[$i]}"
    aws lambda invoke --function-name "${NAMES[$i]}" --payload "fileb://$WORK/event.json" --log-type Tail \
      --query LogResult --output text "$WORK/out.json" | base64 -d | grep REPORT >> "$WORK/${i}.log" || true
  done
  echo "run $run/$RUNS done"
done

node - "$WORK" <<'JS'
const fs = require('fs');
const dir = process.argv[2];
const stat = (xs) => {
  const s = [...xs].sort((a, b) => a - b);
  const q = (p) => s[Math.min(s.length - 1, Math.ceil(p * s.length) - 1)];
  return { n: s.length, min: s[0], p50: q(0.5), p95: q(0.95), max: s[s.length - 1], avg: s.reduce((a, b) => a + b, 0) / s.length };
};
const names = ['Native AOT', 'JIT (self-contained, ReadyToRun)'];
const rows = [];
for (const i of [0, 1]) {
  const lines = fs.readFileSync(`${dir}/${i}.log`, 'utf8').split('\n').filter(Boolean);
  const num = (re) => lines.map((l) => (l.match(re) || [])[1]).filter(Boolean).map(Number);
  rows.push({ name: names[i], init: stat(num(/Init Duration: ([\d.]+) ms/)), dur: stat(num(/\tDuration: ([\d.]+) ms/)), mem: stat(num(/Max Memory Used: (\d+) MB/)) });
}
const f = (x) => x.toFixed(0);
console.log('\n| Variant | Cold starts | Init min / p50 / p95 / max (ms) | Handler duration p50 / p95 (ms) | Total p50 (ms) | Max memory (MB) |');
console.log('|---|---|---|---|---|---|');
for (const r of rows) console.log(`| ${r.name} | ${r.init.n} | ${f(r.init.min)} / ${f(r.init.p50)} / ${f(r.init.p95)} / ${f(r.init.max)} | ${f(r.dur.p50)} / ${f(r.dur.p95)} | ${f(r.init.p50 + r.dur.p50)} | ${f(r.mem.max)} |`);
JS
