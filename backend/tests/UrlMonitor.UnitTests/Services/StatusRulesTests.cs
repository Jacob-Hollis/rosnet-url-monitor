using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Services;

public class StatusRulesTests
{
    // Boundaries of the 2xx range, plus the two auth codes that count as up.
    [Theory]
    [InlineData(200)]
    [InlineData(299)]
    [InlineData(401)]
    [InlineData(403)]
    public void Up_statuses(int status) => Assert.True(StatusRules.IsUp(status));

    // Just outside 2xx, redirects, the 4xx codes either side of 401/403, and 5xx.
    [Theory]
    [InlineData(199)]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(402)]
    [InlineData(404)]
    [InlineData(500)]
    public void Down_statuses(int status) => Assert.False(StatusRules.IsUp(status));
}
