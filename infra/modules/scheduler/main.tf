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

variable "target_alias_arn" {
  description = "ARN of the scheduler Lambda alias (live)"
  type        = string
}

variable "enabled" {
  description = "Set to false to stop all checks (maintenance)"
  type        = bool
  default     = true
}

resource "aws_scheduler_schedule_group" "this" {
  name = var.name_prefix
}

data "aws_iam_policy_document" "assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["scheduler.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "scheduler" {
  name               = "${var.name_prefix}-scheduler-invoke"
  assume_role_policy = data.aws_iam_policy_document.assume.json
}

resource "aws_iam_role_policy" "invoke" {
  name = "invoke-scheduler-lambda"
  role = aws_iam_role.scheduler.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["lambda:InvokeFunction"]
      Resource = [var.target_alias_arn]
    }]
  })
}

resource "aws_scheduler_schedule" "every_minute" {
  name                = "${var.name_prefix}-every-minute"
  group_name          = aws_scheduler_schedule_group.this.name
  schedule_expression = "rate(1 minute)"
  state               = var.enabled ? "ENABLED" : "DISABLED"
  description         = "Triggers the StatusHub scheduler Lambda every minute"
  flexible_time_window {
    mode = "OFF"
  }

  target {
    arn      = var.target_alias_arn
    role_arn = aws_iam_role.scheduler.arn
    input    = "{}"

    # A missed tick is useless one minute later: do not retry.
    retry_policy {
      maximum_retry_attempts       = 0
      maximum_event_age_in_seconds = 60
    }
  }
}

output "schedule_name" {
  value = aws_scheduler_schedule.every_minute.name
}
