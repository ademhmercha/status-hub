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

variable "artifacts_dir" {
  description = "Directory holding api.zip, scheduler.zip, checker.zip, evaluator.zip and notifier.zip (built once, deployed to every environment)."
  type        = string
}

variable "table_name" {
  type = string
}

variable "table_arn" {
  type = string
}

variable "queue_urls" {
  type = map(string)
}

variable "queue_arns" {
  type = map(string)
}

variable "bus_name" {
  type = string
}

variable "bus_arn" {
  type = string
}

variable "ses_identity_arn" {
  description = "Empty when SES is disabled: the functions then get no ses:SendEmail permission."
  type        = string
  default     = ""
}

variable "email_from" {
  type    = string
  default = ""
}

variable "enable_xray" {
  type    = bool
  default = true
}

variable "log_retention_days" {
  type    = number
  default = 14
}

variable "auth_authority" {
  type = string
}

variable "auth_audience" {
  type = string
}

variable "cors_origins" {
  description = "Comma separated list of allowed browser origins"
  type        = string
}

variable "demo_enabled" {
  type    = bool
  default = true
}

variable "checker_max_concurrency" {
  description = "Max parallel Checker invocations (min 2). Lower it if the account Lambda concurrency quota is small."
  type        = number
  default     = 5
}

variable "metrics_namespace" {
  description = "CloudWatch namespace for the EMF business metrics (POWERTOOLS_METRICS_NAMESPACE)"
  type        = string
  default     = "StatusHub"
}

variable "batching_window_seconds" {
  description = "SQS batching window for the Checker and Evaluator: fewer, fuller batches = fewer SQS and Lambda requests (cost). A check waits at most this long in the queue."
  type        = number
  default     = 2
}

variable "extra_environment" {
  description = "Extra environment variables for every function (e.g. POWERTOOLS_*)"
  type        = map(string)
  default     = {}
}

locals {
  dotnet_handler = { for f in ["Scheduler", "Evaluator", "Notifier"] : lower(f) => "StatusHub.Functions.${f}::StatusHub.Functions.${f}.Function::HandleAsync" }

  base_env = merge({
    POWERTOOLS_METRICS_NAMESPACE = var.metrics_namespace
    POWERTOOLS_LOG_LEVEL         = "Information"
    DOTNET_TieredPGO             = "0"
  }, var.extra_environment)

  email_env = var.email_from == "" ? {} : { Email__Provider = "Ses", Email__From = var.email_from }

  functions = {
    api = {
      runtime = "dotnet10"
      arch    = "arm64"
      handler = "StatusHub.Api"
      memory  = 512
      timeout = 15
      env = merge(local.base_env, local.email_env, {
        ASPNETCORE_ENVIRONMENT       = "Production"
        DynamoDb__TableName          = var.table_name
        Auth__Authority              = var.auth_authority
        Auth__Audience               = var.auth_audience
        Cors__Origins                = var.cors_origins
        Demo__Enabled                = tostring(var.demo_enabled)
        Checker__AllowPrivateTargets = "false"
      })
    }
    scheduler = {
      runtime = "dotnet10"
      arch    = "arm64"
      handler = local.dotnet_handler["scheduler"]
      memory  = 256
      timeout = 30
      env = merge(local.base_env, {
        DynamoDb__TableName = var.table_name
        Queues__Checks      = var.queue_urls["checks"]
      })
    }
    checker = {
      runtime = "provided.al2023" # Native AOT custom runtime
      arch    = "x86_64"          # built by the Amazon Linux 2023 x64 container (build-tools/aot)
      handler = "bootstrap"
      memory  = 256
      timeout = 45
      env = merge(local.base_env, {
        Queues__Results              = var.queue_urls["results"]
        Checker__AllowPrivateTargets = "false"
      })
    }
    evaluator = {
      runtime = "dotnet10"
      arch    = "arm64"
      handler = local.dotnet_handler["evaluator"]
      memory  = 256
      timeout = 30
      env = merge(local.base_env, {
        DynamoDb__TableName = var.table_name
        EventBus__Name      = var.bus_name
      })
    }
    notifier = {
      runtime = "dotnet10"
      arch    = "arm64"
      handler = local.dotnet_handler["notifier"]
      memory  = 256
      timeout = 120
      env = merge(local.base_env, local.email_env, {
        DynamoDb__TableName          = var.table_name
        Checker__AllowPrivateTargets = "false"
      })
    }
  }

  # --- least-privilege IAM, one role per function --------------------------------------------------------------
  table_and_indexes = [var.table_arn, "${var.table_arn}/index/*"]

  xray_statements = var.enable_xray ? [{
    Effect   = "Allow"
    Action   = ["xray:PutTraceSegments", "xray:PutTelemetryRecords"]
    Resource = "*" # X-Ray write actions do not support resource-level permissions
  }] : []

  ses_statements = var.ses_identity_arn == "" ? [] : [{
    Effect   = "Allow"
    Action   = ["ses:SendEmail"]
    Resource = var.ses_identity_arn
  }]

  consume = { for q in ["checks", "results", "notifier"] : q => {
    Effect   = "Allow"
    Action   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes", "sqs:ChangeMessageVisibility"]
    Resource = var.queue_arns[q]
  } }

  statements = {
    api = concat(local.xray_statements, local.ses_statements, [{
      Effect   = "Allow"
      Action   = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem", "dynamodb:Query", "dynamodb:BatchGetItem"]
      Resource = local.table_and_indexes
    }])

    scheduler = concat(local.xray_statements, [
      {
        Effect   = "Allow"
        Action   = ["dynamodb:Query"]
        Resource = ["${var.table_arn}/index/GSI2"]
      },
      {
        # Idempotency keys only (item keys starting with IDEM#)
        Effect    = "Allow"
        Action    = ["dynamodb:PutItem", "dynamodb:DeleteItem"]
        Resource  = [var.table_arn]
        Condition = { "ForAllValues:StringLike" = { "dynamodb:LeadingKeys" = ["IDEM#*"] } }
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = var.queue_arns["checks"]
      },
    ])

    checker = concat(local.xray_statements, [
      local.consume["checks"],
      {
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = var.queue_arns["results"]
      },
    ])

    evaluator = concat(local.xray_statements, [
      local.consume["results"],
      {
        Effect   = "Allow"
        Action   = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem", "dynamodb:Query"]
        Resource = local.table_and_indexes
      },
      {
        Effect   = "Allow"
        Action   = ["events:PutEvents"]
        Resource = var.bus_arn
      },
    ])

    notifier = concat(local.xray_statements, local.ses_statements, [
      local.consume["notifier"],
      {
        Effect   = "Allow"
        Action   = ["dynamodb:Query"]
        Resource = [var.table_arn]
      },
    ])
  }

  mappings = {
    checker   = { queue = "checks", batch_size = 10, batching_window = var.batching_window_seconds, max_concurrency = var.checker_max_concurrency }
    evaluator = { queue = "results", batch_size = 10, batching_window = var.batching_window_seconds, max_concurrency = 2 }
    notifier  = { queue = "notifier", batch_size = 1, batching_window = 0, max_concurrency = 2 }
  }
}

