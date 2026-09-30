using BenchmarkDotNet.Attributes;

namespace ResourceExpansion.Benchmarks;

[Config(typeof(BenchmarkConfig))]
public class ExpansionBenchmarks
{
    private BenchmarkApp app = null!;

    // Visits returned per request. Also the number of distinct clubs, up to BenchmarkApp.ClubCount.
    [Params(1, 10, 50)]
    public int RelatedLimit { get; set; }

    // Added by Toxiproxy to every API response: 0 is loopback, 20 is a nearby region.
    [Params(0, 20)]
    public int NetworkLatencyMs { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        app = await BenchmarkApp.InstanceAsync();
        await app.SetNetworkLatencyAsync(NetworkLatencyMs);

        // Record requests, bytes, and SQL queries once per case; custom columns show them next to the timings.
        await MeasureAsync(nameof(Expanded), Expanded);
        await MeasureAsync(nameof(SeparateParallel), SeparateParallel);
        await MeasureAsync(nameof(SeparateSequential), SeparateSequential);
    }

    [Benchmark(Baseline = true)]
    public Task<ScenarioResult> Expanded() => Scenarios.ExpandedAsync(app.Client, RelatedLimit);

    [Benchmark]
    public Task<ScenarioResult> SeparateParallel() => Scenarios.SeparateParallelAsync(app.Client, RelatedLimit);

    [Benchmark]
    public Task<ScenarioResult> SeparateSequential() => Scenarios.SeparateSequentialAsync(app.Client, RelatedLimit);

    private async Task MeasureAsync(string method, Func<Task<ScenarioResult>> scenario)
    {
        await app.ResetQueryCountAsync();
        var result = await scenario();
        ScenarioMetrics.Record(method, RelatedLimit, result, await app.QueryCountAsync());
    }
}
