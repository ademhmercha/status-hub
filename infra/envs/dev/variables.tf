variable "aws_profile" {
  description = "Local AWS CLI profile. CI sets it to an empty string (OIDC credentials)."
  type        = string
  default     = "teamchat"
}

variable "artifacts_dir" {
  description = "Directory with api.zip, scheduler.zip, checker.zip, evaluator.zip, notifier.zip"
  type        = string
  default     = "../../../build/lambda"
}

variable "sender_email" {
  description = "SES sender for alert emails (SES sends a verification link to it)"
  type        = string
  default     = ""
}

# Optional features. If check-free-plan.sh reports REFUSE for one of them, set it to false in terraform.tfvars.
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
  description = "Use TF_VAR_google_client_secret. Never put it in a file."
  type        = string
  default     = ""
  sensitive   = true
}

variable "schedule_enabled" {
  type    = bool
  default = true
}

variable "checker_max_concurrency" {
  type    = number
  default = 5
}
