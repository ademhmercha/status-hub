output "api_endpoint" {
  value = module.stack.api_endpoint
}

output "frontend_url" {
  value = module.stack.frontend_url
}

output "web_bucket" {
  value = module.stack.web_bucket
}

output "cloudfront_distribution_id" {
  value = module.stack.cloudfront_distribution_id
}

output "user_pool_id" {
  value = module.stack.user_pool_id
}

output "user_pool_client_id" {
  value = module.stack.user_pool_client_id
}

output "function_names" {
  value = module.stack.function_names
}

output "queue_urls" {
  value = module.stack.queue_urls
}

output "dlq_urls" {
  value = module.stack.dlq_urls
}

output "table_name" {
  value = module.stack.table_name
}

output "dashboard_name" {
  value = module.stack.dashboard_name
}

output "alarm_topic_arn" {
  value = module.stack.alarm_topic_arn
}
