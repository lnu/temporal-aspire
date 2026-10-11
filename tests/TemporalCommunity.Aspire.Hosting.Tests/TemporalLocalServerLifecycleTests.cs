using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TemporalCommunity.Aspire.Hosting;
using Temporalio.Client;
using Temporalio.Testing;
using Xunit;

namespace TemporalCommunity.Aspire.Hosting.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
    Justification = "Fake environments transfer to the lifecycle owner; extra disposal would increment the shutdown counts being asserted.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Await-using cleanup deliberately preserves the xUnit synchronization context, like ConfigureAwait(true) on explicit awaits.")]
public class TemporalLocalServerLifecycleTests
{
    [Fact]
    public async Task ResourceAndHostStop_AwaitSameShutdownExactlyOnce()
    {
        var shutdown = NewCompletion<bool>();
        var entered = NewCompletion<bool>();
        var environment = new TestEnvironment(() =>
        {
            entered.SetResult(true);
            return shutdown.Task;
        });
        await using var lifecycle = CreateLifecycle(environment);
        var resource = new TemporalLocalResource("test");
        await StartAsync(lifecycle, resource).ConfigureAwait(true);

        var resourceStop = lifecycle.StopResourceAsync(resource);
        await entered.Task.ConfigureAwait(true);
        var hostStop = lifecycle.StopAsync(new CancellationToken(canceled: true));
        var secondStop = lifecycle.StopResourceAsync(resource);
        var disposal = lifecycle.DisposeAsync().AsTask();
        Assert.False(resourceStop.IsCompleted);
        Assert.False(hostStop.IsCompleted);
        Assert.False(secondStop.IsCompleted);
        Assert.False(disposal.IsCompleted);
        shutdown.SetResult(true);
        await Task.WhenAll(resourceStop, hostStop, secondStop, disposal).ConfigureAwait(true);
        Assert.Equal(1, environment.ShutdownCount);
        Assert.Null(resource.WorkflowEnvironment);
    }

    [Fact]
    public async Task HostStopDuringStart_WaitsAndCleansUpLateEnvironment()
    {
        var start = NewCompletion<WorkflowEnvironment>();
        var environment = new TestEnvironment();
        await using var lifecycle = new TemporalLocalServerLifecycle(_ => start.Task);
        var resource = new TemporalLocalResource("test");
        var starting = StartAsync(lifecycle, resource);
        var stopping = lifecycle.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        start.SetResult(environment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => starting).ConfigureAwait(true);
        await stopping.ConfigureAwait(true);
        Assert.Equal(1, environment.ShutdownCount);
        Assert.Null(resource.WorkflowEnvironment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(lifecycle, resource)).ConfigureAwait(true);
    }

    [Fact]
    public async Task ResourceStopDuringStart_WaitsForOwnershipThenShutsDown()
    {
        var start = NewCompletion<WorkflowEnvironment>();
        var environment = new TestEnvironment();
        await using var lifecycle = new TemporalLocalServerLifecycle(_ => start.Task);
        var resource = new TemporalLocalResource("test");
        var starting = StartAsync(lifecycle, resource);
        var stopping = lifecycle.StopResourceAsync(resource);
        Assert.False(stopping.IsCompleted);
        start.SetResult(environment);
        await Task.WhenAll(starting, stopping).ConfigureAwait(true);
        Assert.Equal(1, environment.ShutdownCount);
        Assert.Null(resource.WorkflowEnvironment);
    }

