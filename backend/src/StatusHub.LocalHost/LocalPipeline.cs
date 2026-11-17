using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.LocalHost;

/// <summary>In-process replacement for SQS/EventBridge: a channel per message type.</summary>
public sealed class ChannelPublisher<T> : IMessagePublisher<T>
{
    public Channel<T> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<T>();
    public Task PublishAsync(T message, CancellationToken ct) => Channel.Writer.WriteAsync(message, ct).AsTask();
}

/// <summary>Drains a channel and calls the stage, exactly like a Lambda event source would. Errors are logged, never fatal.</summary>
public sealed class StageWorker<T>(ChannelPublisher<T> source, IServiceScopeFactory scopes, ILogger<StageWorker<T>> log) : BackgroundService
{
    // Like the Lambda event source mapping: checks run concurrently (batch of 10 in parallel, a few batches at once),
    // results are handled one at a time so two results of the same monitor never race.
    private static readonly int Degree = typeof(T) == typeof(CheckMessage) ? 16 : 1;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(source.Channel.Reader.ReadAllAsync(ct),
                new ParallelOptions { MaxDegreeOfParallelism = Degree, CancellationToken = ct },
                async (msg, token) =>
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<IStage<T>>().HandleAsync(msg, token);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        log.LogError(ex, "Local stage {Stage} failed", typeof(T).Name);
                    }
                });
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>Replaces EventBridge Scheduler: fires at the start of every minute, like the real schedule (rate(1 minute)).</summary>
public sealed class SchedulerTicker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SchedulerTicker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                var next = SchedulerTick.Now(clock).Minute.AddMinutes(1);
                await Task.Delay(next - now, clock, ct); // wait for the minute boundary (a tick is never run "late" on purpose)

                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IStage<SchedulerTick>>().HandleAsync(new SchedulerTick(next), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogError(ex, "Local scheduler tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

public static class LocalPipelineExtensions
{
    /// <summary>Registers the 4 stages, in-memory channels and background workers (local mode only).</summary>
    public static IServiceCollection AddLocalPipeline(this IServiceCollection s)
    {
        s.AddSingleton<ChannelPublisher<CheckMessage>>();
        s.AddSingleton<ChannelPublisher<CheckResultMessage>>();
        s.AddSingleton<ChannelPublisher<IncidentEvent>>();
        s.AddSingleton<IMessagePublisher<CheckMessage>>(p => p.GetRequiredService<ChannelPublisher<CheckMessage>>());
        s.AddSingleton<IMessagePublisher<CheckResultMessage>>(p => p.GetRequiredService<ChannelPublisher<CheckResultMessage>>());
        s.AddSingleton<IMessagePublisher<IncidentEvent>>(p => p.GetRequiredService<ChannelPublisher<IncidentEvent>>());
        s.AddSingleton<InMemoryStatusMetrics>();
        s.AddSingleton<IStatusMetrics>(p => p.GetRequiredService<InMemoryStatusMetrics>());
        s.AddStatusHubStages();
        s.AddHostedService<SchedulerTicker>();
        s.AddHostedService<StageWorker<CheckMessage>>();
        s.AddHostedService<StageWorker<CheckResultMessage>>();
        s.AddHostedService<StageWorker<IncidentEvent>>();
        return s;
    }
}
