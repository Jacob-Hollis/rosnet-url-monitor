using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace UrlMonitor.Api.Services;

/// <summary>
/// Checks a URL with a GET request, timing the full body download.
/// The HttpClient must be configured not to follow redirects (see UrlCheckerServiceCollectionExtensions.AddUrlChecker).
/// </summary>
public class UrlChecker(HttpClient httpClient, IOptions<MonitorOptions> options, TimeProvider timeProvider) : IUrlChecker
{
    public const string UserAgent = "UrlMonitor/1.0";
    private const int MaxErrorLength = 1000;

    private readonly MonitorOptions _options = options.Value;

    public async Task<CheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
    {
        var checkedAt = timeProvider.GetUtcNow().UtcDateTime;
        var start = timeProvider.GetTimestamp();
        int Elapsed() => (int)Math.Round(timeProvider.GetElapsedTime(start).TotalMilliseconds);

        // One timeout for the whole check (connect + headers + body), separate from the caller's token
        // so we can tell "timed out" apart from "app is shutting down".
        using var timeoutCts = new CancellationTokenSource(_options.Timeout, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        int? statusCode = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            statusCode = (int)response.StatusCode;

            await ReadBodyAsync(response, linkedCts.Token);
            var elapsed = Elapsed();

            var isUp = StatusRules.IsUp(statusCode.Value);
            return new CheckResult(checkedAt, statusCode, elapsed, isUp, isUp ? null : DescribeStatus(response, request.RequestUri!));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Caller cancelled (shutdown): don't record a fake "timeout".
        }
        catch (OperationCanceledException)
        {
            return Down($"Timeout after {_options.TimeoutSeconds:0.##}s");
        }
        catch (HttpRequestException ex)
        {
            return Down(DescribeRequestError(ex));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UriFormatException)
        {
            // IOException: connection dropped while reading the body. The others: malformed URL.
            return Down(Truncate(ex.Message));
        }

        CheckResult Down(string error) => new(checkedAt, statusCode, Elapsed(), false, error);
    }

    /// <summary>Reads and discards the body, stopping after <see cref="MonitorOptions.MaxBodyBytes"/>.</summary>
    private async Task ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while (total < _options.MaxBodyBytes &&
               (read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
        }
    }

    private static string DescribeStatus(HttpResponseMessage response, Uri requestUri)
    {
        var code = (int)response.StatusCode;
        var text = $"HTTP {code} {response.ReasonPhrase}".TrimEnd();

        if (code is >= 300 and <= 399 && response.Headers.Location is { } location)
        {
            // Show where it redirects so the user knows what to change the URL to.
            var target = location.IsAbsoluteUri
                ? location.ToString()
                : new Uri(requestUri, location).ToString();
            text += $" - redirects to {target}";
        }

        return Truncate(text);
    }

    private static string DescribeRequestError(HttpRequestException ex) => Truncate(ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "DNS failure: host not found",
        HttpRequestError.ConnectionError => $"Connection failed: {InnermostMessage(ex)}",
        HttpRequestError.SecureConnectionError => $"TLS error: {InnermostMessage(ex)}",
        HttpRequestError.ResponseEnded => "Connection closed before the response completed",
        _ => InnermostMessage(ex),
    });

    private static string InnermostMessage(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    private static string Truncate(string s) => s.Length <= MaxErrorLength ? s : s[..MaxErrorLength];
}

public static class UrlCheckerServiceCollectionExtensions
{
    public static IServiceCollection AddUrlChecker(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<IUrlChecker, UrlChecker>(client =>
            {
                // UrlChecker enforces its own timeout so it can tell timeouts apart from shutdown.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UrlChecker.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A redirect counts as down (design.md question 2), so record the 3xx itself.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            });

        return services;
    }

}
