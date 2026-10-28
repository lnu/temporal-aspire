using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TemporalCommunity.Aspire.Hosting.Tests;

public sealed class TemporalDataVolumeExtensionsTests : IDisposable
{
    private readonly string storePath = Directory.CreateTempSubdirectory("temporal-volume-test-").FullName;

    [Fact]
    public async Task WithDataVolume_CliUsesStableResourceScopedHostDirectory()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var first = builder.AddTemporalCliServer("first", null, () => true).WithDataVolume("data");
        var second = builder.AddTemporalCliServer("second", null, () => true).WithDataVolume("data");
        using var services = CreateServices();

        var firstConfig = await ResolveAsync(first.Resource, services);
        var secondConfig = await ResolveAsync(second.Resource, services);
        var firstDirectory = firstConfig.EnvironmentVariables.Single(pair => pair.Key == "TEMPORAL_DATA_DIR").Value;
        var secondDirectory = secondConfig.EnvironmentVariables.Single(pair => pair.Key == "TEMPORAL_DATA_DIR").Value;

        Assert.StartsWith(Path.Combine(storePath, "volumes") + Path.DirectorySeparatorChar, firstDirectory, StringComparison.Ordinal);
        Assert.NotEqual(firstDirectory, secondDirectory);
        Assert.True(Directory.Exists(firstDirectory));
        AssertDatabaseFilename(firstConfig, Path.Combine(firstDirectory, "temporal.db"));
        AssertDatabaseFilename(secondConfig, Path.Combine(secondDirectory, "temporal.db"));
        Assert.Null(first.Resource.Options.DevServerOptions.DatabaseFilename);

