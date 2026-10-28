# Temporal Community Aspire Hosting

`TemporalCommunity.Aspire.Hosting` is a .NET Aspire hosting integration for running Temporal development server resources from an Aspire AppHost.

## Features

- **Local development server** using `Temporalio.Testing.WorkflowEnvironment`
- **Container-based development server** using the `temporalio/temporal` image
- **CLI-based development server** using the Temporal CLI
- **Service discovery** through Aspire resource references
- **Environment variable injection** for dependent worker/client projects
- **Health checks** integrated with Aspire resource health

## Install

Requires .NET Aspire 13.6.1 or later.

```bash
dotnet add package TemporalCommunity.Aspire.Hosting
```

## Usage

Add a Temporal resource to your AppHost and reference it from projects that need Temporal connectivity.

```csharp
using TemporalCommunity.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var temporal = builder.AddTemporalLocalDevServer("temporal");

builder.AddProject<Projects.Worker>("worker")
    .WaitFor(temporal)
    .WithReference(temporal);

builder.Build().Run();
```

`WithReference` injects:

- `TEMPORAL_ADDRESS`
- `TEMPORAL_UI_ADDRESS` (when a UI address is configured)
- `TEMPORAL_NAMESPACE`

## Other resource modes

```csharp
var temporal = builder.AddTemporalDevContainer();
```

```csharp
var temporal = builder.AddTemporalCliServer();
```

`AddTemporalDevContainer` requires an Aspire-supported container runtime, such as Docker or Podman. `AddTemporalCliServer` requires the Temporal CLI on `PATH`.

By default, Aspire allocates free host ports for the container while Temporal continues to listen inside the container on 7233 (gRPC), 8233 (UI), and 9233 (metrics). `WithReference` resolves the allocated ports automatically, so multiple AppHosts can run side by side without sharing a Temporal server accidentally.

To request fixed host ports, configure non-default values explicitly:

```csharp
var temporal = builder.AddTemporalDevContainer("temporal", options =>
{
    options.TargetHost = "0.0.0.0:7399";
    options.UIPort = 8399;
    options.MetricsPort = 9399;
});
```

## Dashboard terminal

Add `.WithCliTerminal()` to a Temporal resource to show **Open Temporal CLI** in the Aspire Dashboard during local AppHost runs:

```csharp
var temporal = builder.AddTemporalDevContainer("temporal")
    .WithCliTerminal();
```

The same extension works with `AddTemporalCliServer`, `AddTemporalLocalDevServer`, and `AddTemporalCloud`. It opens an interactive shell with `TEMPORAL_ADDRESS` and `TEMPORAL_NAMESPACE` configured for the running server. Run commands such as `temporal workflow list` directly in that shell. The command is enabled only while the resource is running and healthy. Cloud resources must enable the health check (`EnableHealthCheck = true`) for the terminal command to become available.

Container terminals use the CLI bundled in the image through Aspire's selected Docker or Podman runtime; no host Temporal CLI installation is needed. CLI, SDK-managed, and Cloud terminals use a host shell and require the [Temporal CLI](https://docs.temporal.io/cli/setup-cli) on `PATH`. Cloud terminals use the resource's API key, address, and namespace with TLS enabled. Anyone who can open the Dashboard terminal can run authenticated CLI commands against that namespace, so enable it only for trusted Dashboard users. The action is omitted when publishing or deploying the AppHost.

The AppHost must enable the Aspire CLI bundle, which supplies Dashboard terminal support:

```xml
<PropertyGroup>
    <AspireUseCliBundle>true</AspireUseCliBundle>
</PropertyGroup>
```

## Temporal Cloud

Use `AddTemporalCloud` to reference an externally managed Temporal Cloud namespace.

```csharp
var temporalAddress = builder.AddParameter("temporal-address");
var temporalNamespace = builder.AddParameter("temporal-namespace");
var temporalApiKey = builder.AddParameter("temporal-api-key", secret: true);
var temporalUiAddress = builder.AddParameter("temporal-ui-address");

var temporal = builder.AddTemporalCloud(
    "temporal",
    temporalAddress,
    temporalNamespace,
    temporalApiKey,
    temporalUiAddress,
    configure: options => options.EnableHealthCheck = true);

builder.AddProject<Projects.Worker>("worker")
    .WithReference(temporal);
```

Use secret parameters for API keys and codec credentials. A UI address adds a **Temporal Dashboard** link in Aspire. `WithReference` supplies the Temporal connection environment variables to consuming projects, which can load them with `ClientEnvConfig.LoadClientConnectOptions()`.

`EnableHealthCheck` verifies the endpoint, TLS, and credentials with `GetSystemInfo`; it does not verify worker or task-queue health.

## Persist local development state

For CLI and container servers, use `.WithDataVolume()` to let Aspire generate a storage name, or pass a name to keep control of the storage identity:

```csharp
var temporal = builder.AddTemporalCliServer("temporal")
    .WithDataVolume();
```

The same extension works with `AddTemporalDevContainer`; pass a name such as `.WithDataVolume("temporal-data")` when you need an explicit container volume name.

Use `AddTemporalDevContainer` instead for a container server. Both follow [Aspire's portable volume path conventions](https://aspire.dev/fundamentals/persist-data-volumes/#use-a-portable-volume-path-environment-variable), so you do not need to configure a host database path:

- **CLI:** Aspire creates a resource-scoped directory in its local store.
- **Container:** Aspire mounts a named volume at `/home/temporal`.

Temporal writes `temporal.db` into the resolved directory. Keep the AppHost location, resource name, and storage name stable to reuse CLI data. The CLI store is normally under the AppHost's `obj/.aspire` directory; removing `obj` removes that data. CLI directories and container volumes are separate storage, so switching modes does not migrate an existing database.

For these modes, choose either `.WithDataVolume()` or `DevServerOptions.DatabaseFilename`; configuring both is rejected. Existing explicit filenames and container mounts continue to work.

The SDK-managed server (`AddTemporalLocalDevServer`) does not support `.WithDataVolume()`. It uses an in-memory database by default; persistence requires an explicit `DevServerOptions.DatabaseFilename` and an existing parent directory.

`DevServerOptions` is marked unstable by the Temporal .NET SDK and may change in future SDK versions.

When `UI = false`, the resource does not publish a dashboard URL or `TEMPORAL_UI_ADDRESS`. Development resources expose `/metrics`; container host ports are allocated unless non-default ports are configured explicitly.

## Run the samples

See the [samples README](https://github.com/temporal-community/temporal-aspire/blob/main/samples/README.md) for local and Temporal Cloud configuration and startup instructions.

## Resources

- [Temporal .NET SDK](https://github.com/temporalio/sdk-dotnet)
- [Temporal .NET SDK documentation](https://docs.temporal.io/develop/dotnet/)
- [Temporal production deployment documentation](https://docs.temporal.io/production-deployment)
