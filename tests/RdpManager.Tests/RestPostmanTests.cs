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
        public void Parse_UrlencodedBody_KeepsPlaceholdersLiteral()
        {
            string json = @"{ ""info"": { ""name"": ""OAuth"" }, ""item"": [
                { ""name"": ""Token"", ""request"": {
                    ""method"": ""POST"",
                    ""url"": { ""raw"": ""https://x/oauth2/token"" },
                    ""body"": { ""mode"": ""urlencoded"", ""urlencoded"": [
                        { ""key"": ""username"", ""value"": ""{{username}}"" },
                        { ""key"": ""grant_type"", ""value"": ""password"" },
                        { ""key"": ""skip"", ""value"": ""x"", ""disabled"": true }
                    ] }
                } }
            ] }";
            var req = PostmanImport.Parse(json).Collection.Requests.First(x => x.Name == "Token");
            Assert.Equal("application/x-www-form-urlencoded", req.BodyContentType);
            Assert.Equal("username={{username}}&grant_type=password", req.Body);   // placeholder literalny, disabled pominięty

            Assert.Equal(3, req.FormFields.Count);   // tabela zachowuje TEŻ wyłączone pola (edytor je pokazuje)
            Assert.Contains(req.FormFields, f => f.Key == "username" && f.Value == "{{username}}" && f.Enabled);
            Assert.Contains(req.FormFields, f => f.Key == "grant_type" && f.Value == "password" && f.Enabled);
            Assert.Contains(req.FormFields, f => f.Key == "skip" && !f.Enabled);
        }

        [Fact]
        public void Parse_ImportsPrerequestAndTestScripts()
        {
            string json = @"{ ""info"": { ""name"": ""Auth"" }, ""item"": [
                { ""name"": ""Login"",
                  ""event"": [
                    { ""listen"": ""prerequest"", ""script"": { ""exec"": [ ""console.log('pre');"", ""pm.environment.set('x','1');"" ] } },
                    { ""listen"": ""test"", ""script"": { ""exec"": [ ""var d = JSON.parse(responseBody);"", ""postman.setEnvironmentVariable('token', d.access_token);"" ] } }
                  ],
                  ""request"": { ""method"": ""POST"", ""url"": { ""raw"": ""https://x/y"" } }
                }
            ] }";
            var req = PostmanImport.Parse(json).Collection.Requests.First();
            Assert.Equal("console.log('pre');\npm.environment.set('x','1');", req.PreScript);
            Assert.Equal("var d = JSON.parse(responseBody);\npostman.setEnvironmentVariable('token', d.access_token);", req.TestScript);
        }

        [Fact]
        public void Parse_NoEvents_LeavesScriptsEmpty()
        {
            var post = PostmanImport.Parse(Sample).Collection.Requests.First(x => x.Name == "Create");
            Assert.Equal("", post.PreScript);
            Assert.Equal("", post.TestScript);
        }

        // Auth na poziomie kolekcji i folderu + dziedziczenie nagłówków (kolekcja/folder → żądanie).
        private const string AuthHeadersSample = @"{
  ""info"": { ""name"": ""Poseidon"" },
  ""auth"": { ""type"": ""bearer"", ""bearer"": [ { ""key"": ""token"", ""value"": ""coll-token"" } ] },
  ""header"": [ { ""key"": ""X-Coll"", ""value"": ""c"" } ],
  ""item"": [
    {
      ""name"": ""Secured"",
      ""auth"": { ""type"": ""basic"", ""basic"": [ { ""key"": ""username"", ""value"": ""fu"" }, { ""key"": ""password"", ""value"": ""fp"" } ] },
      ""header"": [ { ""key"": ""X-Folder"", ""value"": ""f"" } ],
      ""item"": [
        { ""name"": ""In folder"", ""request"": {
            ""method"": ""GET"",
            ""header"": [ { ""key"": ""X-Trace"", ""value"": ""r"" }, { ""key"": ""X-Coll"", ""value"": ""own"" } ],
            ""url"": { ""raw"": ""https://x/y"" }
        } }
      ]
    }
  ]
}";

        [Fact]
        public void Parse_ImportsCollectionAuth_AsBearerWithSecret()
        {
            var r = PostmanImport.Parse(AuthHeadersSample);
            Assert.Equal(1, r.Collection.AuthType);          // Bearer na korzeniu dziedziczenia
            Assert.Equal("coll-token", r.CollectionSecret);  // sekret kolekcji zwrócony osobno (cel liczy wołający)
        }

        [Fact]
        public void Parse_ImportsFolderAuth_AsBasicWithSecret()
        {
            var r = PostmanImport.Parse(AuthHeadersSample);
            var folder = r.Collection.Folders.First(f => f.Name == "Secured");
            Assert.Equal(2, folder.AuthType);
            Assert.Equal("fu", folder.AuthUsername);
            Assert.Equal("fp", r.Secrets[folder.AuthCredTarget]);
        }

        [Fact]
        public void Parse_FlattensInheritedHeaders_RequestWins()
        {
            var req = PostmanImport.Parse(AuthHeadersSample).Collection.Requests.First(x => x.Name == "In folder");
            Assert.Contains(req.Headers, h => h.Key == "X-Trace" && h.Value == "r");    // własny
            Assert.Contains(req.Headers, h => h.Key == "X-Folder" && h.Value == "f");   // odziedziczony z folderu
            var xcoll = req.Headers.Where(h => h.Key == "X-Coll").ToList();
            Assert.Single(xcoll);                     // bez duplikatu z kolekcji
            Assert.Equal("own", xcoll[0].Value);      // nagłówek żądania wygrywa nad odziedziczonym
        }

        [Fact]
        public void Parse_NotACollection_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PostmanImport.Parse(@"{ ""foo"": 1 }"));
        }

        [Fact]
        public void ParseEnvironment_ReadsValues_BlanksSecrets()
        {
            string json = @"{ ""name"": ""Dev"", ""values"": [
                { ""key"": ""base_url"", ""value"": ""https://dev.example.com"", ""type"": ""default"" },
                { ""key"": ""token"", ""value"": ""abc"", ""type"": ""secret"" }
            ], ""_postman_variable_scope"": ""environment"" }";
            var env = PostmanImport.ParseEnvironment(json, out var blanked);
            Assert.Equal("Dev", env.Name);
            Assert.Contains(env.Variables, v => v.Key == "base_url" && v.Value == "https://dev.example.com");
            Assert.Equal("", env.Variables.First(v => v.Key == "token").Value);   // sekret wyzerowany
            Assert.Equal(new[] { "token" }, blanked);   // do ostrzeżenia w UI
        }

        [Fact]
        public void ParseEnvironment_NotAnEnvironment_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PostmanImport.ParseEnvironment(@"{ ""foo"": 1 }", out _));
        }

        // Rozpoznanie env vs kolekcja — wspólna karta importu „kolekcje i środowiska" kieruje plik
        // do właściwego parsera (env-eksport wywalał parser kolekcji na braku „item").
        [Fact]
        public void LooksLikeEnvironment_DistinguishesEnvFromCollection()
        {
            Assert.True(PostmanImport.LooksLikeEnvironment(
                @"{ ""name"": ""Dev"", ""values"": [ { ""key"": ""a"", ""value"": ""1"" } ] }"));
            Assert.False(PostmanImport.LooksLikeEnvironment(
                @"{ ""info"": { ""name"": ""Coll"" }, ""item"": [] }"));
            Assert.False(PostmanImport.LooksLikeEnvironment(@"{ ""foo"": 1 }"));          // ani env, ani kolekcja
            Assert.False(PostmanImport.LooksLikeEnvironment("nie json"));                  // nie-JSON → false, nie wyjątek
            Assert.False(PostmanImport.LooksLikeEnvironment(@"{ ""values"": 5 }"));        // „values" nie-tablica
        }
    }
}
