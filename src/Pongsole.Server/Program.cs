using System.Security.Cryptography;
using Pongsole.Contracts;
using Pongsole.Core;
using Pongsole.Server;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
    builder.WebHost.UseUrls("http://127.0.0.1:5187");

// Forwarding for the game endpoint is configured only through explicit known proxies.
builder.Configuration["ForwardedHeaders_Enabled"] = "false";
builder.AddHealthChecks();
OpenTelemetryExtensions.AddOpenTelemetry(builder);
// Disable the SignalR dispatcher category because its diagnostics can include resume tokens.
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", LogLevel.None);
// SignalR transport tokens also appear in request URLs and connection manager debug logs.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections.Internal.HttpConnectionManager", LogLevel.Information);
builder.Services.AddSignalR().AddJsonProtocol(options => GameJson.Configure(options.PayloadSerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);
BrowserAccess.AddServices(builder.Services);
builder.Services.AddSingleton(builder.Configuration.GetSection("Pong:Turn").Get<TurnOptions>() ?? new());
builder.Services.AddHttpClient<ITurnCredentialProvider, CloudflareTurnCredentialProvider>()
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(_ => new GameEngine(RandomNumberGenerator.GetInt32(int.MaxValue)));
builder.Services.AddSingleton(services => new GameCoordinator(
    services.GetRequiredService<GameEngine>(), services.GetRequiredService<TimeProvider>(), Random.Shared));
builder.Services.AddSingleton<IGameCoordinator>(services => services.GetRequiredService<GameCoordinator>());
builder.Services.AddHostedService(services => services.GetRequiredService<GameCoordinator>());

var app = builder.Build();
var access = new GameAccess(app.Configuration);
access.Apply(app);
app.UseAuthentication();
app.UseHomepage();
app.UseStaticFiles();
app.Services.GetRequiredService<BrowserAccess>().MapEndpoints(app, access);
app.MapHub<GameHub>("/game");
app.MapHealthCheckEndpoints();
app.Run();

public partial class Program;
