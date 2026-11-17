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

variable "function_name" {
  type = string
}

variable "alias_name" {
  type    = string
  default = "live"
}

variable "alias_invoke_arn" {
  type = string
}

variable "jwt_issuer" {
  type = string
}

variable "jwt_audience" {
  type = string
}

variable "cors_origins" {
  type = list(string)
}

variable "throttling_burst_limit" {
  type    = number
  default = 50
}

variable "throttling_rate_limit" {
  type    = number
  default = 25
}

variable "log_retention_days" {
  type    = number
  default = 14
}

resource "aws_apigatewayv2_api" "this" {
  name          = var.name_prefix
  protocol_type = "HTTP"

  cors_configuration {
    allow_origins = var.cors_origins
    allow_methods = ["GET", "POST", "PUT", "DELETE", "OPTIONS"]
    allow_headers = ["authorization", "content-type"]
    max_age       = 3600
  }
}

resource "aws_apigatewayv2_integration" "lambda" {
  api_id                 = aws_apigatewayv2_api.this.id
  integration_type       = "AWS_PROXY"
  integration_uri        = var.alias_invoke_arn
  payload_format_version = "2.0"
}

resource "aws_apigatewayv2_authorizer" "cognito" {
  api_id           = aws_apigatewayv2_api.this.id
  name             = "cognito-jwt"
  authorizer_type  = "JWT"
  identity_sources = ["$request.header.Authorization"]

  jwt_configuration {
    issuer   = var.jwt_issuer
    audience = [var.jwt_audience]
  }
}

locals {
  target = "integrations/${aws_apigatewayv2_integration.lambda.id}"
}

# Public routes (no JWT): health, the demo target and the public status page. Specific routes win over the proxy.
resource "aws_apigatewayv2_route" "public" {
  for_each = toset(["GET /health", "GET /demo/target", "GET /api/v1/status/{slug}", "GET /openapi/{proxy+}"])

  api_id    = aws_apigatewayv2_api.this.id
  route_key = each.value
  target    = local.target
}

resource "aws_apigatewayv2_route" "private" {
  api_id             = aws_apigatewayv2_api.this.id
  route_key          = "ANY /api/v1/{proxy+}"
  target             = local.target
  authorization_type = "JWT"
  authorizer_id      = aws_apigatewayv2_authorizer.cognito.id
}

resource "aws_cloudwatch_log_group" "access" {
  name              = "/aws/apigateway/${var.name_prefix}-access"
  retention_in_days = var.log_retention_days
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.this.id
  name        = "$default"
  auto_deploy = true

  default_route_settings {
    throttling_burst_limit = var.throttling_burst_limit
    throttling_rate_limit  = var.throttling_rate_limit
  }

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.access.arn
    format = jsonencode({
      requestId      = "$context.requestId"
      ip             = "$context.identity.sourceIp"
      requestTime    = "$context.requestTime"
      routeKey       = "$context.routeKey"
      status         = "$context.status"
      responseLength = "$context.responseLength"
      latencyMs      = "$context.responseLatency"
      integrationMs  = "$context.integrationLatency"
      error          = "$context.error.message"
    })
  }
}

resource "aws_lambda_permission" "invoke" {
  statement_id  = "AllowHttpApiInvoke"
  action        = "lambda:InvokeFunction"
  function_name = var.function_name
  qualifier     = var.alias_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.this.execution_arn}/*/*"
}

output "endpoint" {
  value = aws_apigatewayv2_api.this.api_endpoint
}

output "api_id" {
  value = aws_apigatewayv2_api.this.id
}
