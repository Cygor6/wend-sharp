using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WendSharp;

MSBuildLocator.RegisterDefaults();

var solutionPath = GetArg(args, "--solution")
                   ?? Environment.GetEnvironmentVariable("WendSharp_SOLUTION");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(sp =>
    new WorkspaceSession(sp.GetRequiredService<ILogger<WorkspaceSession>>(), solutionPath));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

var warmupSession = host.Services.GetRequiredService<WorkspaceSession>();
var warmupLogger = host.Services.GetRequiredService<ILogger<WorkspaceSession>>();
_ = warmupSession.InitializeAsync(CancellationToken.None).AsTask()
    .ContinueWith(t =>
    {
        if (t.Exception is { } ex)
            warmupLogger.LogWarning(ex.GetBaseException(),
                "Background solution warm-up failed; the first tool call will retry the load.");
    }, TaskContinuationOptions.OnlyOnFaulted);

await host.RunAsync();
return;

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
