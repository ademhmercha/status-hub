using StatusHub.Application;

namespace StatusHub.LocalHost;

/// <summary>Local replacement for CloudWatch EMF: keeps every sample in memory so a load test can read percentiles at /dev/metrics.</summary>
public sealed class InMemoryStatusMetrics : IStatusMetrics
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<double>> samples = [];

    public void Count(string name, double value = 1) => Add(name, value);
    public void Milliseconds(string name, double value) => Add(name, value);
    public void Seconds(string name, double value) => Add(name, value);

    private void Add(string name, double value)
    {
        lock (gate)
        {
            if (!samples.TryGetValue(name, out var list)) samples[name] = list = [];
            list.Add(value);
        }
    }

    public IReadOnlyDictionary<string, MetricSummary> Snapshot()
    {
        lock (gate)
        {
            return samples.ToDictionary(kv => kv.Key, kv =>
            {
                var sorted = kv.Value.Order().ToArray();
                double P(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
                return new MetricSummary(sorted.Length, sorted.Sum(), P(0.5), P(0.95), P(0.99), sorted[^1]);
            });
        }
    }

    public void Reset()
    {
        lock (gate) samples.Clear();
    }
}

public sealed record MetricSummary(int Samples, double Sum, double P50, double P95, double P99, double Max);
