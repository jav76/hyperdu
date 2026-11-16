using BenchmarkDotNet.Running;
using hyperdu.Benchmarks.Cli;

namespace hyperdu.Benchmarks;

public static class Program
{
    /// <summary>
    /// Entry point supporting "cli" (default black-box CLI comparison harness) and "micro" (BenchmarkDotNet).
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "micro", StringComparison.OrdinalIgnoreCase))
        {
            string[] bdnArgs = args.Length > 1 ? args[1..] : [];
            var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(bdnArgs);
            return summaries.Any(s => s.HasCriticalValidationErrors) ? 1 : 0;
        }

        string[] cliArgs = args.Length > 0 && string.Equals(args[0], "cli", StringComparison.OrdinalIgnoreCase)
            ? args[1..]
            : args;

        return await CliBenchmarkRunner.RunAsync(cliArgs);
    }
}
