using System.Security.Cryptography;
using Pongsole.Core;
using Pongsole.Server;
using Pongsole.Transport;
using Pongsole.Transport.Native;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
    builder.WebHost.UseUrls("http://127.0.0.1:5187");

// Forwarding for the game endpoint is configured only through explicit known proxies.
builder.Configuration["ForwardedHeaders_Enabled"] = "false";
builder.AddHealthChecks();
OpenTelemetryExtensions.AddOpenTelemetry(builder);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
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
builder.Services.AddSingleton<IRtcPeerFactory, NativeRtcPeerFactory>();
builder.Services.AddSingleton<RtcSessions>();
builder.Services.AddHostedService(services => services.GetRequiredService<RtcSessions>());

var app = builder.Build();
var access = new GameAccess(app.Configuration);
access.Apply(app);
app.UseAuthentication();
app.UseHomepage();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            context.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.Services.GetRequiredService<BrowserAccess>().MapEndpoints(app, access);
RtcEndpoints.Map(app);
app.MapHealthCheckEndpoints();
app.Run();

public partial class Program;
