using System.IO;
using System.Linq;
using RdpManager;
using Xunit;

namespace RdpManager.Tests
{
    // Okruszki ścieżki. Regresja: cel każdego okruszka powstawał ze sklejki "/" + segment, co zakładało
    // ścieżkę POSIX-ową. Panel LOKALNY używa ścieżek windowsowych z ukośnikami w przód i BEZ wiodącego
    // ukośnika ("C:/Apps"), więc klik w „C:" prowadził do „/C:", a Windows rozwijał to względem katalogu
    // bieżącego dysku — stąd błąd „Nazwa pliku… jest niepoprawna. : 'C:\C:'".
    public class FileTransferBreadcrumbTests
    {
        [Fact]
        public void SciezkaWindows_CelemJestPrefiksANieSklejkaZUkosnikiem()
        {
            var segs = FileTransferPanel.BreadcrumbSegments("C:/Apps");

            Assert.Equal(2, segs.Count);
            Assert.Equal(("C:", "C:"), (segs[0].Label, segs[0].Target));
            Assert.Equal(("Apps", "C:/Apps"), (segs[1].Label, segs[1].Target));
        }

        [Fact]
        public void SciezkaWindows_ZadenCelNieDostajeWiodacegoUkosnika()
        {
            // Niezmiennik, który pękł: dla ścieżki bez wiodącego ukośnika żaden cel nie może go zyskać.
            Assert.All(FileTransferPanel.BreadcrumbSegments("D:/dane/2026/raporty"),
                       s => Assert.False(s.Target.StartsWith("/")));
        }

        [Fact]
        public void SciezkaPosix_CeleNarastajaOdKorzenia()
        {
            var segs = FileTransferPanel.BreadcrumbSegments("/repository/internal/pl/OMPro");

            Assert.Equal(new[] { "repository", "internal", "pl", "OMPro" }, segs.Select(s => s.Label));
            Assert.Equal(new[] { "/repository", "/repository/internal", "/repository/internal/pl",
                                 "/repository/internal/pl/OMPro" }, segs.Select(s => s.Target));
        }

        [Fact]
        public void KorzenDysku_DajeJedenOkruszekBezPustego()
        {
            var segs = FileTransferPanel.BreadcrumbSegments("C:/");
            Assert.Single(segs);
            Assert.Equal(("C:", "C:"), (segs[0].Label, segs[0].Target));
        }

        [Theory]
        [InlineData("/")]
        [InlineData("")]
        [InlineData(null)]
        public void KorzenIPustaSciezka_NieDajaOkruszkow(string path)
            => Assert.Empty(FileTransferPanel.BreadcrumbSegments(path));

        [Fact]
        public void PodwojneUkosnikiNieTworzaPustychOkruszkow()
        {
            var segs = FileTransferPanel.BreadcrumbSegments("//a//b");
            Assert.Equal(new[] { "a", "b" }, segs.Select(s => s.Label));
            Assert.Equal(new[] { "//a", "//a//b" }, segs.Select(s => s.Target));
        }

        [Fact]
        public void GolaLiteraDysku_JestListowalnaPrzezLocalFs()
        {
            // Domknięcie pętli: okruszek dysku prowadzi do „C:", a to musi być ścieżka, którą backend
            // umie wylistować. Zamianę na katalog główny robi LocalFs.Denorm — panel nie zna Windowsa.
            string root = Path.GetPathRoot(Path.GetTempPath());          // np. "C:\"
            string bare = root.TrimEnd('\\', '/');                        // "C:"
            var fs = new LocalFs();

            var fromBare = fs.List(bare).Select(e => e.FullName).OrderBy(x => x).ToArray();
            var fromRoot = fs.List(bare + "/").Select(e => e.FullName).OrderBy(x => x).ToArray();

            Assert.Equal(fromRoot, fromBare);
        }
    }
}
