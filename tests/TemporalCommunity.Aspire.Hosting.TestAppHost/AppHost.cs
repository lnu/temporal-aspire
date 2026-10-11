using TemporalCommunity.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

if (builder.Configuration["TemporalMode"] == "local")
{
    builder.AddTemporalLocalDevServer("temporal", options =>
    {
        options.TargetHost = $"127.0.0.1:{builder.Configuration["TemporalPort"] ?? "7233"}";
        options.MetricsPort = int.Parse(builder.Configuration["TemporalMetricsPort"] ?? "9233", System.Globalization.CultureInfo.InvariantCulture);
        options.UI = false;
    });
    builder.Build().Run();
    return;
}

var temporal = builder.AddTemporalDevContainer("temporal").WithCliTerminal();

var dependent = builder.AddExecutable(
    "dependent",
    OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh",
    builder.AppHostDirectory);

if (OperatingSystem.IsWindows())
    dependent.WithArgs("-NoProfile", "-Command", "Start-Sleep -Seconds 600");
else
    dependent.WithArgs("-c", "sleep 600");

dependent
    .WaitFor(temporal)
    .WithReference(temporal);

builder.Build().Run();
