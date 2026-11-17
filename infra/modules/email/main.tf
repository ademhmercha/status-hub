terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

variable "sender_email" {
  description = "Address alerts are sent from. SES emails you a link to verify it after the first apply."
  type        = string
}

# SES accounts start in the sandbox: you can only send to verified addresses until you request production access.
resource "aws_sesv2_email_identity" "sender" {
  email_identity = var.sender_email
}

output "identity_arn" {
  value = aws_sesv2_email_identity.sender.arn
}

output "sender_email" {
  value = var.sender_email
}
