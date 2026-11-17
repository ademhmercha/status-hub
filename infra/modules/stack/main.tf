terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

# One whole StatusHub environment. envs/dev and envs/prod only differ by the values they pass here.

variable "environment" {
  type = string
}

variable "artifacts_dir" {
  description = "Directory with the Lambda zips (api, scheduler, checker, evaluator, notifier)."
  type        = string
}

variable "sender_email" {
  description = "SES sender address for alert emails"
  type        = string
  default     = ""
}

# --- optional features: turn off what your account refuses (see infra/scripts/check-free-plan.sh) -------------------
variable "enable_ses" {
  type    = bool
  default = true
}

variable "enable_xray" {
  type    = bool
  default = true
}

variable "enable_pitr" {
  type    = bool
  default = false
}

variable "enable_cloudfront" {
  type    = bool
  default = true
}

variable "enable_google" {
  type    = bool
  default = false
}

variable "google_client_id" {
  type    = string
  default = ""
}

variable "google_client_secret" {
  type      = string
  default   = ""
  sensitive = true
}

variable "schedule_enabled" {
  type    = bool
  default = true
}

variable "deletion_protection" {
  type    = bool
  default = false
}

variable "force_destroy_web" {
  type    = bool
  default = false
}

variable "extra_cors_origins" {
  type    = list(string)
  default = []
}

variable "log_retention_days" {
  type    = number
  default = 14
}

variable "checker_max_concurrency" {
  type    = number
  default = 5
}

locals {
  name_prefix = "statushub-${var.environment}"
  use_ses     = var.enable_ses && var.sender_email != ""
  web_origin  = var.enable_cloudfront ? ["https://${module.web[0].domain_name}"] : []
  cors        = distinct(concat(local.web_origin, var.extra_cors_origins))
}

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

module "data" {
  source = "../data"

  name_prefix         = local.name_prefix
  enable_pitr         = var.enable_pitr
  deletion_protection = var.deletion_protection
}

module "messaging" {
  source = "../messaging"

  name_prefix = local.name_prefix
}

module "email" {
  count  = local.use_ses ? 1 : 0
  source = "../email"

  sender_email = var.sender_email
}

module "web" {
  count  = var.enable_cloudfront ? 1 : 0
  source = "../web"

  name_prefix   = local.name_prefix
  force_destroy = var.force_destroy_web
}

module "auth" {
  source = "../auth"

  name_prefix          = local.name_prefix
  deletion_protection  = var.deletion_protection
  enable_google        = var.enable_google
  google_client_id     = var.google_client_id
  google_client_secret = var.google_client_secret
  domain_prefix        = "${local.name_prefix}-${data.aws_caller_identity.current.account_id}"
  callback_urls        = distinct(concat(["http://localhost:4200/login"], [for o in local.web_origin : "${o}/login"]))
}

module "functions" {
  source = "../functions"

  name_prefix             = local.name_prefix
  artifacts_dir           = var.artifacts_dir
  table_name              = module.data.table_name
  table_arn               = module.data.table_arn
  queue_urls              = module.messaging.queue_urls
  queue_arns              = module.messaging.queue_arns
  bus_name                = module.messaging.bus_name
  bus_arn                 = module.messaging.bus_arn
  ses_identity_arn        = local.use_ses ? module.email[0].identity_arn : ""
  email_from              = local.use_ses ? var.sender_email : ""
  enable_xray             = var.enable_xray
  log_retention_days      = var.log_retention_days
  auth_authority          = module.auth.issuer
  auth_audience           = module.auth.client_id
  cors_origins            = join(",", local.cors)
  checker_max_concurrency = var.checker_max_concurrency
}

module "api" {
  source = "../api"

  name_prefix        = local.name_prefix
  function_name      = module.functions.function_names["api"]
  alias_name         = module.functions.alias_names["api"]
  alias_invoke_arn   = module.functions.alias_invoke_arns["api"]
  jwt_issuer         = module.auth.issuer
  jwt_audience       = module.auth.client_id
  cors_origins       = length(local.cors) == 0 ? ["http://localhost:4200"] : local.cors
  log_retention_days = var.log_retention_days
}

module "scheduler" {
  source = "../scheduler"

  name_prefix      = local.name_prefix
  target_alias_arn = module.functions.alias_arns["scheduler"]
  enabled          = var.schedule_enabled
}

# Runtime configuration read by the Angular app (public/config.json). Created here so no CI templating is needed.
resource "aws_s3_object" "frontend_config" {
  count = var.enable_cloudfront ? 1 : 0

  bucket        = module.web[0].bucket
  key           = "config.json"
  content_type  = "application/json"
  cache_control = "no-cache"
  content = jsonencode({
    apiBaseUrl = module.api.endpoint
    auth = {
      mode     = "cognito"
      region   = data.aws_region.current.region
      clientId = module.auth.client_id
      domain   = module.auth.hosted_ui_domain
    }
  })
}

output "api_endpoint" {
  value = module.api.endpoint
}

output "frontend_url" {
  value = var.enable_cloudfront ? "https://${module.web[0].domain_name}" : ""
}

output "web_bucket" {
  value = var.enable_cloudfront ? module.web[0].bucket : ""
}

output "cloudfront_distribution_id" {
  value = var.enable_cloudfront ? module.web[0].distribution_id : ""
}

output "user_pool_id" {
  value = module.auth.user_pool_id
}

output "user_pool_client_id" {
  value = module.auth.client_id
}

output "function_names" {
  value = module.functions.function_names
}

output "queue_urls" {
  value = module.messaging.queue_urls
}

output "dlq_urls" {
  value = module.messaging.dlq_urls
}

output "table_name" {
  value = module.data.table_name
}
