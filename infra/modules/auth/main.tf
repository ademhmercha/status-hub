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

variable "callback_urls" {
  description = "Allowed redirect URLs after Google sign-in (hosted UI)"
  type        = list(string)
  default     = ["http://localhost:4200/login"]
}

variable "enable_google" {
  description = "Google sign-in through the Cognito hosted UI. Off by default; needs google_client_id/secret."
  type        = bool
  default     = false
}

variable "google_client_id" {
  type    = string
  default = ""
}

variable "google_client_secret" {
  description = "Pass through TF_VAR_google_client_secret (GitHub environment secret). Never commit it."
  type        = string
  default     = ""
  sensitive   = true
}

variable "domain_prefix" {
  description = "Hosted UI domain prefix (globally unique). Only created when enable_google is true."
  type        = string
  default     = ""
}

variable "deletion_protection" {
  type    = bool
  default = false
}

data "aws_region" "current" {}

resource "aws_cognito_user_pool" "this" {
  name                     = var.name_prefix
  username_attributes      = ["email"]
  auto_verified_attributes = ["email"]
  mfa_configuration        = "OFF"
  deletion_protection      = var.deletion_protection ? "ACTIVE" : "INACTIVE"

  password_policy {
    minimum_length                   = 10
    require_lowercase                = true
    require_uppercase                = true
    require_numbers                  = true
    require_symbols                  = false
    temporary_password_validity_days = 7
  }

  account_recovery_setting {
    recovery_mechanism {
      name     = "verified_email"
      priority = 1
    }
  }

  verification_message_template {
    default_email_option = "CONFIRM_WITH_CODE"
  }

  schema {
    name                = "email"
    attribute_data_type = "String"
    required            = true
    mutable             = true
    string_attribute_constraints {
      min_length = 5
      max_length = 254
    }
  }
}

resource "aws_cognito_identity_provider" "google" {
  count = var.enable_google ? 1 : 0

  user_pool_id  = aws_cognito_user_pool.this.id
  provider_name = "Google"
  provider_type = "Google"

  provider_details = {
    client_id        = var.google_client_id
    client_secret    = var.google_client_secret
    authorize_scopes = "openid email profile"
  }
  attribute_mapping = {
    email    = "email"
    username = "sub"
  }
}

resource "aws_cognito_user_pool_client" "web" {
  name         = "${var.name_prefix}-web"
  user_pool_id = aws_cognito_user_pool.this.id

  # Public client (browser): no secret.
  generate_secret = false

  explicit_auth_flows = ["ALLOW_USER_PASSWORD_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"]

  prevent_user_existence_errors = "ENABLED"
  enable_token_revocation       = true

  access_token_validity  = 60
  id_token_validity      = 60
  refresh_token_validity = 30
  token_validity_units {
    access_token  = "minutes"
    id_token      = "minutes"
    refresh_token = "days"
  }

  supported_identity_providers         = var.enable_google ? ["COGNITO", "Google"] : ["COGNITO"]
  allowed_oauth_flows_user_pool_client = var.enable_google
  allowed_oauth_flows                  = var.enable_google ? ["implicit"] : []
  allowed_oauth_scopes                 = var.enable_google ? ["openid", "email", "profile"] : []
  callback_urls                        = var.enable_google ? var.callback_urls : []
  logout_urls                          = var.enable_google ? var.callback_urls : []

  depends_on = [aws_cognito_identity_provider.google]
}

resource "aws_cognito_user_pool_domain" "this" {
  count = var.enable_google && var.domain_prefix != "" ? 1 : 0

  domain       = var.domain_prefix
  user_pool_id = aws_cognito_user_pool.this.id
}

output "user_pool_id" {
  value = aws_cognito_user_pool.this.id
}

output "client_id" {
  value = aws_cognito_user_pool_client.web.id
}

output "issuer" {
  value = "https://cognito-idp.${data.aws_region.current.region}.amazonaws.com/${aws_cognito_user_pool.this.id}"
}

output "hosted_ui_domain" {
  description = "Empty when Google sign-in is disabled"
  value       = length(aws_cognito_user_pool_domain.this) == 0 ? "" : "${var.domain_prefix}.auth.${data.aws_region.current.region}.amazoncognito.com"
}
