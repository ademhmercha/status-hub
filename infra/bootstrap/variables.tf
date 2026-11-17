variable "region" {
  type    = string
  default = "eu-north-1"
}

variable "aws_profile" {
  description = "Local AWS CLI profile. Leave empty in CI."
  type        = string
  default     = "teamchat"
}

variable "github_owner" {
  type    = string
  default = "ademhmercha"
}

variable "github_repo" {
  type    = string
  default = "status-hub"
}

variable "create_oidc_provider" {
  description = "Set to false if the account already has the GitHub OIDC provider (only one can exist per account)."
  type        = bool
  default     = true
}
