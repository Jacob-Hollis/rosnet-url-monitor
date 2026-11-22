using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Services;

public class InputValidationTests
{
    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("  http://example.com/health?x=1  ", "http://example.com/health?x=1")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    public void Valid_urls(string input, string expected)
    {
        Assert.True(InputValidation.TryNormalizeUrl(input, out var url, out _));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    public void Invalid_urls(string? input) =>
        Assert.False(InputValidation.TryNormalizeUrl(input, out _, out var error) || error.Length == 0);

    [Fact]
    public void Url_too_long_is_rejected() =>
        Assert.False(InputValidation.TryNormalizeUrl("https://example.com/" + new string('a', 2100), out _, out _));

    [Theory]
    [InlineData("ops@example.com", "ops@example.com")]
    [InlineData("  Ops.Team@Example.COM ", "ops.team@example.com")]
    [InlineData("me@localhost", "me@localhost")]
    public void Valid_emails(string input, string expected)
    {
        Assert.True(InputValidation.TryNormalizeEmail(input, out var email, out _));
        Assert.Equal(expected, email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing@tld")]
    [InlineData("@example.com")]
    [InlineData("Ops <ops@example.com>")]
    [InlineData("a@b.com, c@d.com")]
    public void Invalid_emails(string? input) =>
        Assert.False(InputValidation.TryNormalizeEmail(input, out _, out _));

    [Fact]
    public void Blank_name_becomes_null()
    {
        Assert.True(InputValidation.TryNormalizeName("   ", out var name, out _));
        Assert.Null(name);
    }

    [Fact]
    public void Long_name_is_rejected() =>
        Assert.False(InputValidation.TryNormalizeName(new string('x', 201), out _, out _));
}
