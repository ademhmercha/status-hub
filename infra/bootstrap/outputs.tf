output "state_bucket" {
  description = "Terraform state bucket (backend.hcl: bucket)"
  value       = aws_s3_bucket.state.bucket
}

output "deploy_role_arns" {
  description = "Put these in the GitHub environment variables (AWS_ROLE_ARN)"
  value       = { for k, r in aws_iam_role.deploy : k => r.arn }
}

output "plan_role_arn" {
  description = "Repository variable AWS_PLAN_ROLE_ARN (pull request plans)"
  value       = aws_iam_role.plan.arn
}

output "account_id" {
  value = local.account_id
}
