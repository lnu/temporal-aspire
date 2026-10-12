using TemporalCommunity.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var temporal = builder.AddTemporalCliServer("temporal")
    .WithDataVolume("temporal-data")
    .WithCliTerminal();

builder.AddProject<Projects.SampleWorker>("sample-worker")
    .WaitFor(temporal)
    .WithReference(temporal);

builder.AddProject<Projects.SampleClient>("sample-client")
    .WaitFor(temporal)
    .WithReference(temporal);

builder.Build().Run();
