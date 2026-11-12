using System.Security.Cryptography;
using Pongsole.Contracts;
using Pongsole.Core;
using Pongsole.Server;
using Pongsole.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
    builder.WebHost.UseUrls("http://127.0.0.1:5187");

// Forwarding for the game endpoint is configured only through explicit known proxies.
builder.Configuration["ForwardedHeaders_Enabled"] = "false";
builder.AddServiceDefaults();
// Disable the SignalR dispatcher category because its diagnostics can include resume tokens.
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", LogLevel.None);
builder.Services.AddSignalR().AddJsonProtocol(options => GameJson.Configure(options.PayloadSerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new GameEngine(RandomNumberGenerator.GetInt32(int.MaxValue)));
builder.Services.AddSingleton(services => new GameCoordinator(
    services.GetRequiredService<GameEngine>(), services.GetRequiredService<TimeProvider>(), Random.Shared));
builder.Services.AddSingleton<IGameCoordinator>(services => services.GetRequiredService<GameCoordinator>());
builder.Services.AddHostedService(services => services.GetRequiredService<GameCoordinator>());

var app = builder.Build();
new GameAccess(app.Configuration).Apply(app);
app.MapHub<GameHub>("/game");
app.MapDefaultEndpoints();
app.Run();

public partial class Program;
