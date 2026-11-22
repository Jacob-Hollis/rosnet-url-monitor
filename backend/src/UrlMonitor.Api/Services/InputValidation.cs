using System.Net.Mail;

namespace UrlMonitor.Api.Services;

/// <summary>Validates and normalizes user input for URLs and emails.</summary>
public static class InputValidation
{
    public const int MaxUrlLength = 2048;
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 320;

    /// <summary>Absolute http/https URLs only (design.md question 14). Returns the trimmed URL or an error.</summary>
    public static bool TryNormalizeUrl(string? input, out string url, out string error)
    {
        url = input?.Trim() ?? "";
        error = "";

        if (url.Length == 0)
        {
            error = "URL is required.";
            return false;
        }
        if (url.Length > MaxUrlLength)
        {
            error = $"URL must be at most {MaxUrlLength} characters.";
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            error = "URL must be an absolute http:// or https:// address, e.g. https://example.com.";
            return false;
        }

        return true;
    }

    /// <summary>Returns the trimmed name (null if blank) or an error.</summary>
    public static bool TryNormalizeName(string? input, out string? name, out string error)
    {
        name = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        error = "";
        if (name is { Length: > MaxNameLength })
        {
            error = $"Name must be at most {MaxNameLength} characters.";
            return false;
        }
        return true;
    }

    /// <summary>Format check only (no confirmation email). Returns the trimmed, lower-cased address or an error.</summary>
    public static bool TryNormalizeEmail(string? input, out string email, out string error)
    {
        email = input?.Trim().ToLowerInvariant() ?? "";
        error = "";

        if (email.Length == 0)
        {
            error = "Email is required.";
            return false;
        }
        if (email.Length > MaxEmailLength ||
            !MailAddress.TryCreate(email, out var parsed) ||
            parsed.Address != email ||          // rejects "Name <a@b.com>" and similar forms
            !parsed.Host.Contains('.') && parsed.Host != "localhost")
        {
            error = "Enter a valid email address, e.g. ops@example.com.";
            return false;
        }

        return true;
    }
}
