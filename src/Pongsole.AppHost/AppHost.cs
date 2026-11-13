var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Pongsole_Server>("server")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("http", url =>
    {
        url.DisplayText = "Play Pongsole";
        url.Url = "/";
    });

if (builder.ExecutionContext.IsRunMode)
{
    var serverEndpoint = server.GetEndpoint("http");
    foreach (var (resourceName, playerName) in new[] { ("terminal-1", "Terminal 1"), ("terminal-2", "Terminal 2") })
    {
        // Aspire's interactive resource terminal API is experimental.
#pragma warning disable ASPIRETERMINAL001
        builder.AddProject<Projects.Pongsole_Client>(resourceName)
            .WithArgs("--server", serverEndpoint, "--name", playerName)
            .WaitFor(server)
            .WithExplicitStart()
            .WithTerminal(options =>
            {
                options.Columns = 120;
                options.Rows = 35;
            });
#pragma warning restore ASPIRETERMINAL001
    }
}

builder.Build().Run();
