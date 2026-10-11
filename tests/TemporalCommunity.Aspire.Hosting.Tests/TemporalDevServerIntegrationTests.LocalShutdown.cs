using System.Diagnostics;
using Xunit;

namespace TemporalCommunity.Aspire.Hosting.Tests;

public partial class TemporalDevServerIntegrationTests
{
    [Fact]
    public async Task LocalAppHost_AspireResourceStopRestartAndHostStopReleaseServers()
    {
        if (!IntegrationTestsEnabled() || OperatingSystem.IsWindows())
            return;

        // A unique project identity prevents CLI stop from touching any developer AppHost.
        var directory = Path.Combine(Path.GetTempPath(), $"temporal-aspire-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourceDirectory = Path.Combine(
            FindRepositoryRoot(), "tests", "TemporalCommunity.Aspire.Hosting.TestAppHost");
        var projectName = "TemporalCommunity.Aspire.Hosting.TestAppHost.csproj";
        Process? firstServer = null;
        Process? restartedServer = null;
        var port = GetAvailablePort();
        try
        {
            var project = await File.ReadAllTextAsync(Path.Combine(sourceDirectory, projectName)).ConfigureAwait(true);
            project = project.Replace(
                @"..\..\src\TemporalCommunity.Aspire.Hosting\TemporalCommunity.Aspire.Hosting.csproj",
                Path.Combine(FindRepositoryRoot(), "src", "TemporalCommunity.Aspire.Hosting", "TemporalCommunity.Aspire.Hosting.csproj"),
                StringComparison.Ordinal);
            // Directory.Build.props normally supplies these from the repository.
            project = project.Replace("<OutputType>Exe</OutputType>",
                "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, projectName), project).ConfigureAwait(true);
            File.Copy(Path.Combine(sourceDirectory, "AppHost.cs"), Path.Combine(directory, "AppHost.cs"));
            File.Copy(Path.Combine(sourceDirectory, "aspire.config.json"), Path.Combine(directory, "aspire.config.json"));
            var toolManifestDirectory = Directory.CreateDirectory(Path.Combine(directory, ".config"));
            File.Copy(
                Path.Combine(FindRepositoryRoot(), ".config", "dotnet-tools.json"),
                Path.Combine(toolManifestDirectory.FullName, "dotnet-tools.json"));

            var startResult = await RunProcessAsync("aspire",
                ["start", "--isolated", "--format", "Json", "--non-interactive", "--nologo", "--",
                    "--TemporalMode=local", $"--TemporalPort={port}", $"--TemporalMetricsPort={GetAvailablePort()}"],
                directory).ConfigureAwait(true);
            Assert.Contains("dashboard", startResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
            await WaitForTemporalAsync(port).ConfigureAwait(true);
            firstServer = await FindListeningProcessAsync(port).ConfigureAwait(true);

            await RunProcessAsync("aspire", ["resource", "temporal", "stop", "--non-interactive"], directory).ConfigureAwait(true);
            Assert.True(firstServer.HasExited, $"Resource stop leaked server PID {firstServer.Id}.");
            Assert.True(IsPortAvailable(port), $"Resource stop did not release port {port}.");

            await RunProcessAsync("aspire", ["resource", "temporal", "start", "--non-interactive"], directory).ConfigureAwait(true);
            await WaitForTemporalAsync(port).ConfigureAwait(true);
            restartedServer = await FindListeningProcessAsync(port).ConfigureAwait(true);
            Assert.NotEqual(firstServer.Id, restartedServer.Id);
            await RunProcessAsync("aspire", ["stop", "--non-interactive"], directory).ConfigureAwait(true);
            Assert.True(restartedServer.HasExited, $"Aspire stop leaked restarted server PID {restartedServer.Id}.");
            Assert.True(IsPortAvailable(port), $"Aspire stop did not release port {port}.");
        }
        finally
        {
            await RunProcessAsync("aspire", ["stop", "--non-interactive"], directory, assertSuccess: false).ConfigureAwait(true);
            foreach (var server in new[] { firstServer, restartedServer })
            {
                if (server is null)
                    continue;
                await StopProcessAsync(server).ConfigureAwait(true);
                server.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalAppHost_SigtermStopsServerAndReleasesPort()
    {
        if (!IntegrationTestsEnabled() || OperatingSystem.IsWindows())
            return;

        var port = GetAvailablePort();
        var projectDirectory = Path.Combine(
            FindRepositoryRoot(), "tests", "TemporalCommunity.Aspire.Hosting.TestAppHost");
        var startInfo = CreateProcessStartInfo(
            "dotnet",
            [Path.Combine(projectDirectory, "bin", "Release", "net10.0", "TemporalCommunity.Aspire.Hosting.TestAppHost.dll"),
                "--TemporalMode=local", $"--TemporalPort={port}", $"--TemporalMetricsPort={GetAvailablePort()}"],
            projectDirectory);
        startInfo.Environment["ASPIRE_DASHBOARD_DISABLED"] = "true";
        startInfo.Environment["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var appHost = Process.Start(startInfo)!;
        var stdout = appHost.StandardOutput.ReadToEndAsync();
        var stderr = appHost.StandardError.ReadToEndAsync();
        Process? server = null;
        try
        {
            await WaitForTemporalAsync(port).ConfigureAwait(true);
            server = await FindListeningProcessAsync(port).ConfigureAwait(true);
            Assert.NotEqual(appHost.Id, server.Id);
            await RunProcessAsync("kill", ["-TERM", appHost.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]).ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await appHost.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(0, appHost.ExitCode);
            // Do not kill the server before these assertions: that would hide the regression.
            using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await server.WaitForExitAsync(serverTimeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // The assertion below reports the surviving child process.
            }
            Assert.True(server.HasExited, $"Temporal server PID {server.Id} survived normal AppHost shutdown.");
            Assert.True(IsPortAvailable(port), $"Temporal port {port} is still occupied after shutdown.");
        }
        finally
        {
            await StopProcessAsync(appHost).ConfigureAwait(true);
            if (server is not null)
            {
                await StopProcessAsync(server).ConfigureAwait(true);
                server.Dispose();
            }
            await stdout.ConfigureAwait(true);
            await stderr.ConfigureAwait(true);
        }
    }

    private static async Task<Process> FindListeningProcessAsync(int port)
    {
        var listener = await RunProcessAsync("lsof", ["-t", $"-iTCP:{port}", "-sTCP:LISTEN"]).ConfigureAwait(false);
        return Process.GetProcessById(int.Parse(listener.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }
}
