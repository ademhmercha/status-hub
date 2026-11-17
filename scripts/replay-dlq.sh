#!/usr/bin/env bash
# Replays the messages of a dead-letter queue into the queue they came from (SQS native redrive).
#
#   scripts/replay-dlq.sh <dev|prod> <checks|results|notifier> [--yes]
#
# Safe to replay: the Scheduler (monitor + minute) and the Evaluator (result key) are idempotent, and a failed alert is the only
# thing that can be sent twice. Fix the cause first (see docs/runbook.md), otherwise the messages come straight back.
# Note: the `checks` queue drops messages older than 15 minutes, so replaying old checks is pointless: purge instead.
set -euo pipefail

ENVNAME="${1:?usage: replay-dlq.sh <dev|prod> <checks|results|notifier> [--yes]}"
QUEUE="${2:?usage: replay-dlq.sh <dev|prod> <checks|results|notifier> [--yes]}"
YES="${3:-}"

export AWS_PROFILE="${AWS_PROFILE:-teamchat}"
export AWS_REGION="${AWS_REGION:-eu-north-1}"
export AWS_PAGER=""

case "$ENVNAME" in dev|prod) ;; *) echo "environment must be dev or prod"; exit 2 ;; esac
case "$QUEUE" in checks|results|notifier) ;; *) echo "queue must be checks, results or notifier"; exit 2 ;; esac

DLQ_NAME="statushub-${ENVNAME}-${QUEUE}-dlq"
DLQ_URL=$(aws sqs get-queue-url --queue-name "$DLQ_NAME" --query QueueUrl --output text)
DLQ_ARN=$(aws sqs get-queue-attributes --queue-url "$DLQ_URL" --attribute-names QueueArn --query Attributes.QueueArn --output text)
COUNT=$(aws sqs get-queue-attributes --queue-url "$DLQ_URL" --attribute-names ApproximateNumberOfMessages --query Attributes.ApproximateNumberOfMessages --output text)

echo "Queue: $DLQ_NAME  ($COUNT message(s))"
if [[ "$COUNT" == "0" ]]; then echo "Nothing to replay."; exit 0; fi

if [[ "$YES" != "--yes" ]]; then
  read -r -p "Replay $COUNT message(s) into statushub-${ENVNAME}-${QUEUE}? [y/N] " answer
  [[ "$answer" =~ ^[yY]$ ]] || { echo "Cancelled."; exit 1; }
fi

TASK=$(aws sqs start-message-move-task --source-arn "$DLQ_ARN" --max-number-of-messages-per-second 5 --query TaskHandle --output text)
echo "Redrive started."

for _ in $(seq 1 120); do
  STATUS=$(aws sqs list-message-move-tasks --source-arn "$DLQ_ARN" --max-results 1 --query 'Results[0].Status' --output text)
  MOVED=$(aws sqs list-message-move-tasks --source-arn "$DLQ_ARN" --max-results 1 --query 'Results[0].ApproximateNumberOfMessagesMoved' --output text)
  echo "  status=$STATUS moved=$MOVED"
  [[ "$STATUS" != "RUNNING" ]] && break
  sleep 5
done

LEFT=$(aws sqs get-queue-attributes --queue-url "$DLQ_URL" --attribute-names ApproximateNumberOfMessages --query Attributes.ApproximateNumberOfMessages --output text)
echo "Done. Messages left in the DLQ: $LEFT (they failed again if > 0: read the logs before retrying)."
