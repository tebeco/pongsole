using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Pongsole.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Pongsole_Server>("server")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint(builder.ExecutionContext.IsRunMode ? "https" : "http", url =>
    {
        url.DisplayText = "Play Pongsole";
        url.Url = "/";
    });

if (builder.ExecutionContext.IsRunMode)
{
    var configuredCode = Environment.GetEnvironmentVariable("PONGSOLE_SHARED_CODE");
    var code = string.IsNullOrWhiteSpace(configuredCode)
        ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
        : configuredCode;
    var gameCode = builder.AddParameter("game-code", () => code, secret: true, publishValueAsDefault: false);

    server.WithEnvironment("Pong__Mode", "Private")
        .WithEnvironment("PONGSOLE_SHARED_CODE", gameCode)
        .WithEnvironment("Pong__KnownProxies__0", "127.0.0.1")
        .WithEnvironment("Pong__KnownProxies__1", "::1");

    foreach (var (name, key) in new[]
    {
        ("cloudflare-turn-key-id", "Pong__Turn__KeyId"),
        ("cloudflare-turn-api-token", "Pong__Turn__ApiToken")
    })
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{name}"]))
            server.WithEnvironment(key, builder.AddParameter(name, secret: true));
    }

    var discordOrigin = builder.Configuration["Pong:Discord:Origin"];
    if (!string.IsNullOrWhiteSpace(discordOrigin))
        server.WithEnvironment("Pong__Discord__Origin", discordOrigin);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:discord-client-id"]))
        server.WithEnvironment("Pong__Discord__ClientId", builder.AddParameter("discord-client-id"));
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:discord-client-secret"]))
        server.WithEnvironment("Pong__Discord__ClientSecret", builder.AddParameter("discord-client-secret", secret: true));

    var tunnel = builder.AddProject<Projects.Pongsole_Tunnel>("cloudflared")
        .WithHttpEndpoint(name: "http")
        .WithUrlForEndpoint("http", url =>
        {
            url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
            url.Url = "/";
        })
        .WithHttpHealthCheck("/health")
        .WithEnvironment("Tunnel__Origin", server.GetEndpoint("http"))
        .WaitFor(server);

    var publicHostname = builder.Configuration["Parameters:cloudflare-tunnel-hostname"];
    var hasTunnelToken = !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:cloudflare-tunnel-token"]);
    if (!string.IsNullOrWhiteSpace(publicHostname) || hasTunnelToken)
    {
        if (!hasTunnelToken || Uri.CheckHostName(publicHostname) != UriHostNameType.Dns ||
            !publicHostname!.Contains('.') || publicHostname.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Set both cloudflare-tunnel-hostname and cloudflare-tunnel-token parameters for a named tunnel.");
        }

        tunnel.WithEnvironment("Tunnel__PublicHostname", builder.AddParameter("cloudflare-tunnel-hostname"))
            .WithEnvironment("Tunnel__Token", builder.AddParameter("cloudflare-tunnel-token", secret: true));
    }

    var tunnelEndpoint = tunnel.GetEndpoint("http");
    builder.Services.AddHostedService(services => new TunnelInvitePublisher(
        services.GetRequiredService<ResourceNotificationService>(), tunnel.Resource, tunnelEndpoint, gameCode.Resource,
        string.IsNullOrWhiteSpace(publicHostname) ? null : new UriBuilder(Uri.UriSchemeHttps, publicHostname).Uri));

    var serverEndpoint = server.GetEndpoint("https");
    foreach (var (resourceName, playerName) in new[] { ("terminal-1", "Terminal1"), ("terminal-2", "Terminal2") })
    {
        // Aspire's interactive resource terminal API is experimental.
#pragma warning disable ASPIRETERMINAL001
        builder.AddProject<Projects.Pongsole_Client>(resourceName)
            .WithArgs("--server", serverEndpoint, "--name", playerName)
            .WithEnvironment("PONGSOLE_SHARED_CODE", gameCode)
            .WithEnvironment("PONGSOLE_DASHBOARD_TERMINAL", "1")
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
