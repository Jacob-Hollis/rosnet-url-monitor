namespace UrlMonitor.Api.Security;

/// <summary>
/// Blocks requests that change data (POST, PUT, PATCH, DELETE) when a browser says they come from another site.
/// The API has no login, so without this any web page you visit could, for example, POST to
/// http://localhost:5265/api/alerts/test with a plain form and spam every alert recipient.
/// <list type="bullet">
/// <item><c>Sec-Fetch-Site: cross-site</c> or <c>same-site</c> (set by modern browsers, can't be faked by a page) → blocked.</item>
/// <item><c>Origin</c> present and not a localhost address (older browsers; also <c>Origin: null</c>) → blocked.</item>
/// <item>Neither header (curl, scripts, the integration tests) → allowed: only browsers make cross-site requests.</item>
/// </list>
/// The UI calls the API through the Vite dev proxy, so its requests are same-origin and allowed.
/// Reads (GET) aren't blocked: they change nothing, and browsers already stop other sites reading the responses.
/// </summary>
public static class CrossSiteRequestGuard
{
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "[::1]"];

    public static IApplicationBuilder UseCrossSiteRequestGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsBlocked(context.Request))
            {
                await Results.Problem(
                        "Cross-site requests that change data are not allowed.",
                        statusCode: StatusCodes.Status403Forbidden)
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    public static bool IsBlocked(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase) ||
            fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0 && !IsLocalOrigin(origin);
    }

    private static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        LocalHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}
