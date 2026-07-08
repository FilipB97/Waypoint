using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class UrlValidationTests
    {
        [Fact]
        public void TryNormalizeWebUrl_BareHost_PrependsHttps()
        {
            Assert.True(UrlValidation.TryNormalizeWebUrl("example.com/panel", out var uri));
            Assert.Equal("https://example.com/panel", uri.AbsoluteUri);
        }

        [Fact]
        public void TryNormalizeWebUrl_ExplicitHttp_Allowed()
        {
            Assert.True(UrlValidation.TryNormalizeWebUrl("http://192.168.1.1:8080/", out var uri));
            Assert.Equal("http", uri.Scheme);
        }

        [Fact]
        public void TryNormalizeWebUrl_ExplicitHttps_Allowed()
        {
            Assert.True(UrlValidation.TryNormalizeWebUrl("https://example.com", out var uri));
            Assert.Equal("https", uri.Scheme);
        }

        [Theory]
        [InlineData("file:///C:/Windows/System32/cmd.exe")]
        [InlineData("javascript:alert(1)")]
        [InlineData("custom-handler://payload")]
        [InlineData("ftp://example.com/x")]
        public void TryNormalizeWebUrl_NonHttpScheme_Rejected(string url)
        {
            Assert.False(UrlValidation.TryNormalizeWebUrl(url, out var uri));
            Assert.Null(uri);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void TryNormalizeWebUrl_EmptyOrNull_Rejected(string url)
        {
            Assert.False(UrlValidation.TryNormalizeWebUrl(url, out var uri));
            Assert.Null(uri);
        }
    }
}
