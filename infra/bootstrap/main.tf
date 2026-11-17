terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
  # Bootstrap keeps a LOCAL state on purpose (it creates the bucket that holds every other state).
  # Keep terraform.tfstate somewhere safe, or migrate it to the bucket afterwards (see infra/README.md).
}

provider "aws" {
  region  = var.region
  profile = var.aws_profile == "" ? null : var.aws_profile

  default_tags {
    tags = {
      Project     = "StatusHub"
      Environment = "shared"
      ManagedBy   = "terraform"
    }
  }
}

data "aws_caller_identity" "current" {}

locals {
  account_id = data.aws_caller_identity.current.account_id
  bucket     = "statushub-tfstate-${local.account_id}"
  repo       = "${var.github_owner}/${var.github_repo}"

  # GitHub environment each deploy role trusts. Restrict "production" to the main branch in the GitHub settings.
  roles = {
    dev  = { env = "dev", github_environment = "dev" }
    prod = { env = "prod", github_environment = "production" }
  }
}

# --- Terraform state bucket ---------------------------------------------------------------------------------

resource "aws_s3_bucket" "state" {
  bucket = local.bucket
}

resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_public_access_block" "state" {
  bucket                  = aws_s3_bucket.state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_lifecycle_configuration" "state" {
  bucket = aws_s3_bucket.state.id
  rule {
    id     = "expire-old-versions"
    status = "Enabled"
    filter {}
    noncurrent_version_expiration {
      noncurrent_days = 90
    }
    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }
}

data "aws_iam_policy_document" "state_bucket" {
  statement {
    sid       = "DenyInsecureTransport"
    effect    = "Deny"
    actions   = ["s3:*"]
    resources = [aws_s3_bucket.state.arn, "${aws_s3_bucket.state.arn}/*"]
    principals {
      type        = "*"
      identifiers = ["*"]
    }
    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "state" {
  bucket = aws_s3_bucket.state.id
  policy = data.aws_iam_policy_document.state_bucket.json

  depends_on = [aws_s3_bucket_public_access_block.state]
}

# --- GitHub OIDC (no AWS key is ever stored in GitHub) ------------------------------------------------------------

resource "aws_iam_openid_connect_provider" "github" {
  count = var.create_oidc_provider ? 1 : 0

  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
  # AWS validates GitHub's certificate chain itself; the thumbprint is required by the API but no longer used.
  thumbprint_list = ["6938fd4d98bab03faadb97b34396831e3780aea1"]
}

data "aws_iam_openid_connect_provider" "github" {
  count = var.create_oidc_provider ? 0 : 1
  url   = "https://token.actions.githubusercontent.com"
}

locals {
  oidc_arn = var.create_oidc_provider ? aws_iam_openid_connect_provider.github[0].arn : data.aws_iam_openid_connect_provider.github[0].arn
}

data "aws_iam_policy_document" "trust" {
  for_each = local.roles

  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    principals {
      type        = "Federated"
      identifiers = [local.oidc_arn]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }
    # Only this repository, only jobs that run in the matching GitHub environment.
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${local.repo}:environment:${each.value.github_environment}"]
    }
  }
}

resource "aws_iam_role" "deploy" {
  for_each = local.roles

  name                 = "statushub-deploy-${each.key}"
  description          = "GitHub Actions deploys StatusHub ${each.key} (repo ${local.repo}, environment ${each.value.github_environment})"
  assume_role_policy   = data.aws_iam_policy_document.trust[each.key].json
  max_session_duration = 3600
}

resource "aws_iam_role_policy" "deploy" {
  for_each = local.roles

  name   = "deploy"
  role   = aws_iam_role.deploy[each.key].id
  policy = data.aws_iam_policy_document.deploy[each.key].json
}

# Read-only role used by pull requests to post `terraform plan` (cannot change anything).
data "aws_iam_policy_document" "trust_plan" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    principals {
      type        = "Federated"
      identifiers = [local.oidc_arn]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${local.repo}:pull_request"]
    }
  }
}

resource "aws_iam_role" "plan" {
  name               = "statushub-plan-dev"
  description        = "Read-only: terraform plan of dev on pull requests of ${local.repo}"
  assume_role_policy = data.aws_iam_policy_document.trust_plan.json
}

resource "aws_iam_role_policy" "plan" {
  name   = "plan"
  role   = aws_iam_role.plan.id
  policy = data.aws_iam_policy_document.plan.json
}
