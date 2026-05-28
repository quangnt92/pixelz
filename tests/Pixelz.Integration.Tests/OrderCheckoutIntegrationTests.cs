using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using Pixelz.Infrastructure.Persistence;

namespace Pixelz.Integration.Tests;

/// <summary>
/// Integration tests using WebApplicationFactory with in-memory EF database.
/// These tests exercise the full HTTP pipeline: routing, middleware, controllers, handlers.
/// </summary>
public class OrderCheckoutIntegrationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace SQL Server with in-memory DB
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<PixelzDbContext>));

                if (descriptor is not null)
                    services.Remove(descriptor);

                services.AddDbContext<PixelzDbContext>(opts =>
                    opts.UseInMemoryDatabase($"PixelzTest_{Guid.NewGuid()}"));
            });
        });

    [Fact]
    public async Task GET_HealthCheck_ShouldReturn200()
    {
        var client   = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_Checkout_WithoutJwt_ShouldReturn401()
    {
        var client   = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/orders/{Guid.NewGuid()}/checkout",
            new { paymentMethod = new { type = "card", token = "tok_any" } });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}