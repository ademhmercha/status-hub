# Least-privilege policies of the deploy roles: every statement is limited to resources named statushub-<env>-*.
# Some APIs do not support resource-level permissions (noted below). After the first real apply, review CloudTrail
# for AccessDenied events and adjust here rather than widening to "*".

locals {
  region = var.region
  acct   = local.account_id
}

data "aws_iam_policy_document" "deploy" {
  for_each = local.roles

  # Terraform state of this environment only (S3 native locking writes a .tflock object).
  statement {
    sid       = "StateList"
    actions   = ["s3:ListBucket", "s3:GetBucketVersioning"]
    resources = [aws_s3_bucket.state.arn]
    condition {
      test     = "StringLike"
      variable = "s3:prefix"
      values   = ["envs/${each.value.env}/*", "envs/${each.value.env}"]
    }
  }
  statement {
    sid       = "StateObjects"
    actions   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.state.arn}/envs/${each.value.env}/*"]
  }

  statement {
    sid = "DynamoDbTable"
    actions = [
      "dynamodb:CreateTable", "dynamodb:DeleteTable", "dynamodb:DescribeTable", "dynamodb:UpdateTable",
      "dynamodb:DescribeContinuousBackups", "dynamodb:UpdateContinuousBackups",
      "dynamodb:DescribeTimeToLive", "dynamodb:UpdateTimeToLive",
      "dynamodb:ListTagsOfResource", "dynamodb:TagResource", "dynamodb:UntagResource",
    ]
    resources = [
      "arn:aws:dynamodb:${local.region}:${local.acct}:table/statushub-${each.value.env}",
      "arn:aws:dynamodb:${local.region}:${local.acct}:table/statushub-${each.value.env}/index/*",
    ]
  }
  statement {
    sid       = "DynamoDbAccount"
    actions   = ["dynamodb:DescribeLimits"]
    resources = ["*"] # no resource-level support
  }

  statement {
    sid       = "Sqs"
    actions   = ["sqs:*"]
    resources = ["arn:aws:sqs:${local.region}:${local.acct}:statushub-${each.value.env}-*"]
  }

  statement {
    sid       = "LambdaFunctions"
    actions   = ["lambda:*"]
    resources = ["arn:aws:lambda:${local.region}:${local.acct}:function:statushub-${each.value.env}-*"]
  }
  statement {
    sid       = "LambdaEventSourceMappings"
    actions   = ["lambda:CreateEventSourceMapping", "lambda:UpdateEventSourceMapping", "lambda:DeleteEventSourceMapping", "lambda:GetEventSourceMapping", "lambda:ListEventSourceMappings", "lambda:GetAccountSettings"]
    resources = ["*"] # mappings are identified by UUID
  }

  statement {
    sid = "IamRolesOfTheStack"
    actions = [
      "iam:CreateRole", "iam:DeleteRole", "iam:GetRole", "iam:UpdateRole", "iam:UpdateAssumeRolePolicy",
      "iam:PutRolePolicy", "iam:GetRolePolicy", "iam:DeleteRolePolicy", "iam:ListRolePolicies",
      "iam:ListAttachedRolePolicies", "iam:ListInstanceProfilesForRole", "iam:TagRole", "iam:UntagRole", "iam:ListRoleTags",
    ]
    resources = ["arn:aws:iam::${local.acct}:role/statushub-${each.value.env}-*"]
  }
  statement {
    sid       = "IamPassRoleToServices"
    actions   = ["iam:PassRole"]
    resources = ["arn:aws:iam::${local.acct}:role/statushub-${each.value.env}-*"]
    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["lambda.amazonaws.com", "scheduler.amazonaws.com", "events.amazonaws.com"]
    }
  }

  statement {
    sid       = "ApiGatewayHttpApis"
    actions   = ["apigateway:GET", "apigateway:POST", "apigateway:PUT", "apigateway:PATCH", "apigateway:DELETE"]
    resources = ["arn:aws:apigateway:${local.region}::/apis", "arn:aws:apigateway:${local.region}::/apis/*", "arn:aws:apigateway:${local.region}::/tags/*"]
  }

  statement {
    sid     = "EventBridge"
    actions = ["events:*"]
    resources = [
      "arn:aws:events:${local.region}:${local.acct}:event-bus/statushub-${each.value.env}",
      "arn:aws:events:${local.region}:${local.acct}:rule/statushub-${each.value.env}/*",
    ]
  }
  statement {
    sid     = "EventBridgeScheduler"
    actions = ["scheduler:*"]
    resources = [
      "arn:aws:scheduler:${local.region}:${local.acct}:schedule-group/statushub-${each.value.env}",
      "arn:aws:scheduler:${local.region}:${local.acct}:schedule/statushub-${each.value.env}/*",
    ]
  }

  statement {
    sid       = "CognitoCreate"
    actions   = ["cognito-idp:CreateUserPool"]
    resources = ["*"] # the pool id does not exist before creation
  }
  statement {
    sid       = "CognitoUserPools"
    actions   = ["cognito-idp:*"]
    resources = ["arn:aws:cognito-idp:${local.region}:${local.acct}:userpool/*"]
  }

  statement {
    sid       = "SesIdentities"
    actions   = ["ses:CreateEmailIdentity", "ses:DeleteEmailIdentity", "ses:GetEmailIdentity", "ses:TagResource", "ses:UntagResource", "ses:ListTagsForResource", "ses:PutEmailIdentityDkimAttributes", "ses:PutEmailIdentityMailFromAttributes"]
    resources = ["arn:aws:ses:${local.region}:${local.acct}:identity/*"]
  }

  statement {
    sid       = "WebBucket"
    actions   = ["s3:*"]
    resources = ["arn:aws:s3:::statushub-${each.value.env}-web-${local.acct}", "arn:aws:s3:::statushub-${each.value.env}-web-${local.acct}/*"]
  }

  statement {
    sid = "CloudFront"
    actions = [
      "cloudfront:CreateDistribution", "cloudfront:UpdateDistribution", "cloudfront:DeleteDistribution", "cloudfront:GetDistribution",
      "cloudfront:TagResource", "cloudfront:UntagResource", "cloudfront:ListTagsForResource", "cloudfront:CreateInvalidation",
      "cloudfront:GetInvalidation", "cloudfront:ListDistributions",
      "cloudfront:CreateOriginAccessControl", "cloudfront:UpdateOriginAccessControl", "cloudfront:DeleteOriginAccessControl",
      "cloudfront:GetOriginAccessControl", "cloudfront:ListOriginAccessControls",
      "cloudfront:GetResponseHeadersPolicy", "cloudfront:GetCachePolicy",
    ]
    resources = ["*"] # CloudFront has very limited resource-level permissions
  }

  statement {
    sid     = "Logs"
    actions = ["logs:CreateLogGroup", "logs:DeleteLogGroup", "logs:PutRetentionPolicy", "logs:DeleteRetentionPolicy", "logs:ListTagsForResource", "logs:ListTagsLogGroup", "logs:TagResource", "logs:UntagResource", "logs:TagLogGroup"]
    resources = [
      "arn:aws:logs:${local.region}:${local.acct}:log-group:/aws/lambda/statushub-${each.value.env}-*",
      "arn:aws:logs:${local.region}:${local.acct}:log-group:/aws/lambda/statushub-${each.value.env}-*:*",
      "arn:aws:logs:${local.region}:${local.acct}:log-group:/aws/apigateway/statushub-${each.value.env}-*",
      "arn:aws:logs:${local.region}:${local.acct}:log-group:/aws/apigateway/statushub-${each.value.env}-*:*",
    ]
  }
  statement {
    sid       = "LogsDescribe"
    actions   = ["logs:DescribeLogGroups"]
    resources = ["*"]
  }

  statement {
    sid     = "CloudWatch"
    actions = ["cloudwatch:PutMetricAlarm", "cloudwatch:DeleteAlarms", "cloudwatch:DescribeAlarms", "cloudwatch:TagResource", "cloudwatch:UntagResource", "cloudwatch:ListTagsForResource", "cloudwatch:PutDashboard", "cloudwatch:DeleteDashboards", "cloudwatch:GetDashboard"]
    resources = [
      "arn:aws:cloudwatch:${local.region}:${local.acct}:alarm:statushub-${each.value.env}-*",
      "arn:aws:cloudwatch::${local.acct}:dashboard/statushub-${each.value.env}",
    ]
  }
  statement {
    sid       = "CloudWatchList"
    actions   = ["cloudwatch:ListDashboards", "cloudwatch:DescribeAlarms"]
    resources = ["*"]
  }

  statement {
    sid       = "Sns"
    actions   = ["sns:*"]
    resources = ["arn:aws:sns:${local.region}:${local.acct}:statushub-${each.value.env}-*"]
  }

  statement {
    sid       = "Identity"
    actions   = ["sts:GetCallerIdentity", "tag:GetResources"]
    resources = ["*"]
  }
}

# Pull request plans: metadata only (no GetItem/GetObject on data, no write, except the state lock file).
data "aws_iam_policy_document" "plan" {
  statement {
    sid       = "StateRead"
    actions   = ["s3:ListBucket", "s3:GetBucketVersioning"]
    resources = [aws_s3_bucket.state.arn]
  }
  statement {
    sid       = "StateObjectRead"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.state.arn}/envs/dev/*"]
  }
  statement {
    sid       = "StateLock"
    actions   = ["s3:PutObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.state.arn}/envs/dev/*.tflock"]
  }
  statement {
    sid = "ReadMetadata"
    actions = [
      "dynamodb:Describe*", "dynamodb:ListTagsOfResource",
      "sqs:GetQueueAttributes", "sqs:GetQueueUrl", "sqs:ListQueues", "sqs:ListQueueTags", "sqs:ListDeadLetterSourceQueues",
      "lambda:Get*", "lambda:List*",
      "iam:GetRole", "iam:GetRolePolicy", "iam:ListRolePolicies", "iam:ListAttachedRolePolicies", "iam:ListRoleTags", "iam:ListInstanceProfilesForRole",
      "apigateway:GET",
      "events:Describe*", "events:List*", "scheduler:Get*", "scheduler:List*",
      "cognito-idp:Describe*", "cognito-idp:Get*", "cognito-idp:List*",
      "ses:GetEmailIdentity", "ses:ListTagsForResource",
      "s3:GetBucket*", "s3:GetEncryptionConfiguration", "s3:GetLifecycleConfiguration", "s3:GetAccelerateConfiguration", "s3:GetReplicationConfiguration", "s3:GetBucketPolicy", "s3:ListBucket",
      "cloudfront:Get*", "cloudfront:List*",
      "logs:DescribeLogGroups", "logs:ListTagsForResource", "logs:ListTagsLogGroup",
      "cloudwatch:Describe*", "cloudwatch:GetDashboard", "cloudwatch:ListDashboards", "cloudwatch:ListTagsForResource",
      "sns:Get*", "sns:List*",
      "sts:GetCallerIdentity", "tag:GetResources",
    ]
    resources = ["*"]
  }
}
