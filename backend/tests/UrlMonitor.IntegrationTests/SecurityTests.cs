using System.Net;

namespace UrlMonitor.IntegrationTests;

/// <summary>
/// The API has no login, so it relies on these to stop other websites using it through your browser.
/// Uses POST /api/alerts/test as the target: it has no body, so a plain cross-site form could send it.
/// With no recipients it answers 400, which shows the request got past the guard.
/// </summary>
public sealed class SecurityTests : IAsyncLifetime
{
    private MonitorAppFactory _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _app = new MonitorAppFactory();
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Unknown_host_name_is_rejected_to_stop_dns_rebinding()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/urls", ("Host", "attacker.example"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]          // e.g. another app on localhost:3000
    [InlineData("Origin", "https://attacker.example")]    // older browsers without Sec-Fetch-Site
    [InlineData("Origin", "null")]                        // sandboxed iframes, file:// pages
    public async Task Cross_site_requests_that_change_data_are_blocked(string header, string value)
    {
        var post = await SendAsync(HttpMethod.Post, "/api/alerts/test", (header, value));
        var delete = await SendAsync(HttpMethod.Delete, "/api/urls/1", (header, value));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "same-origin")]       // the UI, through the Vite proxy
    [InlineData("Origin", "http://localhost:5173")]
    [InlineData("Origin", "http://127.0.0.1:5173")]
    [InlineData("Sec-Fetch-Site", "none")]              // typed into the address bar
    public async Task Requests_from_the_app_itself_are_allowed(string header, string value)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/alerts/test", (header, value));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // "add an email first", not 403
    }

    [Fact]
    public async Task Reads_are_not_blocked()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/urls", ("Sec-Fetch-Site", "cross-site"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
