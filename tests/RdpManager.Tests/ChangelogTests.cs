using System.Linq;
using RdpManager;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Historia zmian: parsowanie notatek wydania (markdown → wpisy z rodzajem) + listy wydań GitHub.
    public class ChangelogTests
    {
        [Fact]
        public void ParseNotes_SectionHeaders_CategorizeItems()
        {
            var body = "### Nowe\n- Dodano panel X\n- Kolejna funkcja\n### Poprawki\n- Naprawiono Y";
            var items = Changelog.ParseNotes(body);
            Assert.Equal(3, items.Count);
            Assert.Equal(ChangeKind.New, items[0].Kind);
            Assert.Equal(ChangeKind.New, items[1].Kind);
            Assert.Equal(ChangeKind.Fix, items[2].Kind);
            Assert.Equal("Dodano panel X", items[0].Text);
        }

        [Fact]
        public void ParseNotes_NoSections_KeywordFallback()
        {
            var body = "- Naprawiono błąd zapisu\n- Zmiana układu ustawień\n- Nowy import z pliku";
            var items = Changelog.ParseNotes(body);
            Assert.Equal(ChangeKind.Fix, items[0].Kind);
            Assert.Equal(ChangeKind.Change, items[1].Kind);
            Assert.Equal(ChangeKind.New, items[2].Kind);
        }

        [Fact]
        public void ParseNotes_StripsPrefixesAndMarkdown()
        {
            var body = "- feat: **Świetna** rzecz\n- 🐛 fix: literówka";
            var items = Changelog.ParseNotes(body);
            Assert.Equal("Świetna rzecz", items[0].Text);
            Assert.Equal(ChangeKind.New, items[0].Kind);
            Assert.Equal("literówka", items[1].Text);
            Assert.Equal(ChangeKind.Fix, items[1].Kind);
        }

        [Fact]
        public void ParseNotes_IgnoresPlainParagraphs_AndRespectsMax()
        {
            var body = "To jest zwykły akapit bez punktu.\n- Punkt jeden\n- Punkt dwa";
            var items = Changelog.ParseNotes(body, max: 1);
            Assert.Single(items);
            Assert.Equal("Punkt jeden", items[0].Text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseNotes_EmptyBody_ReturnsEmpty(string body)
            => Assert.Empty(Changelog.ParseNotes(body));

        [Fact]
        public void ParseReleaseList_ParsesTagDateBody_SkipsDrafts()
        {
            var json = @"[
                {""tag_name"":""v1.8.0"",""published_at"":""2026-07-09T10:00:00Z"",""body"":""- Nowe""},
                {""tag_name"":""v1.7.0"",""draft"":true,""published_at"":""2026-07-01T10:00:00Z"",""body"":""x""},
                {""tag_name"":""1.6.0"",""published_at"":""2026-06-01T00:00:00Z"",""body"":""- Fix""}
            ]";
            var list = UpdateCheck.ParseReleaseList(json);
            Assert.Equal(2, list.Count);                 // szkic pominięty
            Assert.Equal("1.8.0", list[0].Version);      // „v" ścięte
            Assert.Equal("2026-07-09", list[0].Date);    // data przycięta do dnia
            Assert.Equal("1.6.0", list[1].Version);
        }

        [Fact]
        public void ParseReleaseList_BadJson_ReturnsEmpty()
        {
            Assert.Empty(UpdateCheck.ParseReleaseList("nie json"));
            Assert.Empty(UpdateCheck.ParseReleaseList("{\"not\":\"array\"}"));
        }
    }
}
