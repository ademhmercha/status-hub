terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

variable "name_prefix" {
  description = "statushub-<env>"
  type        = string
}

variable "enable_pitr" {
  description = "Point-in-time recovery. Off by default: may be refused on a restricted account (see check-free-plan.sh) and costs extra."
  type        = bool
  default     = false
}

variable "deletion_protection" {
  type    = bool
  default = false
}

# Single-table design: see docs/data-model.md.
resource "aws_dynamodb_table" "this" {
  name         = var.name_prefix
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "PK"
  range_key    = "SK"

  attribute {
    name = "PK"
    type = "S"
  }
  attribute {
    name = "SK"
    type = "S"
  }
  attribute {
    name = "GSI1PK"
    type = "S"
  }
  attribute {
    name = "GSI1SK"
    type = "S"
  }
  attribute {
    name = "GSI2PK"
    type = "S"
  }
  attribute {
    name = "GSI2SK"
    type = "S"
  }

  # GSI1: open incidents per account. GSI2: monitors due at a given minute.
  global_secondary_index {
    name            = "GSI1"
    projection_type = "ALL"
    key_schema {
      attribute_name = "GSI1PK"
      key_type       = "HASH"
    }
    key_schema {
      attribute_name = "GSI1SK"
      key_type       = "RANGE"
    }
  }
  global_secondary_index {
    name            = "GSI2"
    projection_type = "ALL"
    key_schema {
      attribute_name = "GSI2PK"
      key_type       = "HASH"
    }
    key_schema {
      attribute_name = "GSI2SK"
      key_type       = "RANGE"
    }
  }

  # Check results expire after 30 days (attribute set by the application).
  ttl {
    attribute_name = "ttl"
    enabled        = true
  }

  point_in_time_recovery {
    enabled = var.enable_pitr
  }

  deletion_protection_enabled = var.deletion_protection
}

output "table_name" {
  value = aws_dynamodb_table.this.name
}

output "table_arn" {
  value = aws_dynamodb_table.this.arn
}
