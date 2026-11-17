using Amazon.XRay.Recorder.Core;
using Amazon.XRay.Recorder.Core.Strategies;
using AWS.Lambda.Powertools.Tracing;

namespace StatusHub.Observability;

/// <summary>
/// X-Ray subsegments for every AWS SDK call (DynamoDB, SQS, EventBridge, SES). Compiled into the JIT Lambdas and the API only:
/// the X-Ray SDK is not Native AOT compatible, the AOT Checker relies on trace-header propagation instead.
/// </summary>
internal static class TracingSetup
{
    public static void Register()
    {
        if (Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") is null) return; // local runs
        AWSXRayRecorder.Instance.ContextMissingStrategy = ContextMissingStrategy.LOG_ERROR; // tracing may be disabled (enable_xray=false)
        Tracing.RegisterForAllServices();
    }
}
