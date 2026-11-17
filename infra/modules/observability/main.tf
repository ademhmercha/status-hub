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

variable "metrics_namespace" {
  description = "CloudWatch namespace used by the Powertools EMF metrics (POWERTOOLS_METRICS_NAMESPACE)"
  type        = string
}

variable "function_names" {
  description = "Lambda function names by role (api, scheduler, checker, evaluator, notifier)"
  type        = map(string)
}

variable "queue_names" {
  type = map(string)
}

variable "dlq_names" {
  type = map(string)
}

variable "api_id" {
  type = string
}

variable "alarm_email" {
  description = "Email receiving the alarms (SNS asks for a confirmation click). Empty = topic without subscriber."
  type        = string
  default     = ""
}

variable "slo_target" {
  description = "Share of checks that must run within 60 s of their scheduled time (rolling 30 days)"
  type        = number
  default     = 0.995
}

variable "fast_burn_rate" {
  description = "Burn rate over 1 h that pages: 14.4 = 2% of the 30-day budget consumed in one hour"
  type        = number
  default     = 14.4
}

variable "slow_burn_rate" {
  description = "Burn rate over 6 h that warns: 6 = 5% of the 30-day budget consumed in six hours"
  type        = number
  default     = 6
}

data "aws_region" "current" {}

locals {
  ns           = var.metrics_namespace
  region       = data.aws_region.current.region
  error_budget = 1 - var.slo_target # 0.005
  fn_keys      = sort(keys(var.function_names))
  # AWS/Lambda metrics are recorded per function name
  lambda_metric = { for m in ["Errors", "Throttles", "Invocations"] : m => [for k in local.fn_keys : ["AWS/Lambda", m, "FunctionName", var.function_names[k], { label = k }]] }
}

# --- notifications --------------------------------------------------------------------------------------------------

resource "aws_sns_topic" "alarms" {
  name = "${var.name_prefix}-alarms"
}

data "aws_caller_identity" "current" {}

