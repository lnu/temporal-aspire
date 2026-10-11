using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Testing;

namespace TemporalCommunity.Aspire.Hosting;

/// <summary>
/// Owns SDK environments independently of resource events, which are not guaranteed on host shutdown.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
    Justification = "Stop and disposal may race or be called repeatedly via DI aliases. The semaphore uses no WaitHandle and must remain usable for late cleanup.")]
internal sealed class TemporalLocalServerLifecycle : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<TemporalLocalResource, (WorkflowEnvironment Environment, ILogger Logger)> resources = [];
    private readonly Func<TemporalResourceOptions, Task<WorkflowEnvironment>> startEnvironment;
    private volatile bool stopping;
    private int applicationStoppingRegistered;

    public TemporalLocalServerLifecycle()
        : this(options => WorkflowEnvironment.StartLocalAsync(TemporalArgsBuilder.BuildLocalOptions(options)))
    {
    }

    internal TemporalLocalServerLifecycle(Func<TemporalResourceOptions, Task<WorkflowEnvironment>> startEnvironment)
    {
        this.startEnvironment = startEnvironment;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => StopAllAsync();

    internal void RegisterApplicationStopping(IHostApplicationLifetime lifetime)
    {
        if (Interlocked.Exchange(ref applicationStoppingRegistered, 1) != 0)
            return;

        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                StopAllAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Resource-specific shutdown failures are logged by StopResourceCoreAsync; StopAsync can retry.
            }
        });
    }

    public ValueTask DisposeAsync() => new(StopAllAsync());

    internal async Task<WorkflowEnvironment> StartResourceAsync(
        TemporalLocalResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (stopping)
                throw new InvalidOperationException("Cannot start a Temporal local server while the host is stopping.");

            if (resources.TryGetValue(resource, out var existing))
                return existing.Environment;

            // The SDK start API has no cancellation token. Keep ownership until it completes,
            // even when host stop or cancellation arrives during startup.
            var environment = await startEnvironment(resource.Options);
            resource.WorkflowEnvironment = environment;
            resources.Add(resource, (environment, logger));
            if (stopping || cancellationToken.IsCancellationRequested)
            {
                await StopResourceCoreAsync(resource, environment, logger);
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The host stopped while the Temporal local server was starting.");
            }

            return environment;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task StopResourceAsync(TemporalLocalResource resource)
    {
        // Cleanup must not be skipped because the resource or host cancellation token was canceled.
        await gate.WaitAsync();
        try
        {
            if (resources.TryGetValue(resource, out var entry))
                await StopResourceCoreAsync(resource, entry.Environment, entry.Logger);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopAllAsync()
    {
        stopping = true;
        await gate.WaitAsync();
        try
        {
            List<Exception> errors = [];
            foreach (var (resource, entry) in resources.ToArray())
            {
                try
                {
                    await StopResourceCoreAsync(resource, entry.Environment, entry.Logger);
                }
                catch (Exception exception)
                {
                    // Attempt every server. A failure must not strand the remaining environments.
                    errors.Add(exception);
                }
            }

            if (errors.Count > 0)
                throw new AggregateException("Failed to shut down Temporal local servers.", errors);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopResourceCoreAsync(TemporalLocalResource resource, WorkflowEnvironment environment, ILogger logger)
    {
        try
        {
            logger.LogInformation("Shutting down Temporal test server '{ResourceName}'...", resource.Name);
            await environment.ShutdownAsync();
            resource.WorkflowEnvironment = null;
            resources.Remove(resource);
            logger.LogInformation("Temporal test server '{ResourceName}' shut down successfully.", resource.Name);
        }
        catch (Exception exception)
        {
            // Retain ownership after failure so host stop/disposal can retry, and preserve the real error.
            logger.LogError(exception, "Error shutting down Temporal test server '{ResourceName}'", resource.Name);
            throw;
        }
    }
}
