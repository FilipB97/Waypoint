using System.Linq;
using RdpManager;
using Xunit;

namespace RdpManager.Tests
{
    // Kolorowanie odpowiedzi JSON: tokenizer dzieli tekst na segmenty (tekst + rodzaj).
    public class RestJsonColorizerTests
    {
        [Fact]
        public void Tokenize_DistinguishesKeyFromStringValue()
        {
            var segs = RestJsonColorizer.Tokenize("{\"name\": \"Filip\"}");
            Assert.Contains(segs, s => s.Kind == RestJsonTok.Key && s.Text == "\"name\"");
            Assert.Contains(segs, s => s.Kind == RestJsonTok.Str && s.Text == "\"Filip\"");
        }

        [Fact]
        public void Tokenize_ClassifiesNumbersAndKeywords()
        {
            var segs = RestJsonColorizer.Tokenize("{\"id\": 12, \"ok\": true, \"x\": null}");
            Assert.Contains(segs, s => s.Kind == RestJsonTok.Num && s.Text == "12");
            Assert.Contains(segs, s => s.Kind == RestJsonTok.Keyword && s.Text == "true");
            Assert.Contains(segs, s => s.Kind == RestJsonTok.Keyword && s.Text == "null");
        }

        [Fact]
        public void Tokenize_IsLossless_ConcatEqualsInput()
        {
            var input = "{\n  \"users\": [ { \"id\": 1, \"name\": \"A. B.\", \"active\": true } ],\n  \"total\": 2\n}";
            var segs = RestJsonColorizer.Tokenize(input);
            Assert.Equal(input, string.Concat(segs.Select(s => s.Text)));
        }

        [Fact]
        public void Tokenize_HandlesEscapedQuoteInsideString()
        {
            var segs = RestJsonColorizer.Tokenize("\"a\\\"b\": 1");
            Assert.Equal("\"a\\\"b\"", segs[0].Text);   // ucieczka nie kończy łańcucha
            Assert.Equal(RestJsonTok.Key, segs[0].Kind);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Tokenize_Empty_ReturnsEmpty(string input)
            => Assert.Empty(RestJsonColorizer.Tokenize(input));
    }
}
