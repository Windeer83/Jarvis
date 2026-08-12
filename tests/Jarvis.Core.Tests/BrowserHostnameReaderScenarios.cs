using Xunit;

namespace Jarvis.Core.Tests;

public sealed class BrowserHostnameReaderScenarios
{
    [Theory]
    [InlineData("https://Example.COM/private?q=secret#fragment", "example.com")]
    [InlineData("example.com/path", "example.com")]
    [InlineData("localhost:5000/path", "localhost")]
    [InlineData("chrome://settings", null)]
    [InlineData("search terms", null)]
    [InlineData("someone@example.com", null)]
    [InlineData("just-a-word", null)]
    public void Parser_releases_only_a_strict_hostname(string addressBarValue, string? expected) =>
        Assert.Equal(expected, BrowserHostnameReader.ParseHostname(addressBarValue));
}
