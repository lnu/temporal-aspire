namespace TemporalCommunity.Aspire.Hosting;

/// <summary>Configures persistent storage for Temporal CLI and container servers.</summary>
public static class TemporalDataVolumeExtensions
{
    private const string DataDirectoryVariable = "TEMPORAL_DATA_DIR";

    /// <summary>Persists the CLI server database in an Aspire-managed host directory.</summary>
    /// <param name="builder">The Temporal CLI server.</param>
    /// <param name="name">The storage name, scoped to this resource by Aspire.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<TemporalCliServerResource> WithDataVolume(
        this IResourceBuilder<TemporalCliServerResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return WithDataVolumeCore(builder, name ?? VolumeNameGenerator.Generate(builder, "cli"), builder.Resource.Options);
    }

    /// <summary>Persists the container server database in a named volume.</summary>
    /// <param name="builder">The Temporal container server.</param>
    /// <param name="name">The volume name.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<TemporalContainerResource> WithDataVolume(
        this IResourceBuilder<TemporalContainerResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return WithDataVolumeCore(builder, name ?? VolumeNameGenerator.Generate(builder, "data"), builder.Resource.Options);
    }

    internal static string? GetDatabaseFilename(
        TemporalResourceOptions options, CommandLineArgsCallbackContext context)
    {
        var binding = GetDataBinding(context.Resource);
        if (binding is null)
            return options.DevServerOptions.DatabaseFilename;

        EnsureNoExplicitFilename(options);
        var directory = binding.ResolvePath(new EnvironmentCallbackContext(
            context.ExecutionContext, context.Resource, cancellationToken: context.CancellationToken));

        // Container paths use Unix separators even when the AppHost runs on Windows.
        return context.Resource is ContainerResource || context.ExecutionContext.IsPublishMode
            ? directory.TrimEnd('/') + "/temporal.db"
            : Path.Combine(directory, "temporal.db");
    }

    private static IResourceBuilder<T> WithDataVolumeCore<T>(
        IResourceBuilder<T> builder, string name, TemporalResourceOptions options)
        where T : IComputeResource, IResourceWithEnvironment
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureNoExplicitFilename(options);
        if (GetDataBinding(builder.Resource) is not null)
            throw new InvalidOperationException("A Temporal data volume is already configured for this resource.");

        return builder.WithVolume(name, "/home/temporal", env: DataDirectoryVariable);
    }

    private static VolumeMountBindingAnnotation? GetDataBinding(IResource resource) =>
        resource.Annotations.OfType<VolumeMountBindingAnnotation>()
            .SingleOrDefault(binding => binding.EnvironmentVariableName == DataDirectoryVariable);

    private static void EnsureNoExplicitFilename(TemporalResourceOptions options)
    {
        if (!string.IsNullOrEmpty(options.DevServerOptions.DatabaseFilename))
            throw new InvalidOperationException(
                "Use either WithDataVolume or DevServerOptions.DatabaseFilename to configure Temporal persistence, not both.");
    }
}
