# Running the samples

Both samples start a worker and a client that runs a simple Temporal workflow.

## Setup

Install the .NET 10 SDK, then restore the pinned Aspire CLI from the repository root:

```bash
dotnet tool restore
```

## Local development server

From `samples/`:

```bash
dotnet tool run aspire -- start --apphost SampleAppHost/SampleAppHost.csproj
```

This sample starts a Temporal CLI server and persists its database with `.WithDataVolume("temporal-data")` in an Aspire-managed host directory. It requires the Temporal CLI on `PATH`; the Dashboard's **Open Temporal CLI** action uses that same CLI to open a shell for the server. Removing the AppHost's `obj` directory also removes the CLI server's persisted data.

## Temporal Cloud

From `samples/`, copy the configuration template:

```bash
cd SampleCloudAppHost/
cp .secrets.env.example .secrets.env
```

Edit `.secrets.env` with your Cloud address, namespace, API key, and UI URL. The file is ignored by Git. Then run from that folder:

```bash
source .secrets.env
dotnet tool run aspire -- start
```

The worker and client start after the Cloud connection health check succeeds. **Open Temporal CLI** opens a local shell configured with the Cloud address, namespace, API key, and TLS; it requires the Temporal CLI on `PATH`.

We recommend the [Temporal Cloud CLI extension](https://github.com/temporalio/cloud-cli) for inspecting and managing Cloud namespaces. After sourcing `.secrets.env`, use the sample's API key to check your identity and namespace:

```bash
TEMPORAL_API_KEY="$Parameters__temporal_api_key" temporal cloud whoami
TEMPORAL_API_KEY="$Parameters__temporal_api_key" \
  temporal cloud namespace get --namespace "$Parameters__temporal_namespace"
```

## Check the result and stop

Open the Dashboard URL printed by Aspire. In **sample-client** console logs, expect:

```text
Hello, Aspire, from Temporal!
```

Stop the local sample from the repository root:

```bash
dotnet tool run aspire -- stop --apphost samples/SampleAppHost/SampleAppHost.csproj --non-interactive
```

Or stop the Cloud sample from `samples/SampleCloudAppHost/`:

```bash
dotnet tool run aspire -- stop --non-interactive
```
