using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JavaLanguageSupport.ProjectSystem.PackageManager;

internal static class Program
{
    private const string ArtifactJson = @"{
      ""response"": {""numFound"": 2, ""docs"": [
        {""g"": ""com.google.code.gson"", ""a"": ""gson"", ""latestVersion"": ""2.13.1"",
         ""p"": ""jar"", ""versionCount"": 30, ""timestamp"": 1735689600000,
         ""ec"": ["".pom"", "".jar"", ""-sources.jar""]},
        {""g"": ""some.example"", ""a"": ""gson-wrapper"", ""latestVersion"": ""1.0"", ""p"": ""jar""}
      ]}}";
    private const string VersionsJson = @"{
      ""response"": {""numFound"": 3, ""docs"": [
        {""g"": ""com.google.code.gson"", ""a"": ""gson"", ""v"": ""2.13.1"", ""p"": ""jar"", ""timestamp"": 1735689600000},
        {""g"": ""com.google.code.gson"", ""a"": ""gson"", ""v"": ""2.13.0"", ""p"": ""jar"", ""timestamp"": 1735689500000}
      ]}}";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "--live", StringComparison.OrdinalIgnoreCase))
                await LiveAsync(args.Length > 1 ? args[1] : "gson");
            else if (args.Length == 0 || string.Equals(args[0], "--offline", StringComparison.OrdinalIgnoreCase))
                await OfflineAsync();
            else
            {
                Console.Error.WriteLine("Uso: dotnet run -- [--offline | --live gson]");
                return 2;
            }
            Console.WriteLine("F4.F.1: TEST OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("F4.F.1: TEST FALLITO: " + ex);
            return 1;
        }
    }

    private static void Assert(bool ok, string why)
    {
        if (!ok) throw new Exception("Assert failed: " + why);
    }

    private static async Task OfflineAsync()
    {
        var urls = new List<Uri>();
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            urls.Add(request.RequestUri);
            return ArtifactResponse(request);
        })))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            var response = await service.SearchAsync("gson", 1, 2);
            Assert(response.TotalCount == 2 && response.Items.Count == 2, "search count");
            Assert(response.Items[0].Coordinate == "com.google.code.gson:gson", "coordinates");
            Assert(response.Items[0].LatestVersion == "2.13.1", "latest version");
            Assert(response.Items[0].Extensions.Contains("-sources.jar"), "sources metadata");
            Assert(!response.HasNextPage, "pagination");
            Assert(urls[0].Scheme == "https" && urls[0].Host == "search.maven.org", "HTTPS host");
            Assert(Query(urls[0], "q") == "\"gson\"", "safe text search");
            Assert(Query(urls[0], "rows") == "2" && Query(urls[0], "start") == "0", "paging arguments");
            var secondPage = await service.SearchAsync("com.google.code.gson:gson", 2, 1);
            Assert(secondPage.TotalCount == 1 && secondPage.Items.Count == 0,
                "empty second page for single exact coordinate");
            Assert(Query(urls[1], "q") == "g:\"com.google.code.gson\" AND a:\"gson\"", "coordinate search");
            Assert(Query(urls[1], "start") == "1", "next page offset");
            await service.SearchAsync("gson OR *:*", 1, 10);
            Assert(Query(urls[2], "q").Contains("\\*\\:\\*"), "Solr meta-characters escaped");
            var exact = await service.GetArtifactAsync("com.google.code.gson", "gson");
            Assert(exact != null && exact.GroupId == "com.google.code.gson", "get artifact");
        }
        using (var http = new HttpClient(new FakeHandler(request => Ok(VersionsJson))))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            var versions = await service.GetVersionsAsync("com.google.code.gson", "gson", 1, 2);
            Assert(versions.TotalCount == 3 && versions.Items.Count == 2 && versions.HasNextPage, "version page");
            Assert(versions.Items[0].Coordinate == "com.google.code.gson:gson:2.13.1", "version coordinates");
        }
        using (var http = new HttpClient(new FakeHandler(request =>
            new HttpResponseMessage((HttpStatusCode)429))))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            try { await service.SearchAsync("gson"); throw new Exception("HTTP 429 not detected"); }
            catch (JavaMavenCentralSearchException ex) { Assert(ex.StatusCode == 429 && ex.IsTransient, "429 status"); }
        }
        using (var http = new HttpClient(new FakeHandler(request => Ok("not json"))))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            try { await service.SearchAsync("gson"); throw new Exception("invalid JSON not detected"); }
            catch (JavaMavenCentralSearchException ex) { Assert(ex.Message.Contains("JSON"), "JSON error"); }
        }
        using (var http = new HttpClient(new FakeHandler(request => Ok(new string('a', 2 * 1024 * 1024 + 20)))))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            try { await service.SearchAsync("gson"); throw new Exception("large response not detected"); }
            catch (JavaMavenCentralSearchException ex) { Assert(ex.Message.Contains("troppo grande"), "bounded HTTP response"); }
        }
        using (var http = new HttpClient(new FakeHandler(request => Ok(ArtifactJson))))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await service.SearchAsync("gson", cancellationToken: canceled.Token);
                    throw new Exception("cancellation not detected"); }
                catch (OperationCanceledException) { /* Expected. */ }
            }
            try { await service.GetVersionsAsync("bad/group", "gson");
                throw new Exception("invalid coordinates accepted"); }
            catch (ArgumentException) { /* Expected. */ }
            try { await service.SearchAsync("gson", pageSize: 100);
                throw new Exception("unbounded page size accepted"); }
            catch (ArgumentOutOfRangeException) { /* Expected. */ }
        }
        // F4F25-CACHE-RETRY-OFFLINE: one bounded retry for transient 503,
        // successful response cached; no retries after a 429.
        int transientCalls = 0;
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            transientCalls++;
            return transientCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : ArtifactResponse(request);
        })))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            var result = await service.SearchAsync("gson", 1, 2);
            Assert(result.Items.Count == 2 && transientCalls == 2, "bounded retry on 503");
            result = await service.SearchAsync("gson", 1, 2);
            Assert(result.Items.Count == 2 && transientCalls == 2, "cache prevents repeat HTTP");
        }
        int rateLimitCalls = 0;
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            rateLimitCalls++;
            return new HttpResponseMessage((HttpStatusCode)429);
        })))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            try { await service.SearchAsync("gson"); throw new Exception("429 not detected"); }
            catch (JavaMavenCentralSearchException ex) { Assert(ex.StatusCode == 429, "429 status"); }
            Assert(rateLimitCalls == 1, "no retry on 429");
        }
        using (var slow = new SlowFirstHandler())
        using (var http = new HttpClient(slow))
        using (var service = new JavaMavenCentralSearchService(
            http, TimeSpan.FromMilliseconds(100)))
        {
            var recovered = await service.SearchAsync("gson", 1, 2);
            Assert(recovered.Items.Count == 2 && slow.Calls == 2,
                "timeout then successful bounded retry");
        }
        // F4F27-METADATA-ACCEPTANCE: a single repository metadata response
        // supplies every page; the search index is not queried for versions.
        int metadataRequests = 0;
        int solrVersionRequests = 0;
        string metadataXml = "<metadata><groupId>com.google.code.gson</groupId>" +
            "<artifactId>gson</artifactId><versioning><versions>" +
            string.Join("", Enumerable.Range(1, 67)
                .Select(i => "<version>1." + i + "</version>")) +
            "</versions></versioning></metadata>";
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri.Host == "repo.maven.apache.org")
            {
                metadataRequests++;
                Assert(request.RequestUri.AbsolutePath ==
                    "/maven2/com/google/code/gson/gson/maven-metadata.xml", "metadata path");
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(metadataXml, Encoding.UTF8, "application/xml") };
            }
            solrVersionRequests++;
            return Ok(VersionsJson);
        })))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            var one = await service.GetVersionsAsync("com.google.code.gson", "gson", 1, 30);
            var two = await service.GetVersionsAsync("com.google.code.gson", "gson", 2, 30);
            var three = await service.GetVersionsAsync("com.google.code.gson", "gson", 3, 30);
            Assert(one.TotalCount == 67 && one.Items.Count == 30 && one.HasNextPage,
                "metadata first page");
            Assert(two.Items.Count == 30 && three.Items.Count == 7 &&
                !three.HasNextPage, "metadata last page");
            Assert(one.Items[0].Version == "1.67" && three.Items[6].Version == "1.1",
                "metadata version order");
            Assert(metadataRequests == 1 && solrVersionRequests == 0,
                "single download, local paging, cached catalog");
        }
        int fallbackRequests = 0;
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            fallbackRequests++;
            return request.RequestUri.Host == "repo.maven.apache.org"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Ok(VersionsJson);
        })))
        using (var service = new JavaMavenCentralSearchService(http))
        {
            var page = await service.GetVersionsAsync("com.google.code.gson", "gson", 1, 2);
            Assert(page.TotalCount == 3 && page.Items.Count == 2 &&
                fallbackRequests == 2, "Solr fallback only when metadata missing");
        }
        Console.WriteLine("Offline F4.F.2.7: metadata 67 versioni, paginazione locale, cache, fallback OK.");
        Console.WriteLine("Offline: ricerca, coordinate, versioni, pagine, annullamento, HTTP 429, JSON, cache, retry, timeout OK.");
    }

    // The fake must obey Solr rows/start and exact-coordinate filtering.
    // Returning all fixture documents to a one-row query is a malformed server
    // response: the production engine correctly rejects it.
    private static HttpResponseMessage ArtifactResponse(HttpRequestMessage request)
    {
        var root = JObject.Parse(ArtifactJson);
        var response = (JObject)root["response"];
        var documents = ((JArray)response["docs"]).OfType<JObject>();
        string q = Query(request.RequestUri, "q");
        if (q == "g:\"com.google.code.gson\" AND a:\"gson\"")
            documents = documents.Where(doc =>
                (string)doc["g"] == "com.google.code.gson" &&
                (string)doc["a"] == "gson");
        var all = documents.ToList();
        int rows = int.Parse(Query(request.RequestUri, "rows"),
            CultureInfo.InvariantCulture);
        int start = int.Parse(Query(request.RequestUri, "start"),
            CultureInfo.InvariantCulture);
        response["numFound"] = all.Count;
        response["docs"] = new JArray(all.Skip(start).Take(rows)
            .Select(doc => doc.DeepClone()));
        return Ok(root.ToString(Formatting.None));
    }

    private static string Query(Uri uri, string parameter)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&'))
        {
            int equals = part.IndexOf('=');
            if (equals > 0 && part.Substring(0, equals) == parameter)
                return Uri.UnescapeDataString(part.Substring(equals + 1).Replace('+', ' '));
        }
        return null;
    }

    private static HttpResponseMessage Ok(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static async Task LiveAsync(string term)
    {
        using (var service = new JavaMavenCentralSearchService())
        {
            var artifacts = await service.SearchAsync(term, 1, 10);
            Console.WriteLine("Maven Central: '" + term + "'; trovati=" + artifacts.TotalCount);
            foreach (var item in artifacts.Items.Take(5))
                Console.WriteLine("  " + item.Coordinate + "   " + item.LatestVersion);
            Assert(artifacts.TotalCount > 0 && artifacts.Items.Count > 0,
                "live artifact search should return results");
            var versions = await service.GetVersionsAsync("com.google.code.gson", "gson", 1, 5);
            Console.WriteLine("Versioni Gson: " + versions.TotalCount);
            foreach (var item in versions.Items.Take(3))
                Console.WriteLine("  " + item.Coordinate);
            Assert(versions.TotalCount > 0 && versions.Items.Count > 0, "live versions");
        }
    }

    private sealed class SlowFirstHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return ArtifactResponse(request);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> response;
        internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            this.response = response;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