        // A new model with the same resource/storage identity must reuse the directory.
        var nextBuilder = DistributedApplication.CreateBuilder([]);
        var next = nextBuilder.AddTemporalCliServer("first", null, () => true).WithDataVolume("data");
        var nextConfig = await ResolveAsync(next.Resource, services);
        AssertDatabaseFilename(nextConfig, Path.Combine(firstDirectory, "temporal.db"));
    }

    [Fact]
    public async Task WithDataVolume_CliGeneratesStorageNameWhenOmitted()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var temporal = builder.AddTemporalCliServer("temporal", null, () => true).WithDataVolume();
        using var services = CreateServices();

        var config = await ResolveAsync(temporal.Resource, services);
        var directory = config.EnvironmentVariables.Single(pair => pair.Key == "TEMPORAL_DATA_DIR").Value;

        Assert.StartsWith(Path.Combine(storePath, "volumes") + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
        AssertDatabaseFilename(config, Path.Combine(directory, "temporal.db"));
    }

    [Fact]
    public async Task WithDataVolume_ContainerUsesNamedVolumeAndUnixDatabasePath()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var temporal = builder.AddTemporalDevContainer().WithDataVolume("orders-data");
        using var services = CreateServices();

        var config = await ResolveAsync(temporal.Resource, services);

        AssertDatabaseFilename(config, "/home/temporal/temporal.db");
        Assert.Equal("/home/temporal", config.EnvironmentVariables.Single(pair => pair.Key == "TEMPORAL_DATA_DIR").Value);
        var mount = Assert.Single(temporal.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("orders-data", mount.Source);
        Assert.Equal("/home/temporal", mount.Target);
        Assert.Equal(ContainerMountType.Volume, mount.Type);
        Assert.False(mount.IsReadOnly);
    }

    [Fact]
    public void WithDataVolume_ContainerGeneratesStorageNameWhenOmitted()
    {
        var temporal = DistributedApplication.CreateBuilder([]).AddTemporalDevContainer().WithDataVolume();

        var mount = Assert.Single(temporal.Resource.Annotations.OfType<ContainerMountAnnotation>());

        Assert.False(string.IsNullOrWhiteSpace(mount.Source));
        Assert.Equal("/home/temporal", mount.Target);
        Assert.Equal(ContainerMountType.Volume, mount.Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithDataVolume_RejectsExistingExplicitFilename(bool container)
    {
        var server = AddServer(DistributedApplication.CreateBuilder([]), container);
        server.Options.DevServerOptions.DatabaseFilename = "existing.db";

        var error = Assert.Throws<InvalidOperationException>(() => server.WithVolume("data"));

        Assert.Contains("DatabaseFilename", error.Message);
        Assert.Empty(server.Resource.Annotations.OfType<VolumeMountBindingAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithDataVolume_RejectsFilenameSetAfterVolumeRegistration(bool container)
    {
        var server = AddServer(DistributedApplication.CreateBuilder([]), container);
        server.WithVolume("data");
        server.Options.DevServerOptions.DatabaseFilename = "late.db";
        using var services = CreateServices();
        var context = new CommandLineArgsCallbackContext([], server.Resource)
        {
            ExecutionContext = CreateExecutionContext(services)
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Assert.Single(server.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()).Callback(context));

        Assert.Contains("DatabaseFilename", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutDataVolume_ExplicitFilenameIsReadLazily(bool container)
    {
        var server = AddServer(DistributedApplication.CreateBuilder([]), container);
        server.Options.DevServerOptions.DatabaseFilename = "configured-after-registration.db";
        using var services = CreateServices();

        var config = await ResolveAsync(server.Resource, services);

        AssertDatabaseFilename(config, "configured-after-registration.db");
        Assert.DoesNotContain(config.EnvironmentVariables, pair => pair.Key == "TEMPORAL_DATA_DIR");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutDataVolume_DefaultRemainsInMemory(bool container)
    {
        var server = AddServer(DistributedApplication.CreateBuilder([]), container);
        using var services = CreateServices();

        var config = await ResolveAsync(server.Resource, services);

        Assert.DoesNotContain(config.Arguments, argument => argument.Value == "--db-filename");
        Assert.DoesNotContain(config.EnvironmentVariables, pair => pair.Key == "TEMPORAL_DATA_DIR");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void WithDataVolume_RejectsInvalidStorageName(string? name)
    {
        var temporal = DistributedApplication.CreateBuilder([]).AddTemporalDevContainer();

        Assert.ThrowsAny<ArgumentException>(() => temporal.WithDataVolume(name!));
        Assert.Empty(temporal.Resource.Annotations.OfType<VolumeMountBindingAnnotation>());
    }

    [Fact]
    public void WithDataVolume_RejectsMultipleDataVolumes()
    {
        var temporal = DistributedApplication.CreateBuilder([]).AddTemporalDevContainer().WithDataVolume("first");

        Assert.Throws<InvalidOperationException>(() => temporal.WithDataVolume("second"));
        Assert.Single(temporal.Resource.Annotations.OfType<VolumeMountBindingAnnotation>());
    }

    public void Dispose() => Directory.Delete(storePath, recursive: true);

    private ServiceProvider CreateServices() => new ServiceCollection()
        .AddSingleton<IAspireStore>(new TestStore(storePath)).BuildServiceProvider();

    private static DistributedApplicationExecutionContext CreateExecutionContext(IServiceProvider services) =>
        new(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = services });

    private static async Task<IExecutionConfigurationResult> ResolveAsync(IResource resource, IServiceProvider services)
    {
        var result = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig().WithEnvironmentVariablesConfig().BuildAsync(CreateExecutionContext(services), NullLogger.Instance).ConfigureAwait(false);
        Assert.Null(result.Exception);
        return result;
    }

    private static void AssertDatabaseFilename(IExecutionConfigurationResult result, string expected)
    {
        var arguments = result.Arguments.Select(argument => argument.Value).ToArray();
        Assert.Single(arguments, argument => argument == "--db-filename");
        Assert.Equal(expected, arguments[Array.IndexOf(arguments, "--db-filename") + 1]);
    }

    private static (IResource Resource, TemporalResourceOptions Options, Action<string> WithVolume) AddServer(
        IDistributedApplicationBuilder builder, bool container)
    {
        if (container)
        {
            var server = builder.AddTemporalDevContainer();
            return (server.Resource, server.Resource.Options, name => server.WithDataVolume(name));
        }
        var cli = builder.AddTemporalCliServer("temporal-cli-server", null, () => true);
        return (cli.Resource, cli.Resource.Options, name => cli.WithDataVolume(name));
    }

    private sealed class TestStore(string path) : IAspireStore
    {
        public string BasePath => path;
        public string GetFileNameWithContent(string filenameTemplate, Stream contentStream) => throw new NotSupportedException();
    }
}
