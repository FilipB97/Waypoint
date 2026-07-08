using System;
using System.Linq;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class RestPostmanTests
    {
        private const string Sample = @"{
  ""info"": { ""name"": ""My API"" },
  ""item"": [
    {
      ""name"": ""Users"",
      ""item"": [
        {
          ""name"": ""Get user"",
          ""request"": {
            ""method"": ""GET"",
            ""header"": [ { ""key"": ""Accept"", ""value"": ""application/json"" } ],
            ""url"": {
              ""raw"": ""https://api.example.com/users?id=1"",
              ""query"": [ { ""key"": ""id"", ""value"": ""1"" }, { ""key"": ""skip"", ""value"": ""x"", ""disabled"": true } ]
            },
            ""auth"": { ""type"": ""bearer"", ""bearer"": [ { ""key"": ""token"", ""value"": ""secret-token"" } ] }
          }
        }
      ]
    },
    {
      ""name"": ""Create"",
      ""request"": {
        ""method"": ""post"",
        ""url"": { ""raw"": ""https://api.example.com/users"" },
        ""body"": { ""mode"": ""raw"", ""raw"": ""[1,2,3]"", ""options"": { ""raw"": { ""language"": ""json"" } } },
        ""auth"": { ""type"": ""basic"", ""basic"": [ { ""key"": ""username"", ""value"": ""u"" }, { ""key"": ""password"", ""value"": ""p"" } ] }
      }
    }
  ],
  ""variable"": [ { ""key"": ""base_url"", ""value"": ""https://api.example.com"" } ]
}";

        [Fact]
        public void Parse_ReadsNameFoldersAndCounts()
        {
            var r = PostmanImport.Parse(Sample);
            Assert.Equal("My API", r.Name);
            Assert.Equal(2, r.RequestCount);
            Assert.Single(r.Collection.Folders);
            Assert.Equal("Users", r.Collection.Folders[0].Name);
        }

        [Fact]
        public void Parse_RequestInFolder_MapsFieldsAndBearer()
        {
            var r = PostmanImport.Parse(Sample);
            var folder = r.Collection.Folders[0];
            var get = r.Collection.Requests.First(x => x.Name == "Get user");

            Assert.Equal("GET", get.Method);
            Assert.Equal("https://api.example.com/users", get.Url);          // query odcięte do parametrów
            Assert.Equal(folder.Id, get.FolderId);
            Assert.Contains(get.QueryParams, p => p.Key == "id" && p.Value == "1" && p.Enabled);
            Assert.Contains(get.QueryParams, p => p.Key == "skip" && !p.Enabled);   // disabled → wyłączony
            Assert.Contains(get.Headers, h => h.Key == "Accept" && h.Value == "application/json");
            Assert.Equal(1, get.AuthType);
            Assert.Equal("secret-token", r.Secrets[get.AuthCredTarget]);
        }

        [Fact]
        public void Parse_RootRequest_MapsBodyContentTypeAndBasic()
        {
            var r = PostmanImport.Parse(Sample);
            var post = r.Collection.Requests.First(x => x.Name == "Create");

            Assert.Equal("POST", post.Method);                 // metoda znormalizowana do wielkich liter
            Assert.Equal("https://api.example.com/users", post.Url);
            Assert.Equal("[1,2,3]", post.Body);
            Assert.Equal("application/json", post.BodyContentType);
            Assert.Equal("", post.FolderId);                   // korzeń
            Assert.Equal(2, post.AuthType);
            Assert.Equal("u", post.AuthUsername);
            Assert.Equal("p", r.Secrets[post.AuthCredTarget]);
        }

        [Fact]
        public void Parse_ImportsCollectionVariablesAsEnvironment()
        {
            var r = PostmanImport.Parse(Sample);
            Assert.Single(r.Collection.Environments);
            var env = r.Collection.Environments[0];
            Assert.Contains(env.Variables, v => v.Key == "base_url" && v.Value == "https://api.example.com");
            Assert.Equal(env.Id, r.Collection.ActiveEnvironmentId);
        }

        [Fact]
        public void Parse_NotACollection_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PostmanImport.Parse(@"{ ""foo"": 1 }"));
        }
    }
}
