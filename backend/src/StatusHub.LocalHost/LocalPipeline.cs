using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.LocalHost;

/// <summary>In-process replacement for SQS/EventBridge: a channel per message type.</summary>
public sealed class ChannelPublisher<T> : IMessagePublisher<T>
{
    public Channel<T> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<T>();
    public Task PublishAsync(T message, CancellationToken ct) => Channel.Writer.WriteAsync(message, ct).AsTask();
}

/// <summary>Drains a channel and calls the stage, exactly like a Lambda event source would.</summary>
public sealed class StageWorker<T>(ChannelPublisher<T> source, IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var msg in source.Channel.Reader.ReadAllAsync(ct))
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IStage<T>>().HandleAsync(msg, ct);
        }
    }
}

/// <summary>Replaces EventBridge Scheduler: ticks every minute.</summary>
public sealed class SchedulerTicker(IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var now = DateTimeOffset.UtcNow;
            var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IStage<SchedulerTick>>().HandleAsync(new SchedulerTick(minute), ct);
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

public static class LocalPipelineExtensions
{
    /// <summary>Registers the 4 stages + channels + background workers (local mode only).</summary>
    public static IServiceCollection AddLocalPipeline(this IServiceCollection s)
    {
        s.AddSingleton<ChannelPublisher<CheckMessage>>();
        s.AddSingleton<ChannelPublisher<CheckResultMessage>>();
        s.AddSingleton<ChannelPublisher<IncidentEvent>>();
        s.AddSingleton<IMessagePublisher<CheckMessage>>(p => p.GetRequiredService<ChannelPublisher<CheckMessage>>());
        s.AddSingleton<IMessagePublisher<CheckResultMessage>>(p => p.GetRequiredService<ChannelPublisher<CheckResultMessage>>());
        s.AddSingleton<IMessagePublisher<IncidentEvent>>(p => p.GetRequiredService<ChannelPublisher<IncidentEvent>>());
        s.AddScoped<IStage<SchedulerTick>, SchedulerStage>();
        s.AddScoped<IStage<CheckMessage>, CheckerStage>();
        s.AddScoped<IStage<CheckResultMessage>, EvaluatorStage>();
        s.AddScoped<IStage<IncidentEvent>, NotifierStage>();
        s.AddHostedService<SchedulerTicker>();
        s.AddHostedService<StageWorker<CheckMessage>>();
        s.AddHostedService<StageWorker<CheckResultMessage>>();
        s.AddHostedService<StageWorker<IncidentEvent>>();
        return s;
    }
}
