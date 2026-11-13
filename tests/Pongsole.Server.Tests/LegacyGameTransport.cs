using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pongsole.Contracts;

namespace Pongsole.Server.Tests;

internal static class LegacyGameTransport
{
    internal static void AddLegacyGameTransport(this IServiceCollection services)
    {
        services.AddSignalR().AddJsonProtocol(options => GameJson.Configure(options.PayloadSerializerOptions));
        services.AddSingleton<IStartupFilter, LegacyGameEndpoint>();
        services.AddLogging(logging =>
        {
            logging.AddFilter("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Http.Connections.Internal.HttpConnectionManager", LogLevel.Information);
        });
    }

    private sealed class LegacyGameEndpoint(IConfiguration configuration) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var access = new GameAccess(configuration);
            app.Map("/game", branch =>
            {
                access.ApplyForwarding(branch);
                branch.Use(async (context, continuation) =>
                {
                    var status = await access.RejectionStatusAsync(context);
                    if (status is { } rejected)
                        context.Response.StatusCode = rejected;
                    else
                        await continuation(context);
                });
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseEndpoints(endpoints => endpoints.MapHub<LegacyGameHub>(""));
            });
            next(app);
        };
    }
}
