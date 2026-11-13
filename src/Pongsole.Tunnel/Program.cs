using Pongsole.Tunnel;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => TunnelAddress.ParseOrigin(builder.Configuration["Tunnel:Origin"]));
builder.Services.AddSingleton(_ => new HttpClient());
builder.Services.AddSingleton<CloudflaredInstaller>();
builder.Services.AddSingleton<ITunnelLauncher, CloudflaredLauncher>();
builder.Services.AddSingleton<TunnelExitStatus>();
builder.Services.AddSingleton<TunnelService>();
builder.Services.AddHostedService(services => services.GetRequiredService<TunnelService>());

var app = builder.Build();
app.MapGet("/", (HttpContext context, TunnelService tunnel) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return tunnel.PublicAddress is { } address
        ? Results.Redirect(address.AbsoluteUri)
        : Results.Text("The tunnel is not ready.", statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/health", (HttpContext context, TunnelService tunnel) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.StatusCode(tunnel.PublicAddress is not null
        ? StatusCodes.Status200OK
        : StatusCodes.Status503ServiceUnavailable);
});
var exitStatus = app.Services.GetRequiredService<TunnelExitStatus>();
await app.RunAsync();
return exitStatus.Code;

public partial class Program;
