var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Pongsole_Server>("server")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("http", url =>
    {
        url.DisplayText = "Play Pongsole";
        url.Url = "/";
    });

builder.Build().Run();
