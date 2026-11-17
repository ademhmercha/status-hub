#!/usr/bin/env bash
# StatusHub - checks which AWS services your account (Free plan / SCP) lets you use.
#
# For each service it creates a minimal resource, then deletes it immediately, and prints OK or REFUSE.
# Nothing is left behind (cleanup also runs on Ctrl+C or on error). Run it YOURSELF, it is not run by CI or by Claude.
#
# Usage:  AWS_PROFILE=teamchat ./infra/scripts/check-free-plan.sh
# Needs:  aws CLI v2, and one of: python3, python, zip, powershell (to build a tiny zip for the Lambda test).
set -uo pipefail

export AWS_PROFILE="${AWS_PROFILE:-teamchat}"
export AWS_REGION="${AWS_REGION:-eu-north-1}"
export AWS_DEFAULT_REGION="$AWS_REGION"
export AWS_PAGER=""
# Git Bash on Windows rewrites arguments starting with "/" (log group names...).
export MSYS_NO_PATHCONV=1

ID="chk$(date +%s)$RANDOM"
NAME="statushub-${ID}"
WORK="$(mktemp -d)"
RESULTS=()
CLEANUP=()

green() { printf '\033[32m%s\033[0m' "$1"; }
red() { printf '\033[31m%s\033[0m' "$1"; }

# record <service> <exit-code> <message>
record() {
  if [[ "$2" -eq 0 ]]; then
    echo "  $(green OK)      $1"
    RESULTS+=("OK|$1")
  else
    echo "  $(red REFUSE)  $1  -> ${3:-}" | head -c 400; echo
    RESULTS+=("REFUSE|$1")
  fi
}

# run <service> <create command...>   (create step: output captured for the error message)
try() {
  local svc="$1"; shift
  local out
  out="$("$@" 2>&1)"
  local rc=$?
  record "$svc" "$rc" "$(echo "$out" | tail -n 1)"
  return $rc
}

cleanup() {
  echo
  echo "Cleaning up..."
  # run registered cleanups in reverse order; ignore failures (resource may not exist)
  for ((i=${#CLEANUP[@]}-1; i>=0; i--)); do
    eval "${CLEANUP[$i]}" >/dev/null 2>&1 || true
  done
  rm -rf "$WORK"
}
trap cleanup EXIT

make_zip() { # make_zip <out.zip> : zip containing index.py
  echo 'def handler(e, c):
    return "ok"' > "$WORK/index.py"
  if command -v python3 >/dev/null 2>&1; then python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1],'w').write(sys.argv[2],'index.py')" "$1" "$WORK/index.py"
  elif command -v python >/dev/null 2>&1; then python -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1],'w').write(sys.argv[2],'index.py')" "$1" "$WORK/index.py"
  elif command -v zip >/dev/null 2>&1; then (cd "$WORK" && zip -q "$1" index.py)
  elif command -v powershell.exe >/dev/null 2>&1; then powershell.exe -NoProfile -Command "Compress-Archive -Force -Path '$(cygpath -w "$WORK/index.py")' -DestinationPath '$(cygpath -w "$1")'"
  else return 1; fi
}

echo "Account check (profile=$AWS_PROFILE region=$AWS_REGION, resource prefix: $NAME)"
if ! ACCOUNT=$(aws sts get-caller-identity --query Account --output text 2>&1); then
  echo "Cannot call AWS: $ACCOUNT"; exit 2
fi
echo "Account: $ACCOUNT"
echo

# --- IAM (needed by Lambda and Scheduler) ---------------------------------------------------------
TRUST='{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":["lambda.amazonaws.com","scheduler.amazonaws.com"]},"Action":"sts:AssumeRole"}]}'
if try "IAM role" aws iam create-role --role-name "$NAME" --assume-role-policy-document "$TRUST"; then
  CLEANUP+=("aws iam delete-role --role-name $NAME")
  ROLE_ARN="arn:aws:iam::${ACCOUNT}:role/${NAME}"
  sleep 8 # IAM propagation
