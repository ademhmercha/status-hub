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
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var msg in source.Channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IStage<T>>().HandleAsync(msg, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Local stage {Stage} failed", typeof(T).Name);
            }
        }
    }
}

/// <summary>Replaces EventBridge Scheduler: ticks every minute (and once at startup).</summary>
public sealed class SchedulerTicker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SchedulerTicker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IStage<SchedulerTick>>().HandleAsync(SchedulerTick.Now(clock), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Local scheduler tick failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
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
        s.AddStatusHubStages();
        s.AddHostedService<SchedulerTicker>();
        s.AddHostedService<StageWorker<CheckMessage>>();
        s.AddHostedService<StageWorker<CheckResultMessage>>();
        s.AddHostedService<StageWorker<IncidentEvent>>();
        return s;
    }
}
