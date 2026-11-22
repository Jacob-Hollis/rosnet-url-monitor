namespace UrlMonitor.Api.Services;

/// <summary>Decides whether an HTTP status code counts as "up" (design.md, questions 1 and 2).</summary>
public static class StatusRules
{
    /// <summary>
    /// Up: 2xx, 401, 403 (the server is responding, it just wants auth).
    /// Down: 3xx (redirects mean the saved URL should be updated), any other 4xx, 5xx.
    /// </summary>
    public static bool IsUp(int statusCode) =>
        statusCode is >= 200 and <= 299 or 401 or 403;
}
