using System.Collections.Concurrent;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace ResourceExpansion.Benchmarks;

public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        // In-process, so the shared environment and the recorded metrics live in the same process as the summary.
        AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(15));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(StatisticColumn.P95);
        AddColumn(
            new MetricColumn("Requests", "HTTP requests per operation", metrics => metrics.Result.Requests),
            new MetricColumn("Round trips", "Requests on the critical path (network latencies waited for)", metrics => metrics.Result.RoundTrips),
            new MetricColumn("SQL queries", "SELECT statements PostgreSQL executed per operation", metrics => metrics.Queries),
            new MetricColumn("Response bytes", "Response body bytes per operation", metrics => metrics.Result.Bytes));
    }
}

public sealed record ScenarioMetrics(ScenarioResult Result, int Queries)
{
    private static readonly ConcurrentDictionary<(string Method, int RelatedLimit), ScenarioMetrics> recorded = new();

    public static void Record(string method, int relatedLimit, ScenarioResult result, int queries) =>
        recorded[(method, relatedLimit)] = new ScenarioMetrics(result, queries);

    public static ScenarioMetrics? Find(BenchmarkCase benchmarkCase) => recorded.GetValueOrDefault(
        (benchmarkCase.Descriptor.WorkloadMethod.Name, (int)benchmarkCase.Parameters["RelatedLimit"]));
}

public sealed class MetricColumn(string name, string legend, Func<ScenarioMetrics, long> value) : IColumn
{
    public string Id => name;
    public string ColumnName => name;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => legend;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public bool IsAvailable(Summary summary) => true;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        ScenarioMetrics.Find(benchmarkCase) is { } metrics ? value(metrics).ToString("N0") : "?";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);
}
