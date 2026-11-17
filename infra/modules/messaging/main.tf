terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

variable "name_prefix" {
  type = string
}

variable "dlq_max_receive_count" {
  type    = number
  default = 3
}

locals {
  # Visibility timeout must be >= 6x the timeout of the Lambda that consumes the queue (AWS recommendation).
  #   checker timeout 45 s, evaluator 30 s, notifier 120 s (see modules/functions).
  # Stale checks are useless, so the checks queue keeps messages only 15 minutes.
  queues = {
    checks   = { visibility = 270, retention = 900 }
    results  = { visibility = 180, retention = 86400 }
    notifier = { visibility = 720, retention = 345600 }
  }
}

resource "aws_sqs_queue" "dlq" {
  for_each = local.queues

  name                      = "${var.name_prefix}-${each.key}-dlq"
  message_retention_seconds = 1209600 # 14 days, to leave time to replay
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "main" {
  for_each = local.queues

  name                       = "${var.name_prefix}-${each.key}"
  visibility_timeout_seconds = each.value.visibility
  message_retention_seconds  = each.value.retention
  sqs_managed_sse_enabled    = true

  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.dlq[each.key].arn
    maxReceiveCount     = var.dlq_max_receive_count
  })
}

resource "aws_sqs_queue_redrive_allow_policy" "dlq" {
  for_each = local.queues

  queue_url = aws_sqs_queue.dlq[each.key].id
  redrive_allow_policy = jsonencode({
    redrivePermission = "byQueue"
    sourceQueueArns   = [aws_sqs_queue.main[each.key].arn]
  })
}

# --- EventBridge bus: IncidentOpened / IncidentResolved -> notifier queue ------------------------------------

resource "aws_cloudwatch_event_bus" "this" {
  name = var.name_prefix
}

resource "aws_cloudwatch_event_rule" "incidents" {
  name           = "${var.name_prefix}-incidents"
  event_bus_name = aws_cloudwatch_event_bus.this.name
  event_pattern = jsonencode({
    source        = ["statushub"]
    "detail-type" = ["IncidentOpened", "IncidentResolved"]
  })
}

resource "aws_cloudwatch_event_target" "notifier" {
  rule           = aws_cloudwatch_event_rule.incidents.name
  event_bus_name = aws_cloudwatch_event_bus.this.name
  target_id      = "notifier-queue"
  arn            = aws_sqs_queue.main["notifier"].arn
}

data "aws_iam_policy_document" "notifier_queue" {
  statement {
    sid       = "AllowEventBridgeRule"
    actions   = ["sqs:SendMessage"]
    resources = [aws_sqs_queue.main["notifier"].arn]
    principals {
      type        = "Service"
      identifiers = ["events.amazonaws.com"]
    }
    condition {
      test     = "ArnEquals"
      variable = "aws:SourceArn"
      values   = [aws_cloudwatch_event_rule.incidents.arn]
    }
  }
}

resource "aws_sqs_queue_policy" "notifier" {
  queue_url = aws_sqs_queue.main["notifier"].id
  policy    = data.aws_iam_policy_document.notifier_queue.json
}

output "queue_urls" {
  value = { for k, q in aws_sqs_queue.main : k => q.id }
}

output "queue_arns" {
  value = { for k, q in aws_sqs_queue.main : k => q.arn }
}

output "queue_names" {
  value = { for k, q in aws_sqs_queue.main : k => q.name }
}

output "dlq_urls" {
  value = { for k, q in aws_sqs_queue.dlq : k => q.id }
}

output "dlq_arns" {
  value = { for k, q in aws_sqs_queue.dlq : k => q.arn }
}

output "dlq_names" {
  value = { for k, q in aws_sqs_queue.dlq : k => q.name }
}

output "bus_name" {
  value = aws_cloudwatch_event_bus.this.name
}

output "bus_arn" {
  value = aws_cloudwatch_event_bus.this.arn
}
