using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Pongsole.Server.Tests;

public sealed class ServiceHealthTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task DevelopmentHealthEndpointsRespondSuccessfully(string path)
    {
        await using var server = new WebApplicationFactory<Program>();
        using var client = server.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
