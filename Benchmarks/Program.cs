using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

using SharedLib.Logging;

// Stage-resolved profile of the navmesh bake pipeline (in-process, no server).
if (args.Length > 0 && args[0] == "--alloc-profile")
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Minimal, theme: TemplateTheme.Literate))
        .CreateLogger();

    Benchmarks.Navmesh.AllocProfiler.Run(Log.Logger);
}
else if (args.Length > 0 && args[0] == "--dump-geometry")
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Minimal, theme: TemplateTheme.Literate))
        .CreateLogger();

    Benchmarks.Navmesh.GeometryDumper.Run(args.Length > 1 ? args[1] : "artifacts/rcdump", Log.Logger);
}
else if (args.Length > 0 && args[0] == "--bake-profile")
{
    string profileLabel = args.Length > 1 ? args[1] : "baseline";
    int profileIterations = args.Length > 2 && int.TryParse(args[2], out int pIter) ? pIter : 3;

    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Minimal, theme: TemplateTheme.Literate))
        .CreateLogger();

    if (args.Contains("--throughput"))
    {
        Benchmarks.Navmesh.BakeProfiler.RunThroughput(profileLabel, Log.Logger);
    }
    else
    {
        Benchmarks.Navmesh.BakeProfiler.Run(profileLabel, profileIterations, Log.Logger);
    }

    Log.CloseAndFlush();
}
// End-to-end routes against a running Pathing Service.
else if (args.Length > 0 && args[0] == "--pather-benchmark")
{
    string baseUrl = args.Length > 1 ? args[1] : Contracts.Pathing.PathingApi.DefaultUrl;
    int iterations = args.Length > 2 && int.TryParse(args[2], out int iter) ? iter : 2;
    string label = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : "navmesh";

    Log.Logger = new LoggerConfiguration()
        .WriteTo.File(new ExpressionTemplate(LogOutputTemplates.Minimal),
            path: "benchmark_out.log",
            rollingInterval: RollingInterval.Day)
        .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Minimal, theme: TemplateTheme.Literate))
        .CreateLogger();

    Log.Information($"Running the Pathing Service benchmark against {baseUrl}...\n");

    await Benchmarks.PathingServiceBenchmark.RunBenchmark(baseUrl, iterations, logger: Log.Logger, label: label);

    Log.CloseAndFlush();
}
else
{
    // Run BenchmarkDotNet suite
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
