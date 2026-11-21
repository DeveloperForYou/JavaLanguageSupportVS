using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.ProjectSystem.PackageManager;

internal sealed class FakeHandler : HttpMessageHandler
{
    internal readonly List<string> Seen = new List<string>();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        lock (Seen) Seen.Add(request.RequestUri.AbsoluteUri);
        string host = request.RequestUri.Host;
        string content = host == "central.test" ? Metadata("1.0", "2.0", "2.1") :
            host == "spring.test" ? Metadata("2.1", "3.0-M1", "3.0") : null;
        if (host == "denied.test") return Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        if (content == null) return Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(content) });
    }
    private static string Metadata(params string[] versions)
    {
        return "<metadata><groupId>com.example</groupId><artifactId>sample</artifactId>" +
            "<versioning><versions>" + string.Concat(versions.Select(v =>
                "<version>" + v + "</version>")) + "</versions></versioning></metadata>";
    }
}

internal static class Program
{
    static JavaPackageSource Source(string id, string url, bool effective=true)
    {
        return new JavaPackageSource { Id=id, Name=id, Url=new Uri(url),
            Effective=effective, Releases=true, Snapshots=true, Origin="test" };
    }
    private static void Assert(bool condition, string label)
    { if (!condition) throw new Exception("FAILED: " + label); Console.WriteLine("OK: " + label); }
    private static async Task RunAsync()
    {
        var handler=new FakeHandler();
        using(var client=new HttpClient(handler))
        using(var service=new JavaPackageMultiRepositorySearchService(client))
        {
            var origins=new[] {
                Source("central", "https://central.test/maven2/"),
                Source("spring", "https://spring.test/maven2/"),
                Source("inactive", "https://inactive.test/maven2/", false),
                Source("denied", "https://denied.test/maven2/"),
                Source("http", "http://insecure.test/maven2/")
            };
            var first=await service.GetCatalogAsync("com.example", "sample", origins,
                CancellationToken.None);
            Assert(first.Versions.Count==4, "deduplicazione versioni");
            Assert(first.Versions[0]=="3.0", "ordinamento versione stabile");
            Assert(first.VersionOrigins["2.1"].Contains("central") &&
                   first.VersionOrigins["2.1"].Contains("spring"), "provenienza multipla");
            Assert(first.Warnings.Any(x=>x.Contains("Accesso negato")), "401 senza password nei log");
            Assert(handler.Seen.All(x=>!x.Contains("inactive.test") && !x.Contains("insecure.test")),
                "repository inattivi/non HTTPS esclusi");
            Assert(handler.Seen.Any(x=>x.EndsWith("/com/example/sample/maven-metadata.xml")),
                "path metadata Maven corretto");
            int before=handler.Seen.Count;
            var second=await service.GetCatalogAsync("com.example", "sample", origins,
                CancellationToken.None);
            Assert(second.Versions.Count==4, "risultato ripetuto");
            Assert(handler.Seen.Count == before+1, "cache per repository validi (401 non memorizzato)");
            string group, artifact;
            Assert(JavaPackageMultiRepositorySearchService.TryCoordinate("com.example:sample", out group,
                out artifact) && group=="com.example", "ricerca coordinate");
            Assert(!JavaPackageMultiRepositorySearchService.TryCoordinate("guava", out group,
                out artifact), "testo libero non diventa coordinate");
            try
            {
                await service.GetCatalogAsync("../../etc", "sample", origins, CancellationToken.None);
                throw new Exception("Coordinata malevola accettata");
            }
            catch (ArgumentException) { Console.WriteLine("OK: coordinate non valide rifiutate"); }
        }
    }
    public static int Main()
    {
        try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("ALL TESTS PASSED"); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