    [Fact]
    public async Task CancellationDuringStart_CleansUpEnvironment()
    {
        var start = NewCompletion<WorkflowEnvironment>();
        var environment = new TestEnvironment();
        await using var lifecycle = new TemporalLocalServerLifecycle(_ => start.Task);
        using var cancellation = new CancellationTokenSource();
        var resource = new TemporalLocalResource("test");
        var starting = lifecycle.StartResourceAsync(resource, NullLogger.Instance, cancellation.Token);
        await cancellation.CancelAsync().ConfigureAwait(true);
        start.SetResult(environment);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting).ConfigureAwait(true);
        Assert.Equal(1, environment.ShutdownCount);
        Assert.Null(resource.WorkflowEnvironment);
    }

    [Fact]
    public async Task DuplicateStartAndRestart_KeepOneOwnerPerGeneration()
    {
        var first = new TestEnvironment();
        var second = new TestEnvironment();
        var starts = 0;
        await using var lifecycle = new TemporalLocalServerLifecycle(_ =>
            Task.FromResult<WorkflowEnvironment>(++starts == 1 ? first : second));
        var resource = new TemporalLocalResource("test");
        await Task.WhenAll(StartAsync(lifecycle, resource), StartAsync(lifecycle, resource)).ConfigureAwait(true);
        Assert.Equal(1, starts);
        await lifecycle.StopResourceAsync(resource).ConfigureAwait(true);
        await StartAsync(lifecycle, resource).ConfigureAwait(true);
        Assert.Same(second, resource.WorkflowEnvironment);
        await lifecycle.StopAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(2, starts);
        Assert.Equal(1, first.ShutdownCount);
        Assert.Equal(1, second.ShutdownCount);
    }

    [Fact]
    public async Task ShutdownFailure_IsLoggedRetainedAndDoesNotSkipOtherServers()
    {
        var error = new InvalidOperationException("SDK shutdown failed");
        var failing = new TestEnvironment(() => Task.FromException(error));
        var healthy = new TestEnvironment();
        var logger = new TestLogger();
        await using var lifecycle = new TemporalLocalServerLifecycle(options =>
            Task.FromResult<WorkflowEnvironment>(options.Namespace == "failing" ? failing : healthy));
        var first = new TemporalLocalResource("first") { Options = new() { Namespace = "failing" } };
        var second = new TemporalLocalResource("second");
        await lifecycle.StartResourceAsync(first, logger, CancellationToken.None).ConfigureAwait(true);
        await StartAsync(lifecycle, second).ConfigureAwait(true);
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            lifecycle.StopAsync(CancellationToken.None)).ConfigureAwait(true);
        Assert.Same(error, Assert.Single(aggregate.InnerExceptions));
        Assert.Same(error, Assert.Single(logger.Errors));
        Assert.Same(failing, first.WorkflowEnvironment);
        Assert.Null(second.WorkflowEnvironment);
        Assert.Equal(1, healthy.ShutdownCount);
        failing.Shutdown = () => Task.CompletedTask;
        await lifecycle.StopAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.Null(first.WorkflowEnvironment);
        Assert.Equal(2, failing.ShutdownCount);
    }

    [Fact]
    public async Task FailedStart_DoesNotPreventAnotherResourceStarting()
    {
        var environment = new TestEnvironment();
        await using var lifecycle = new TemporalLocalServerLifecycle(options =>
            options.Namespace == "failing"
                ? Task.FromException<WorkflowEnvironment>(new InvalidOperationException("start failed"))
                : Task.FromResult<WorkflowEnvironment>(environment));
        var failing = new TemporalLocalResource("first") { Options = new() { Namespace = "failing" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(lifecycle, failing)).ConfigureAwait(true);
        Assert.Null(failing.WorkflowEnvironment);
        await StartAsync(lifecycle, new TemporalLocalResource("second")).ConfigureAwait(true);
        await lifecycle.StopAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(1, environment.ShutdownCount);
    }

    [Fact]
    public async Task MultipleResources_RegisterOneSharedHostedLifecycle()
    {
        var builder = DistributedApplication.CreateBuilder();
        var originalServices = builder.Services.ToArray();
        builder.AddTemporalLocalDevServer("first");
        builder.AddTemporalLocalDevServer("second");
        await using var services = builder.Services.BuildServiceProvider();
        var lifecycle = services.GetRequiredService<TemporalLocalServerLifecycle>();
        var hostedService = Assert.Single(builder.Services.Except(originalServices),
            descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Same(lifecycle, hostedService.ImplementationFactory!(services));
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TemporalLocalServerLifecycle CreateLifecycle(TestEnvironment environment) =>
        new(_ => Task.FromResult<WorkflowEnvironment>(environment));

    private static Task<WorkflowEnvironment> StartAsync(TemporalLocalServerLifecycle lifecycle, TemporalLocalResource resource) =>
        lifecycle.StartResourceAsync(resource, NullLogger.Instance, CancellationToken.None);

    private sealed class TestEnvironment(Func<Task>? shutdown = null)
        : WorkflowEnvironment(TemporalClient.CreateLazy(new TemporalClientConnectOptions("localhost:7233")))
    {
        public Func<Task> Shutdown { get; set; } = shutdown ?? (() => Task.CompletedTask);

        public int ShutdownCount { get; private set; }

        public override Task ShutdownAsync()
        {
            ShutdownCount++;
            return Shutdown();
        }
    }

    private sealed class TestLogger : ILogger
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
                Errors.Add(exception);
        }
    }
}