fi

# --- DynamoDB ---------------------------------------------------------------------------------------
if try "DynamoDB" aws dynamodb create-table --table-name "$NAME" --billing-mode PAY_PER_REQUEST \
    --attribute-definitions AttributeName=PK,AttributeType=S --key-schema AttributeName=PK,KeyType=HASH; then
  CLEANUP+=("aws dynamodb delete-table --table-name $NAME")
  aws dynamodb wait table-exists --table-name "$NAME" >/dev/null 2>&1
  # features Terraform may use
  try "DynamoDB TTL" aws dynamodb update-time-to-live --table-name "$NAME" --time-to-live-specification Enabled=true,AttributeName=ttl
  try "DynamoDB point-in-time recovery (enable_pitr)" aws dynamodb update-continuous-backups --table-name "$NAME" \
    --point-in-time-recovery-specification PointInTimeRecoveryEnabled=true
fi

# --- SQS --------------------------------------------------------------------------------------------
if try "SQS" aws sqs create-queue --queue-name "$NAME"; then
  QUEUE_URL=$(aws sqs get-queue-url --queue-name "$NAME" --query QueueUrl --output text 2>/dev/null)
  CLEANUP+=("aws sqs delete-queue --queue-url $QUEUE_URL")
fi

# --- Lambda -----------------------------------------------------------------------------------------
if [[ -n "${ROLE_ARN:-}" ]] && make_zip "$WORK/f.zip"; then
  if try "Lambda" aws lambda create-function --function-name "$NAME" --runtime python3.12 --role "$ROLE_ARN" \
      --handler index.handler --zip-file "fileb://$WORK/f.zip" --tracing-config Mode=Active; then
    CLEANUP+=("aws lambda delete-function --function-name $NAME")
    aws lambda wait function-active-v2 --function-name "$NAME" >/dev/null 2>&1
    try "Lambda version + alias (rollback)" bash -c "aws lambda publish-version --function-name $NAME --query Version --output text | xargs -I{} aws lambda create-alias --function-name $NAME --name live --function-version {}"
  elif aws lambda create-function --function-name "$NAME" --runtime python3.12 --role "$ROLE_ARN" \
      --handler index.handler --zip-file "fileb://$WORK/f.zip" >/dev/null 2>&1; then
    echo "           (Lambda works without X-Ray active tracing: set enable_xray = false)"
    CLEANUP+=("aws lambda delete-function --function-name $NAME")
  fi
else
  record "Lambda" 1 "skipped: no IAM role or no tool to build a zip"
fi

# --- API Gateway (HTTP API) --------------------------------------------------------------------------
if API_ID=$(aws apigatewayv2 create-api --name "$NAME" --protocol-type HTTP --query ApiId --output text 2>&1); then
  record "API Gateway HTTP API" 0
  CLEANUP+=("aws apigatewayv2 delete-api --api-id $API_ID")
else
  record "API Gateway HTTP API" 1 "$API_ID"
fi

# --- EventBridge: bus and Scheduler --------------------------------------------------------------------
if try "EventBridge bus" aws events create-event-bus --name "$NAME"; then
  CLEANUP+=("aws events delete-event-bus --name $NAME")
fi
if try "EventBridge Scheduler group" aws scheduler create-schedule-group --name "$NAME"; then
  CLEANUP+=("aws scheduler delete-schedule-group --name $NAME")
  if [[ -n "${QUEUE_URL:-}" && -n "${ROLE_ARN:-}" ]]; then
    QUEUE_ARN=$(aws sqs get-queue-attributes --queue-url "$QUEUE_URL" --attribute-names QueueArn --query Attributes.QueueArn --output text 2>/dev/null)
    if try "EventBridge Scheduler schedule" aws scheduler create-schedule --name "$NAME" --group-name "$NAME" \
        --schedule-expression "rate(1 day)" --state DISABLED --flexible-time-window Mode=OFF \
        --target "{\"Arn\":\"$QUEUE_ARN\",\"RoleArn\":\"$ROLE_ARN\"}"; then
      CLEANUP+=("aws scheduler delete-schedule --name $NAME --group-name $NAME")
    fi
  fi
