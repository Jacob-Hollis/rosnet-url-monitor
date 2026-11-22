using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Services;

public class InputValidationTests
{
    [Theory]
    [InlineData("  http://example.com/health?x=1  ", "http://example.com/health?x=1")] // trimmed
    [InlineData("http://localhost:8080", "http://localhost:8080")]                     // host + port
    public void Valid_urls(string input, string expected)
    {
        Assert.True(InputValidation.TryNormalizeUrl(input, out var url, out _));
        Assert.Equal(expected, url);
    }

    // One case per rejection reason: blank, not absolute, wrong scheme, no host, too long.
    [Theory]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("https://")]
    public void Invalid_urls(string input)
    {
        Assert.False(InputValidation.TryNormalizeUrl(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Url_too_long_is_rejected() =>
        Assert.False(InputValidation.TryNormalizeUrl("https://example.com/" + new string('a', 2100), out _, out _));

    [Theory]
    [InlineData("  Ops.Team@Example.COM ", "ops.team@example.com")] // trimmed + lower-cased
    [InlineData("me@localhost", "me@localhost")]                     // localhost allowed without a dot
    public void Valid_emails(string input, string expected)
    {
        Assert.True(InputValidation.TryNormalizeEmail(input, out var email, out _));
        Assert.Equal(expected, email);
    }

    // One case per rejection reason: blank, unparseable, host without a dot, display-name form.
    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing@tld")]
    [InlineData("Ops <ops@example.com>")]
    public void Invalid_emails(string input) =>
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
