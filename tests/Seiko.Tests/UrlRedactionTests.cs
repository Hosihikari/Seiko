using Xunit;

namespace Seiko.Tests;

public sealed class UrlRedactionTests
{
    [Theory]
    [InlineData("GET http://demo:PASS,SECRET_SENTINEL@127.0.0.1/index.json?token=QUERY_SENTINEL")]
    [InlineData("GET http://demo:PASS;OTHER@127.0.0.1/index.json?token=QUERY_SENTINEL")]
    [InlineData("GET http://demo:PASS@[::1]:8080/index.json?token=QUERY_SENTINEL")]
    [InlineData("GET http://demo:PASS@127.0.0.1/index.json?a=b,c&d=[1;2]")]
    [InlineData("GET (http://demo:PASS@127.0.0.1/index.json?token=QUERY_SENTINEL) done")]
    [InlineData("GET http://demo:PASS@127.0.0.1/index.json?token=QUERY_SENTINEL).")]
    [InlineData("url=https://demo:PASS@example.invalid/a,b.json?x=QUERY, done")]
    [InlineData("bad http://demo:PASS@127.0.0.1:notaport/index.json?t=QUERY end")]
    public void RedactText_RemovesCredentialsFromEveryUrlShape(string text)
    {
        var result = Urls.RedactText(text);

        Assert.DoesNotContain("PASS", result);
        Assert.DoesNotContain("QUERY", result);
        Assert.DoesNotContain("SECRET_SENTINEL", result);
        Assert.DoesNotContain("OTHER", result);
    }

    [Fact]
    public void RedactText_KeepsTextAroundTheUrl()
    {
        var result = Urls.RedactText(
            "source 'x' index http://demo:PASS@example.invalid/a.json?a=1 done");

        Assert.Equal("source 'x' index http://example.invalid/a.json done", result);
    }

    [Fact]
    public void RedactText_KeepsAnEmptyIpv6()
    {
        var result = Urls.RedactText("GET http://[::1]:8080/a.json?t=Q");

        Assert.Equal("GET http://[::1]:8080/a.json", result);
    }

    [Theory]
    [InlineData("http://demo:PASS,SECRET@127.0.0.1/index.json?token=QUERY")]
    [InlineData("http://demo:PASS@[::1]:8080/index.json?token=QUERY")]
    public void TryGetHttp_RedactsTheWholeUri(string url)
    {
        Assert.True(Urls.TryGetHttp(url, out var uri));

        var result = Urls.Redact(uri!);

        Assert.DoesNotContain("PASS", result);
        Assert.DoesNotContain("QUERY", result);
    }
}