fi

# --- Cognito ---------------------------------------------------------------------------------------
if POOL_ID=$(aws cognito-idp create-user-pool --pool-name "$NAME" --query UserPool.Id --output text 2>&1); then
  record "Cognito user pool" 0
  CLEANUP+=("aws cognito-idp delete-user-pool --user-pool-id $POOL_ID")
else
  record "Cognito user pool" 1 "$POOL_ID"
fi

# --- SES (domain identity: no email is sent) ----------------------------------------------------------
if try "SES identity (enable_ses)" aws sesv2 create-email-identity --email-identity "${NAME}.example.com"; then
  CLEANUP+=("aws sesv2 delete-email-identity --email-identity ${NAME}.example.com")
fi

# --- X-Ray -----------------------------------------------------------------------------------------
if try "X-Ray (enable_xray)" aws xray create-group --group-name "$NAME" --filter-expression 'service("statushub-check")'; then
  CLEANUP+=("aws xray delete-group --group-name $NAME")
fi

# --- S3 --------------------------------------------------------------------------------------------
BUCKET="${NAME}-${ACCOUNT}"
if try "S3" aws s3api create-bucket --bucket "$BUCKET" --region "$AWS_REGION" --create-bucket-configuration "LocationConstraint=$AWS_REGION"; then
  CLEANUP+=("aws s3api delete-bucket --bucket $BUCKET")
fi

# --- CloudFront (an origin access control: a real distribution takes minutes to delete) ------------------
OAC_CFG="{\"Name\":\"$NAME\",\"SigningProtocol\":\"sigv4\",\"SigningBehavior\":\"always\",\"OriginAccessControlOriginType\":\"s3\"}"
if OAC=$(aws cloudfront create-origin-access-control --origin-access-control-config "$OAC_CFG" --query 'OriginAccessControl.[Id]' --output text 2>&1); then
  record "CloudFront (enable_cloudfront) - origin access control only" 0
  ETAG=$(aws cloudfront get-origin-access-control --id "$OAC" --query ETag --output text 2>/dev/null)
  CLEANUP+=("aws cloudfront delete-origin-access-control --id $OAC --if-match $ETAG")
else
  record "CloudFront (enable_cloudfront)" 1 "$OAC"
fi

# --- Observability / alerts ------------------------------------------------------------------------------
if try "CloudWatch Logs" aws logs create-log-group --log-group-name "/statushub/$NAME"; then
  CLEANUP+=("aws logs delete-log-group --log-group-name /statushub/$NAME")
fi
if TOPIC=$(aws sns create-topic --name "$NAME" --query TopicArn --output text 2>&1); then
  record "SNS" 0
  CLEANUP+=("aws sns delete-topic --topic-arn $TOPIC")
else
  record "SNS" 1 "$TOPIC"
fi
if try "CloudWatch alarm" aws cloudwatch put-metric-alarm --alarm-name "$NAME" --namespace StatusHubCheck --metric-name X \
    --statistic Sum --period 60 --evaluation-periods 1 --threshold 1 --comparison-operator GreaterThanThreshold; then
  CLEANUP+=("aws cloudwatch delete-alarms --alarm-names $NAME")
fi

# --- Summary -----------------------------------------------------------------------------------------
echo
echo "Summary"
refused=0
for r in "${RESULTS[@]}"; do
  [[ "${r%%|*}" == "REFUSE" ]] && { refused=$((refused+1)); echo "  REFUSE  ${r#*|}"; }
done
if [[ $refused -eq 0 ]]; then echo "  Everything is allowed: keep the default variables."; else
  echo "  $refused service(s) refused: disable the matching variable (see the name in parentheses) in infra/envs/<env>/terraform.tfvars."; fi
exit 0
