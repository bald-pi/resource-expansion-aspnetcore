using BenchmarkDotNet.Running;

namespace ResourceExpansion.Benchmarks;

public static class BenchmarkProgram
{
    public static async Task Main(string[] args)
    {
        try
        {
            BenchmarkSwitcher.FromAssembly(typeof(BenchmarkProgram).Assembly).Run(args);
        }
        finally
        {
            await BenchmarkApp.DisposeInstanceAsync();
        }
    }
}
