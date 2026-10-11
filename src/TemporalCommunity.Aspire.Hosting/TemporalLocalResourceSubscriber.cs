using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace TemporalCommunity.Aspire.Hosting;

/// <summary>
/// Event subscriber for managing local Temporal server lifecycle.
/// </summary>
public class TemporalLocalResourceSubscriber : IDistributedApplicationEventingSubscriber
{
    /// <summary>
    /// Subscribes to resource events for initializing and managing local Temporal servers.
    /// </summary>
    /// <param name="eventing">The distributed application eventing service.</param>
    /// <param name="executionContext">The execution context containing application state and mode information.</param>
    /// <param name="cancellationToken">The cancellation token for this operation.</param>
    /// <returns>A completed task once event subscriptions are registered.</returns>
    public Task SubscribeAsync(IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        // Don't start server in publish mode
        if (executionContext.IsPublishMode)
        {
            return Task.CompletedTask;
        }

        // Subscribe to InitializeResourceEvent for each TemporalLocalResource
        eventing.Subscribe<InitializeResourceEvent>(OnInitializeAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles initialization of local Temporal resources.
    /// </summary>
    private static async Task OnInitializeAsync(InitializeResourceEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.Resource is not TemporalLocalResource resource)
        {
            return;
        }

        var resourceLoggerService = @event.Services.GetRequiredService<ResourceLoggerService>();
        var lifecycle = @event.Services.GetRequiredService<TemporalLocalServerLifecycle>();
        lifecycle.RegisterApplicationStopping(@event.Services.GetRequiredService<IHostApplicationLifetime>());

        // Subscribe to ResourceStoppedEvent for this specific resource to handle cleanup
        @event.Eventing.Subscribe<ResourceStoppedEvent>(resource,
            (_, _) => lifecycle.StopResourceAsync(resource));

        await StartTemporalTestServerAsync(resource, @event.Eventing,
            @event.Notifications, resourceLoggerService, @event.Services, cancellationToken);
    }

    /// <summary>
    /// Starts the local Temporal test server and publishes connection events.
    /// </summary>
    private static async Task StartTemporalTestServerAsync(TemporalLocalResource resource,
        IDistributedApplicationEventing eventing,
        ResourceNotificationService resourceNotificationService,
        ResourceLoggerService resourceLoggerService,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var resourceLogger = resourceLoggerService.GetLogger(resource);

        try
        {
            // Publish starting state
            await resourceNotificationService.PublishUpdateAsync(resource, state => state with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info),
                StartTimeStamp = DateTime.UtcNow
            });

            resourceLogger.LogInformation("Starting Temporal test server for resource '{ResourceName}'...",
                resource.Name);

            var env = await serviceProvider.GetRequiredService<TemporalLocalServerLifecycle>()
                .StartResourceAsync(resource, resourceLogger, cancellationToken);

            var targetHost = env.Client.Connection.Options.TargetHost ?? "unknown";
            var namespaces = string.Join(", ", resource.Options.AdditionalNamespaces);

            resourceLogger.LogInformation(
                "Temporal test server started successfully. Target: {TargetHost}, Namespaces: {Namespaces}",
                targetHost,
                namespaces);

            // Publish running state with properties
            await resourceNotificationService.PublishUpdateAsync(resource, state => state with
            {
                State = KnownResourceStates.Running,
                Properties =
                [
                    new ResourcePropertySnapshot(CustomResourceKnownProperties.Source,
                        "Temporalio.Testing.WorkflowEnvironment"),
                    new ResourcePropertySnapshot("temporal.target-host", targetHost),
                    new ResourcePropertySnapshot("temporal.namespace", resource.Options.Namespace),
                    new ResourcePropertySnapshot("temporal.namespaces", namespaces)
                ]
            });

            // Now publish events (after environment is properly stored and assigned)
            var connectionStringAvailableEvent = new ConnectionStringAvailableEvent(resource, serviceProvider);
            await eventing.PublishAsync(connectionStringAvailableEvent, cancellationToken).ConfigureAwait(false);

            // Publish ResourceReadyEvent to signal that the resource is ready
            await eventing.PublishAsync(new ResourceReadyEvent(resource, serviceProvider), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            resourceLogger.LogError(ex, "Failed to start Temporal test server for resource '{ResourceName}'",
                resource.Name);
            await serviceProvider.GetRequiredService<TemporalLocalServerLifecycle>().StopResourceAsync(resource);

            // Publish failed state
            await resourceNotificationService.PublishUpdateAsync(resource, state => state with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error)
            });

            throw;
        }
    }
}