data "aws_iam_policy_document" "alarms_topic" {
  statement {
    sid       = "AllowCloudWatchAlarms"
    actions   = ["sns:Publish"]
    resources = [aws_sns_topic.alarms.arn]
    principals {
      type        = "Service"
      identifiers = ["cloudwatch.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [data.aws_caller_identity.current.account_id]
    }
  }
}

resource "aws_sns_topic_policy" "alarms" {
  arn    = aws_sns_topic.alarms.arn
  policy = data.aws_iam_policy_document.alarms_topic.json
}

resource "aws_sns_topic_subscription" "email" {
  count = var.alarm_email == "" ? 0 : 1

  topic_arn = aws_sns_topic.alarms.arn
  protocol  = "email"
  endpoint  = var.alarm_email
}

# --- dashboard ----------------------------------------------------------------------------------------------------------

locals {
  checker   = "checker"
  scheduler = "scheduler"

  widgets = [
    {
      type = "metric", x = 0, y = 0, width = 8, height = 6
      properties = {
        title  = "Check throughput (per minute)"
        region = local.region, view = "timeSeries", stat = "Sum", period = 60
        metrics = [
          [local.ns, "ChecksScheduled", "Service", local.scheduler, { label = "scheduled" }],
          [local.ns, "ChecksExecuted", "Service", local.checker, { label = "executed" }],
          [local.ns, "ResultsProcessed", "Service", "evaluator", { label = "evaluated" }],
        ]
      }
    },
    {
      type = "metric", x = 8, y = 0, width = 8, height = 6
      properties = {
        title  = "Check latency (ms)"
        region = local.region, view = "timeSeries", period = 60
        metrics = [
          [local.ns, "CheckLatencyMs", "Service", local.checker, { stat = "p50", label = "p50" }],
          [local.ns, "CheckLatencyMs", "Service", local.checker, { stat = "p95", label = "p95" }],
        ]
      }
    },
    {
      type = "metric", x = 16, y = 0, width = 8, height = 6
      properties = {
        title  = "Late and failed checks"
        region = local.region, view = "timeSeries", stat = "Sum", period = 60
        metrics = [
          [local.ns, "ChecksLate", "Service", local.checker, { label = "late (> 60 s)" }],
          [local.ns, "ChecksFailed", "Service", local.checker, { label = "target down" }],
        ]
      }
    },
    {
      type = "metric", x = 0, y = 6, width = 8, height = 6
      properties = {
        title   = "Lambda errors"
        region  = local.region, view = "timeSeries", stat = "Sum", period = 60
        metrics = local.lambda_metric["Errors"]
      }
    },
    {
      type = "metric", x = 8, y = 6, width = 8, height = 6
      properties = {
        title   = "Lambda throttles"
        region  = local.region, view = "timeSeries", stat = "Sum", period = 60
        metrics = local.lambda_metric["Throttles"]
      }
    },
    {
      type = "metric", x = 16, y = 6, width = 8, height = 6
      properties = {
        title  = "Cold starts (per function)"
        region = local.region, view = "timeSeries", stat = "Sum", period = 300
        metrics = [
          [{ expression = "SEARCH('Namespace=\"${local.ns}\" MetricName=\"ColdStart\"', 'Sum', 300)", id = "cold", label = "" }],
        ]
      }
    },
    {
      type = "metric", x = 0, y = 12, width = 8, height = 6
      properties = {
        title   = "Queue depth (messages waiting)"
        region  = local.region, view = "timeSeries", stat = "Maximum", period = 60
        metrics = [for k in sort(keys(var.queue_names)) : ["AWS/SQS", "ApproximateNumberOfMessagesVisible", "QueueName", var.queue_names[k], { label = k }]]
      }
    },
    {
      type = "metric", x = 8, y = 12, width = 8, height = 6
      properties = {
        title   = "Dead-letter queues (must stay at 0)"
        region  = local.region, view = "timeSeries", stat = "Maximum", period = 60
        metrics = [for k in sort(keys(var.dlq_names)) : ["AWS/SQS", "ApproximateNumberOfMessagesVisible", "QueueName", var.dlq_names[k], { label = "${k}-dlq" }]]
      }
    },
    {
      type = "metric", x = 16, y = 12, width = 8, height = 6
      properties = {
        title  = "Incidents opened vs resolved; open = opened - resolved over the window"
        region = local.region, view = "timeSeries", period = 300
        metrics = [
          [local.ns, "IncidentsOpened", "Service", "evaluator", { id = "o", stat = "Sum", label = "opened" }],
          [local.ns, "IncidentsResolved", "Service", "evaluator", { id = "r", stat = "Sum", label = "resolved" }],
          [{ expression = "RUNNING_SUM(FILL(o,0)) - RUNNING_SUM(FILL(r,0))", id = "open", label = "open (net)" }],
        ]
      }
    },
    {
      type = "metric", x = 0, y = 18, width = 12, height = 6
      properties = {
        title       = "SLO: checks run within 60 s of schedule (target ${var.slo_target * 100}%)"
        region      = local.region, view = "timeSeries", period = 3600
        yAxis       = { left = { min = 90, max = 100 } }
        annotations = { horizontal = [{ label = "SLO", value = var.slo_target * 100 }] }
        metrics = [
          [local.ns, "ChecksScheduled", "Service", local.scheduler, { id = "s", stat = "Sum", visible = false }],
          [local.ns, "ChecksOnTime", "Service", local.checker, { id = "o", stat = "Sum", visible = false }],
          [{ expression = "IF(FILL(s,0)>0, 100*FILL(o,0)/FILL(s,0), 100)", id = "sli", label = "on-time %" }],
        ]
      }
    },
    {
      type = "metric", x = 12, y = 18, width = 6, height = 6
      properties = {
        title  = "Error budget remaining (30 days)"
        region = local.region, view = "singleValue", period = 2592000, start = "-P30D", end = "P0D"
        metrics = [
          [local.ns, "ChecksScheduled", "Service", local.scheduler, { id = "s", stat = "Sum", visible = false }],
          [local.ns, "ChecksOnTime", "Service", local.checker, { id = "o", stat = "Sum", visible = false }],
          [{ expression = "IF(FILL(s,0)>0, 100 - 100*(FILL(s,0)-FILL(o,0))/(${local.error_budget}*FILL(s,0)), 100)", id = "budget", label = "budget left %" }],
        ]
      }
    },
    {
      type = "metric", x = 18, y = 18, width = 6, height = 6
      properties = {
        title  = "API Gateway: 5xx / 4xx"
        region = local.region, view = "timeSeries", stat = "Sum", period = 300
        metrics = [
          ["AWS/ApiGateway", "5xx", "ApiId", var.api_id, { label = "5xx" }],
          ["AWS/ApiGateway", "4xx", "ApiId", var.api_id, { label = "4xx" }],
        ]
      }
    },
    {
      type = "log", x = 0, y = 24, width = 24, height = 6
      properties = {
        title  = "Cold starts and init duration (Lambda REPORT lines)"
        region = local.region, view = "table"
        query = join(" | ", concat(
          [join(" ", [for k in local.fn_keys : "SOURCE '/aws/lambda/${var.function_names[k]}'"])],
          [
            "filter @type = \"REPORT\"",
            "stats count(*) as invocations, count(@initDuration) as coldStarts, avg(@initDuration) as avgInitMs, max(@initDuration) as maxInitMs, pct(@duration, 95) as p95DurationMs, max(@maxMemoryUsed / 1000000) as maxMemoryMB by @log",
          ]
        ))
      }
    },
  ]
}

resource "aws_cloudwatch_dashboard" "this" {
  dashboard_name = var.name_prefix
  dashboard_body = jsonencode({ widgets = local.widgets })
}

# --- alarms ----------------------------------------------------------------------------------------------------------------

locals {
  alarm_actions = [aws_sns_topic.alarms.arn]
}

resource "aws_cloudwatch_metric_alarm" "dlq_not_empty" {
  for_each = var.dlq_names

  alarm_name          = "${var.name_prefix}-dlq-${each.key}-not-empty"
  alarm_description   = "Messages are stuck in the ${each.key} dead-letter queue. Runbook: docs/runbook.md#dlq-not-empty"
  namespace           = "AWS/SQS"
  metric_name         = "ApproximateNumberOfMessagesVisible"
  dimensions          = { QueueName = each.value }
  statistic           = "Maximum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "lambda_errors" {
  for_each = var.function_names

  alarm_name          = "${var.name_prefix}-${each.key}-errors"
  alarm_description   = "Lambda ${each.key} returned errors. Runbook: docs/runbook.md#lambda-errors"
  namespace           = "AWS/Lambda"
  metric_name         = "Errors"
  dimensions          = { FunctionName = each.value }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 1
  comparison_operator = "GreaterThanOrEqualToThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "lambda_throttles" {
  for_each = var.function_names

  alarm_name          = "${var.name_prefix}-${each.key}-throttles"
  alarm_description   = "Lambda ${each.key} is throttled (concurrency limit). Runbook: docs/runbook.md#lambda-throttling"
  namespace           = "AWS/Lambda"
  metric_name         = "Throttles"
  dimensions          = { FunctionName = each.value }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 1
  comparison_operator = "GreaterThanOrEqualToThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "api_5xx" {
  alarm_name          = "${var.name_prefix}-api-5xx"
  alarm_description   = "The API returns 5xx errors. Runbook: docs/runbook.md#api-5xx"
  namespace           = "AWS/ApiGateway"
  metric_name         = "5xx"
  dimensions          = { ApiId = var.api_id }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 5
  comparison_operator = "GreaterThanOrEqualToThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

# No scheduler invocation for 10 minutes: nothing is being checked at all (the burn-rate alarms cannot see that).
resource "aws_cloudwatch_metric_alarm" "scheduler_silent" {
  alarm_name          = "${var.name_prefix}-scheduler-silent"
  alarm_description   = "The scheduler Lambda is not being invoked every minute. Runbook: docs/runbook.md#scheduler-silent"
  namespace           = "AWS/Lambda"
  metric_name         = "Invocations"
  dimensions          = { FunctionName = var.function_names["scheduler"] }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 2
  threshold           = 1
  comparison_operator = "LessThanThreshold"
  treat_missing_data  = "breaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

# --- SLO burn rate (multi-window) ------------------------------------------------------------------------------------------
# error ratio = (scheduled - on time) / scheduled ; burn rate = error ratio / error budget. A burn rate of 1 uses exactly the
# 30-day budget in 30 days. Windows with very few checks are ignored to avoid paging on one lost check.

resource "aws_cloudwatch_metric_alarm" "slo_fast_burn" {
  alarm_name          = "${var.name_prefix}-slo-fast-burn"
  alarm_description   = "SLO fast burn: the 30-day error budget is being consumed ${var.fast_burn_rate}x too fast (1 h window). Runbook: docs/runbook.md#slo-burn-rate"
  comparison_operator = "GreaterThanThreshold"
  threshold           = var.fast_burn_rate
  evaluation_periods  = 1
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions

  metric_query {
    id = "s"
    metric {
      namespace   = local.ns
      metric_name = "ChecksScheduled"
      dimensions  = { Service = local.scheduler }
      period      = 3600
      stat        = "Sum"
    }
  }
  metric_query {
    id = "o"
    metric {
      namespace   = local.ns
      metric_name = "ChecksOnTime"
      dimensions  = { Service = local.checker }
      period      = 3600
      stat        = "Sum"
    }
  }
  metric_query {
    id          = "burn"
    expression  = "IF(FILL(s,0)>=30, ((FILL(s,0)-FILL(o,0))/FILL(s,0))/${local.error_budget}, 0)"
    label       = "burn rate (1 h)"
    return_data = true
  }
}

resource "aws_cloudwatch_metric_alarm" "slo_slow_burn" {
  alarm_name          = "${var.name_prefix}-slo-slow-burn"
  alarm_description   = "SLO slow burn: the error budget is being consumed ${var.slow_burn_rate}x too fast (6 h window). Runbook: docs/runbook.md#slo-burn-rate"
  comparison_operator = "GreaterThanThreshold"
  threshold           = var.slow_burn_rate
  evaluation_periods  = 1
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions

  metric_query {
    id = "s"
    metric {
      namespace   = local.ns
      metric_name = "ChecksScheduled"
      dimensions  = { Service = local.scheduler }
      period      = 21600
      stat        = "Sum"
    }
  }
  metric_query {
    id = "o"
    metric {
      namespace   = local.ns
      metric_name = "ChecksOnTime"
      dimensions  = { Service = local.checker }
      period      = 21600
      stat        = "Sum"
    }
  }
  metric_query {
    id          = "burn"
    expression  = "IF(FILL(s,0)>=100, ((FILL(s,0)-FILL(o,0))/FILL(s,0))/${local.error_budget}, 0)"
    label       = "burn rate (6 h)"
    return_data = true
  }
}

output "dashboard_name" {
  value = aws_cloudwatch_dashboard.this.dashboard_name
}

output "alarm_topic_arn" {
  value = aws_sns_topic.alarms.arn
}
