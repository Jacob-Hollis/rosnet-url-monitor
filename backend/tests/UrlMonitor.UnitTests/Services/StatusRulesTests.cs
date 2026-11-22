using UrlMonitor.Api.Services;

namespace UrlMonitor.UnitTests.Services;

public class StatusRulesTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(299)]
    [InlineData(401)]
    [InlineData(403)]
    public void Up_statuses(int status) => Assert.True(StatusRules.IsUp(status));

    [Theory]
    [InlineData(100)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(304)]
    [InlineData(307)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void Down_statuses(int status) => Assert.False(StatusRules.IsUp(status));
}
