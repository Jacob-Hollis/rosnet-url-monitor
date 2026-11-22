using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace UrlMonitor.IntegrationTests;

/// <summary>
/// A real local HTTP server (Kestrel on a random port) that the monitor checks, so the tests
/// exercise real sockets and the real HttpClient configuration but never touch the internet.
/// </summary>
public sealed class StubServer : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";

    /// <summary>Controls /toggle: 200 when true, 503 when false.</summary>
    public bool ToggleIsUp { get; set; } = true;

    public string Url(string path) => BaseUrl + path;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.MapGet("/ok", () => "ok");
        _app.MapGet("/error", () => Results.StatusCode(StatusCodes.Status500InternalServerError));
        _app.MapGet("/redirect", () => Results.Redirect("/ok"));
        _app.MapGet("/auth", () => Results.StatusCode(StatusCodes.Status401Unauthorized));
        _app.MapGet("/slow", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return "too late";
        });
        _app.MapGet("/toggle", () => ToggleIsUp
            ? Results.Ok("up")
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        BaseUrl = address.TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
