using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Services;

public class UrlCheckerTests
{
    private const string Url = "https://example.com/health";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly MonitorOptions _options = new() { TimeoutSeconds = 10, MaxBodyBytes = 1024 * 1024 };

    private UrlChecker CreateChecker(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new FakeHandler(handler)), Options.Create(_options), _time);

    private UrlChecker CreateChecker(HttpStatusCode status, Action<HttpResponseMessage>? configure = null) =>
        CreateChecker((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("body") };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });

    [Fact]
    public async Task Ok_response_is_up()
    {
        var result = await CreateChecker(HttpStatusCode.OK).CheckAsync(Url);

        Assert.True(result.IsUp);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Error);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, result.CheckedAt);
        Assert.Equal(DateTimeKind.Utc, result.CheckedAt.Kind);
    }

    [Fact]
    public async Task Server_error_is_down_with_status_in_error()
    {
        var result = await CreateChecker(HttpStatusCode.InternalServerError).CheckAsync(Url);

        Assert.False(result.IsUp);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("HTTP 500 Internal Server Error", result.Error);
    }

    [Fact]
    public async Task Redirect_is_down_and_shows_absolute_target()
    {
        var checker = CreateChecker(HttpStatusCode.Found, r => r.Headers.Location = new Uri("/new-home", UriKind.Relative));

        var result = await checker.CheckAsync(Url);

        Assert.False(result.IsUp);
        Assert.Equal(302, result.StatusCode);
        Assert.Contains("redirects to https://example.com/new-home", result.Error);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "DNS failure")]
    [InlineData(HttpRequestError.ConnectionError, "Connection failed")]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS error")]
    public async Task Network_errors_are_down_with_no_status(HttpRequestError error, string expectedPrefix)
    {
        var checker = CreateChecker((_, _) => throw new HttpRequestException(
            error, "request failed", new AuthenticationException("inner detail")));

        var result = await checker.CheckAsync(Url);

        Assert.False(result.IsUp);
        Assert.Null(result.StatusCode);
        Assert.StartsWith(expectedPrefix, result.Error);
    }

    [Fact]
    public async Task Timeout_waiting_for_response_is_down()
    {
        var checker = CreateChecker(async (_, ct) =>
        {
            _time.Advance(TimeSpan.FromSeconds(11)); // fires the 10s timeout
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        });

        var result = await checker.CheckAsync(Url);

        Assert.False(result.IsUp);
        Assert.Null(result.StatusCode);
        Assert.Equal("Timeout after 10s", result.Error);
        Assert.Equal(11_000, result.ResponseTimeMs);
    }

    [Fact]
    public async Task Timeout_while_downloading_body_is_down_but_keeps_status()
    {
        var body = new TestStream(onRead: async ct =>
        {
            _time.Advance(TimeSpan.FromSeconds(11));
            await Task.Delay(Timeout.Infinite, ct);
        });
        var checker = CreateChecker((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

        var result = await checker.CheckAsync(Url);

        Assert.False(result.IsUp);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Timeout after 10s", result.Error);
    }

    [Fact]
    public async Task Response_time_includes_full_body_download()
    {
        var advanced = false;
        var body = new TestStream(length: 10, onRead: _ =>
        {
            if (!advanced) _time.Advance(TimeSpan.FromMilliseconds(300)); // body download time
            advanced = true;
            return Task.CompletedTask;
        });
        var checker = CreateChecker((_, _) =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(200)); // time to headers
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        });

        var result = await checker.CheckAsync(Url);

        Assert.True(result.IsUp);
        Assert.Equal(500, result.ResponseTimeMs);
    }

    [Fact]
    public async Task Body_reading_stops_at_max_body_bytes()
    {
        _options.MaxBodyBytes = 100_000;
        var body = new TestStream(length: 50_000_000);
        var checker = CreateChecker((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

        var result = await checker.CheckAsync(Url);

        Assert.True(result.IsUp);
        Assert.InRange(body.BytesRead, 100_000, 100_000 + 81_920);
    }

    [Fact]
    public async Task Caller_cancellation_throws_instead_of_recording_a_timeout()
    {
        var checker = CreateChecker(HttpStatusCode.OK);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(Url, cts.Token));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            handler(request, ct);
    }

    /// <summary>A read-only stream of zeros that can run a callback on each read.</summary>
    private sealed class TestStream(long length = 1024, Func<CancellationToken, Task>? onRead = null) : Stream
    {
        public long BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (onRead is not null) await onRead(ct);
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..count].Clear();
            BytesRead += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
