#!/usr/bin/env bash
# Real monthly cost of an environment, from AWS Cost Explorer, grouped by service. Uses the cost allocation tags Project / Environment.
#
#   loadtest/cost-report.sh dev 2026-11
#
# Prerequisites (once, by you): Billing console -> Cost allocation tags -> activate `Project` and `Environment`
# (they appear 24 h after the first tagged resource exists). Each Cost Explorer API call costs about $0.01.
set -euo pipefail

ENVNAME="${1:?usage: cost-report.sh <dev|prod> <YYYY-MM>}"
MONTH="${2:?usage: cost-report.sh <dev|prod> <YYYY-MM>}"
export AWS_PROFILE="${AWS_PROFILE:-teamchat}" AWS_PAGER=""
# Cost Explorer is a global endpoint served from us-east-1.
export AWS_REGION=us-east-1

START="${MONTH}-01"
END=$(date -d "${START} +1 month" +%Y-%m-%d 2>/dev/null || node -e "const d=new Date('${START}T00:00:00Z');d.setUTCMonth(d.getUTCMonth()+1);console.log(d.toISOString().slice(0,10))")

FILTER=$(cat <<EOF
{"And":[{"Tags":{"Key":"Project","Values":["StatusHub"]}},{"Tags":{"Key":"Environment","Values":["${ENVNAME}"]}}]}
EOF
)

aws ce get-cost-and-usage --time-period "Start=${START},End=${END}" --granularity MONTHLY \
  --metrics UnblendedCost --filter "$FILTER" --group-by Type=DIMENSION,Key=SERVICE --output json \
| node -e "
const r = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const groups = (r.ResultsByTime[0]?.Groups ?? []).map(g => [g.Keys[0], parseFloat(g.Metrics.UnblendedCost.Amount)]).sort((a, b) => b[1] - a[1]);
let total = 0;
console.log('| Service | Cost (USD) |\n|---|---|');
for (const [k, v] of groups) { total += v; console.log('| ' + k + ' | ' + v.toFixed(2) + ' |'); }
console.log('| **Total** | **' + total.toFixed(2) + '** |');
if (groups.length === 0) console.log('\n(No data: are the tags activated as cost allocation tags, and has 24 h passed?)');
"
