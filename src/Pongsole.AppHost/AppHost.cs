var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Pongsole_Server>("server")
    .WithHttpHealthCheck("/health");

builder.Build().Run();
