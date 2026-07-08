using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class RestAuthResolveTests
    {
        [Fact]
        public void Resolve_RootRequest_FallsBackToCollection()
        {
            var coll = new RestCollection { AuthType = 1, AuthUsername = "", AuthSecret = "coll-token" };
            var (type, user, secret, source) = RestAuthResolve.Resolve(coll, "");
            Assert.Equal(1, type);
            Assert.Equal("coll-token", secret);
            Assert.Null(source);   // rozwiązano do kolekcji, nie folderu
        }

        [Fact]
        public void Resolve_FolderWithExplicitAuth_StopsThere_IgnoresCollection()
        {
            var folder = new RestFolder { Name = "F", AuthType = 2, AuthUsername = "u", AuthSecret = "p" };
            var coll = new RestCollection { AuthType = 1, AuthSecret = "coll-token", Folders = { folder } };
            var (type, user, secret, source) = RestAuthResolve.Resolve(coll, folder.Id);
            Assert.Equal(2, type);
            Assert.Equal("u", user);
            Assert.Equal("p", secret);
            Assert.Same(folder, source);
        }

        [Fact]
        public void Resolve_FolderInherit_WalksUpToParentFolder()
        {
            var parent = new RestFolder { Name = "Parent", AuthType = 1, AuthSecret = "parent-token" };
            var child = new RestFolder { Name = "Child", ParentId = parent.Id, AuthType = RestAuthResolve.Inherit };
            var coll = new RestCollection { Folders = { parent, child } };
            var (type, _, secret, source) = RestAuthResolve.Resolve(coll, child.Id);
            Assert.Equal(1, type);
            Assert.Equal("parent-token", secret);
            Assert.Same(parent, source);
        }

        [Fact]
        public void Resolve_AllFoldersInherit_FallsBackToCollectionRoot()
        {
            var parent = new RestFolder { Name = "Parent", AuthType = RestAuthResolve.Inherit };
            var child = new RestFolder { Name = "Child", ParentId = parent.Id, AuthType = RestAuthResolve.Inherit };
            var coll = new RestCollection { AuthType = 2, AuthUsername = "root-user", AuthSecret = "root-pass", Folders = { parent, child } };
            var (type, user, secret, source) = RestAuthResolve.Resolve(coll, child.Id);
            Assert.Equal(2, type);
            Assert.Equal("root-user", user);
            Assert.Equal("root-pass", secret);
            Assert.Null(source);
        }

        [Fact]
        public void Resolve_UnknownStartFolderId_TreatedAsRoot()
        {
            var coll = new RestCollection { AuthType = 0 };
            var (type, _, _, source) = RestAuthResolve.Resolve(coll, "does-not-exist");
            Assert.Equal(0, type);
            Assert.Null(source);
        }

        [Fact]
        public void Resolve_CyclicParentId_DoesNotHangAndFallsBackToCollection()
        {
            // Zepsute dane (np. ręcznie edytowany JSON): A wskazuje na B, B wskazuje na A. Bez ochrony
            // przed cyklem Resolve wisiałby w nieskończonej pętli (A9 z przeglądu).
            var a = new RestFolder { Name = "A", AuthType = RestAuthResolve.Inherit };
            var b = new RestFolder { Name = "B", AuthType = RestAuthResolve.Inherit, ParentId = a.Id };
            a.ParentId = b.Id;
            var coll = new RestCollection { AuthType = 2, AuthSecret = "root-pass", Folders = { a, b } };

            var (type, _, secret, source) = RestAuthResolve.Resolve(coll, a.Id);

            Assert.Equal(2, type);
            Assert.Equal("root-pass", secret);
            Assert.Null(source);
        }
    }
}
