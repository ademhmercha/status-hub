terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # Values come from backend.hcl (local) or -backend-config flags (CI). See backend.hcl.example.
  backend "s3" {
    key          = "envs/prod/terraform.tfstate"
    use_lockfile = true
    encrypt      = true
  }
}

provider "aws" {
  region  = "eu-north-1"
  profile = var.aws_profile == "" ? null : var.aws_profile

  default_tags {
    tags = {
      Project     = "StatusHub"
      Environment = "prod"
      ManagedBy   = "terraform"
    }
  }
}

module "stack" {
  source = "../../modules/stack"

  environment   = "prod"
  artifacts_dir = var.artifacts_dir
  sender_email  = var.sender_email

  enable_ses        = var.enable_ses
  enable_xray       = var.enable_xray
  enable_pitr       = var.enable_pitr
  enable_cloudfront = var.enable_cloudfront

  enable_google        = var.enable_google
  google_client_id     = var.google_client_id
  google_client_secret = var.google_client_secret

  enable_observability = var.enable_observability
  alarm_email          = var.alarm_email

  schedule_enabled        = var.schedule_enabled
  checker_max_concurrency = var.checker_max_concurrency
  extra_cors_origins      = []

  # prod: protected against accidental deletion
  deletion_protection = true
  force_destroy_web   = false
}