data "aws_iam_policy_document" "assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_cloudwatch_log_group" "this" {
  for_each = local.functions

  name              = "/aws/lambda/${var.name_prefix}-${each.key}"
  retention_in_days = var.log_retention_days
}

resource "aws_iam_role" "this" {
  for_each = local.functions

  name               = "${var.name_prefix}-${each.key}"
  assume_role_policy = data.aws_iam_policy_document.assume.json
}

resource "aws_iam_role_policy" "this" {
  for_each = local.functions

  name = "least-privilege"
  role = aws_iam_role.this[each.key].id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat([{
      Effect   = "Allow"
      Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
      Resource = "${aws_cloudwatch_log_group.this[each.key].arn}:*"
    }], local.statements[each.key])
  })
}

resource "aws_lambda_function" "this" {
  for_each = local.functions

  function_name = "${var.name_prefix}-${each.key}"
  role          = aws_iam_role.this[each.key].arn
  runtime       = each.value.runtime
  handler       = each.value.handler
  architectures = [each.value.arch]
  memory_size   = each.value.memory
  timeout       = each.value.timeout

  filename         = "${var.artifacts_dir}/${each.key}.zip"
  source_code_hash = filebase64sha256("${var.artifacts_dir}/${each.key}.zip")

  # Every deployment publishes an immutable version; the "live" alias below is what everything invokes.
  publish = true

  tracing_config {
    mode = var.enable_xray ? "Active" : "PassThrough"
  }

  logging_config {
    log_format = "JSON"
    log_group  = aws_cloudwatch_log_group.this[each.key].name
  }

  environment {
    # POWERTOOLS_SERVICE_NAME = the "Service" dimension of the metrics and the service field of every log line
    variables = merge(each.value.env, { POWERTOOLS_SERVICE_NAME = each.key })
  }

  depends_on = [aws_iam_role_policy.this]
}

# Rollback in one command: aws lambda update-alias --function-name <fn> --name live --function-version <previous>
resource "aws_lambda_alias" "live" {
  for_each = local.functions

  name             = "live"
  function_name    = aws_lambda_function.this[each.key].function_name
  function_version = aws_lambda_function.this[each.key].version
}

resource "aws_lambda_event_source_mapping" "this" {
  for_each = local.mappings

  event_source_arn                   = var.queue_arns[each.value.queue]
  function_name                      = aws_lambda_alias.live[each.key].arn
  batch_size                         = each.value.batch_size
  maximum_batching_window_in_seconds = each.value.batching_window
  function_response_types            = ["ReportBatchItemFailures"]

  scaling_config {
    maximum_concurrency = each.value.max_concurrency
  }
}

output "function_names" {
  value = { for k, f in aws_lambda_function.this : k => f.function_name }
}

output "alias_arns" {
  value = { for k, a in aws_lambda_alias.live : k => a.arn }
}

output "alias_invoke_arns" {
  value = { for k, a in aws_lambda_alias.live : k => a.invoke_arn }
}

output "alias_names" {
  value = { for k, a in aws_lambda_alias.live : k => a.name }
}

output "versions" {
  value = { for k, f in aws_lambda_function.this : k => f.version }
}

output "log_group_names" {
  value = { for k, g in aws_cloudwatch_log_group.this : k => g.name }
}
