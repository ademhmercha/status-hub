#!/usr/bin/env bash
# Creates / deletes the Cognito users used by the load test (loadtest-01@example.com ...). Run it yourself.
#
#   USER_PASSWORD='Some-Strong-Pass-123' loadtest/users.sh create dev 25
#   loadtest/users.sh delete dev 25
set -euo pipefail

ACTION="${1:?usage: users.sh <create|delete> <dev|prod> [count]}"
ENVNAME="${2:?usage: users.sh <create|delete> <dev|prod> [count]}"
COUNT="${3:-25}"
export AWS_PROFILE="${AWS_PROFILE:-teamchat}" AWS_REGION="${AWS_REGION:-eu-north-1}" AWS_PAGER=""

POOL=$(aws cognito-idp list-user-pools --max-results 60 --query "UserPools[?Name=='statushub-${ENVNAME}'].Id | [0]" --output text)
[[ "$POOL" != "None" && -n "$POOL" ]] || { echo "user pool statushub-${ENVNAME} not found"; exit 1; }

for n in $(seq 1 "$COUNT"); do
  EMAIL=$(printf 'loadtest-%02d@example.com' "$n")
  if [[ "$ACTION" == "create" ]]; then
    : "${USER_PASSWORD:?set USER_PASSWORD (>= 10 chars, upper, lower, digit)}"
    aws cognito-idp admin-create-user --user-pool-id "$POOL" --username "$EMAIL" --message-action SUPPRESS \
      --user-attributes Name=email,Value="$EMAIL" Name=email_verified,Value=true >/dev/null 2>&1 || true
    aws cognito-idp admin-set-user-password --user-pool-id "$POOL" --username "$EMAIL" --password "$USER_PASSWORD" --permanent
    echo "created $EMAIL"
  else
    aws cognito-idp admin-delete-user --user-pool-id "$POOL" --username "$EMAIL" >/dev/null 2>&1 && echo "deleted $EMAIL" || true
  fi
done
