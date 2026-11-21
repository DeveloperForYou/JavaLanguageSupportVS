using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // F4F29-MULTI-REPOSITORY: read-only Maven-layout catalog query. No Gradle/Maven
    // project or configuration file is ever modified by this service.
    internal sealed class JavaRepositoryCatalogResult
    {
        internal JavaRepositoryCatalogResult(IReadOnlyList<string> versions,
            IDictionary<string, string> origins, IList<string> warnings, int queried)
        {
            Versions = versions;
            VersionOrigins = new Dictionary<string, string>(origins, StringComparer.OrdinalIgnoreCase);
            Warnings = warnings.ToList();
            RepositoriesQueried = queried;
        }
        internal IReadOnlyList<string> Versions { get; private set; }
        internal IReadOnlyDictionary<string, string> VersionOrigins { get; private set; }
        internal IReadOnlyList<string> Warnings { get; private set; }
        internal int RepositoriesQueried { get; private set; }
        internal string WarningText { get { return string.Join(" · ", Warnings.Take(4)); } }
    }

    internal sealed class JavaPackageMultiRepositorySearchService : IDisposable
    {
        private const int MaxRepositories = 12;
        private const int MaxMetadataBytes = 2 * 1024 * 1024;
        private static readonly Regex Identifier = new Regex(
            @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Coordinate = new Regex(
            @"\A(?<g>[A-Za-z0-9_][A-Za-z0-9_.-]*):(?<a>[A-Za-z0-9_][A-Za-z0-9_.-]*)\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly HttpClient client;
        private readonly bool ownsClient;
        private readonly object cacheGate = new object();
        private readonly Dictionary<string, CachedVersions> cache = new Dictionary<string, CachedVersions>(StringComparer.Ordinal);
        private bool disposed;

        private sealed class CachedVersions
        {
            internal DateTime ExpiresUtc;
            internal string[] Versions;
        }
        private sealed class SourceResponse
        {
            internal JavaPackageSource Source;
            internal string[] Versions;
            internal string Warning;
        }

        internal JavaPackageMultiRepositorySearchService()
            : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false,
                UseCookies = false, UseDefaultCredentials = false }), true) { }

        // Inject a fake HttpClient in acceptance tests; do not own an injected handler.
        internal JavaPackageMultiRepositorySearchService(HttpClient client)
            : this(client, false) { }

        private JavaPackageMultiRepositorySearchService(HttpClient client, bool ownsClient)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.ownsClient = ownsClient;
            if (ownsClient) client.Timeout = Timeout.InfiniteTimeSpan;
        }

        internal static bool TryCoordinate(string text, out string group, out string artifact)
        {
            group = artifact = null;
            var match = Coordinate.Match((text ?? "").Trim());
            if (!match.Success || text.Length > 240) return false;
            group = match.Groups["g"].Value;
            artifact = match.Groups["a"].Value;
            return true;
        }

        internal static bool IsEligible(JavaPackageSource source)
        {
            return source != null && !source.Blocked && source.Effective && source.Url != null &&
                source.Url.IsAbsoluteUri && source.Url.Scheme == Uri.UriSchemeHttps &&
                source.Url.UserInfo.Length == 0 && source.Url.Query.Length == 0 &&
                source.Url.Fragment.Length == 0;
        }

        internal async Task<JavaRepositoryCatalogResult> GetCatalogAsync(string group, string artifact,
            IEnumerable<JavaPackageSource> discovered, CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException(GetType().Name);
            if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(artifact) ||
                group.Length > 160 || artifact.Length > 160 ||
                !Identifier.IsMatch(group) || !Identifier.IsMatch(artifact))
                throw new ArgumentException("Coordinate Maven non valide.");
            token.ThrowIfCancellationRequested();

            var all = (discovered ?? Enumerable.Empty<JavaPackageSource>()).ToList();
            var eligible = all.Where(IsEligible).GroupBy(s => s.Url.AbsoluteUri.TrimEnd('/'),
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.HasServerConfiguration).First())
                .Take(MaxRepositories).ToList();
            var warnings = new List<string>();
            if (all.Count(IsEligible) > MaxRepositories)
                warnings.Add("Limite di " + MaxRepositories + " repository per richiesta; controllare le origini.");
            if (all.Any(s => s != null && !s.Effective))
                warnings.Add("Repository Gradle non attivi esclusi dalla ricerca.");
            if (all.Any(s => s != null && s.Blocked))
                warnings.Add("Repository non HTTPS/bloccati esclusi dalla ricerca.");
            if (eligible.Count == 0)
            {
                IList<string> none = warnings.Count == 0
                    ? (IList<string>)new[] { "Nessun repository HTTPS attivo." } : warnings;
                return new JavaRepositoryCatalogResult(new string[0],
                    new Dictionary<string, string>(), none, 0);
            }

            // Bound concurrent requests; one failing repository must never hide others.
            using (var throttle = new SemaphoreSlim(3))
            {
                var tasks = eligible.Select(async source =>
                {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                    try { return await LoadSourceAsync(source, group, artifact, token).ConfigureAwait(false); }
                    finally { throttle.Release(); }
                }).ToArray();
                var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
                var versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var response in responses)
                {
                    if (response.Warning != null) warnings.Add(response.Warning);
                    foreach (string version in response.Versions ?? new string[0])
                    {
                        HashSet<string> origins;
                        if (!versions.TryGetValue(version, out origins))
                        {
                            origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            versions.Add(version, origins);
                        }
                        origins.Add(response.Source.Name ?? response.Source.Id ?? "repository");
                    }
                }
                token.ThrowIfCancellationRequested();
                var ordered = versions.Keys.ToList();
                ordered.Sort((left, right) =>
                {
                    int c = JavaPackageUpdateSelector.CompareVersions(right, left);
                    return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(right, left);
                });
                var labels = versions.ToDictionary(p => p.Key,
                    p => string.Join(", ", p.Value.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase);
                return new JavaRepositoryCatalogResult(ordered, labels, warnings, eligible.Count);
            }
        }

        private async Task<SourceResponse> LoadSourceAsync(JavaPackageSource source,
            string group, string artifact, CancellationToken token)
        {
            var response = new SourceResponse { Source = source, Versions = new string[0] };
            string key = source.Url.AbsoluteUri.TrimEnd('/') + "/" + group + ":" + artifact;
            lock (cacheGate)
            {
                CachedVersions cached;
                if (cache.TryGetValue(key, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
                { response.Versions = cached.Versions; return response; }
            }
            // URL segments cannot contain '/', '..', query, fragment, or credentials.
            string relative = string.Join("/", group.Split('.').Select(Uri.EscapeDataString)) + "/" +
                Uri.EscapeDataString(artifact) + "/maven-metadata.xml";
            var uri = new Uri(source.Url.AbsoluteUri.TrimEnd('/') + "/" + relative);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(22));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                    // Only Maven user-owned settings may provide HTTP Basic credentials.
                    // Encrypted Maven credentials and Gradle credentials MUST NOT be guessed.
                    var credentials = JavaMavenSourceCredentials.GetPlaintext(source);
                    if (credentials != null)
                    {
                        string value = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                            credentials.UserName + ":" + credentials.Password));
                        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
                    }
                    using (var result = await client.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        if (result.StatusCode == HttpStatusCode.NotFound) return response;
                        if (result.StatusCode == HttpStatusCode.Unauthorized ||
                            result.StatusCode == HttpStatusCode.Forbidden)
                        {
                            response.Warning = "Accesso negato a " + SafeLabel(source) +
                                " (HTTP " + (int)result.StatusCode + "; credenziali native non disponibili o insufficienti).";
                            return response;
                        }
                        if (!result.IsSuccessStatusCode)
                        {
                            response.Warning = SafeLabel(source) + ": HTTP " + (int)result.StatusCode;
                            return response;
                        }
                        byte[] bytes;
                        using (var input = await result.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            var block = new byte[8192];
                            int n;
                            while ((n = await input.ReadAsync(block, 0, block.Length, timeout.Token)
                                .ConfigureAwait(false)) != 0)
                            {
                                if (buffer.Length + n > MaxMetadataBytes) throw new InvalidDataException("Metadati troppo grandi.");
                                buffer.Write(block, 0, n);
                            }
                            bytes = buffer.ToArray();
                        }
                        token.ThrowIfCancellationRequested();
                        using (var mem = new MemoryStream(bytes))
                        using (var reader = XmlReader.Create(mem, new XmlReaderSettings
                        {
                            DtdProcessing = DtdProcessing.Prohibit,
                            XmlResolver = null,
                            MaxCharactersInDocument = MaxMetadataBytes
                        }))
                        {
                            var doc = XDocument.Load(reader);
                            var root = doc.Root;
                            if (root == null || root.Name.LocalName != "metadata" ||
                                !string.Equals(Local(root, "groupId"), group, StringComparison.Ordinal) ||
                                !string.Equals(Local(root, "artifactId"), artifact, StringComparison.Ordinal))
                                throw new InvalidDataException("Identità dei metadati non corrispondente.");
                            var versioning = root.Elements().FirstOrDefault(x => x.Name.LocalName == "versioning");
                            var list = versioning == null ? null : versioning.Elements().FirstOrDefault(x => x.Name.LocalName == "versions");
                            response.Versions = list == null ? new string[0] : list.Elements()
                                .Where(x => x.Name.LocalName == "version").Select(x => x.Value.Trim())
                                .Where(x => x.Length > 0 && x.Length <= 160 &&
                                    !x.Any(ch => char.IsControl(ch) || ch == '/' || ch == '\\' || ch == ':' || ch == '?'))
                                .Where(x => x.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase)
                                    ? source.Snapshots : source.Releases)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20000).ToArray();
                        }
                    }
                }
                lock (cacheGate)
                {
                    if (cache.Count > 200) cache.Clear();
                    cache[key] = new CachedVersions { ExpiresUtc = DateTime.UtcNow.AddMinutes(3),
                        Versions = response.Versions };
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { response.Warning = SafeLabel(source) + ": timeout richiesta metadati."; }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException ||
                ex is InvalidDataException || ex is XmlException || ex is ArgumentException ||
                ex is InvalidOperationException || ex is System.Security.SecurityException)
            { response.Warning = SafeLabel(source) + ": metadati non disponibili/validi."; }
            return response;
        }

        private static string Local(XElement element, string name)
        {
            return (element.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "").Trim();
        }
        private static string SafeLabel(JavaPackageSource source)
        {
            // Avoid emitting secret URL fragments/queries, which are never accepted.
            return (source.Name ?? source.Id ?? "repository").Replace("\r", "").Replace("\n", "");
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ownsClient) client.Dispose();
            lock (cacheGate) cache.Clear();
        }
    }

    // Maven settings credentials are used only for repositories declared in a
    // trusted user-owned settings profile (or its mirror), never from a foreign POM.
    // Gradle credentials and encrypted Maven passwords must be resolved by the
    // native build tools; we do not export them from Gradle nor decrypt them here.
    internal static class JavaMavenSourceCredentials
    {
        internal static NetworkCredential GetPlaintext(JavaPackageSource source)
        {
            if (source == null || !source.HasServerConfiguration ||
                string.IsNullOrWhiteSpace(source.ServerId) ||
                (!((source.Origin ?? "").Contains("settings.xml") ||
                    (source.Origin ?? "").StartsWith("mirror di ", StringComparison.Ordinal))) ||
                source.Url == null || source.Url.Scheme != Uri.UriSchemeHttps)
                return null;
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".m2", "settings.xml");
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 4 * 1024 * 1024) return null;
                using (var stream = File.OpenRead(file))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 4 * 1024 * 1024
                }))
                {
                    var root = XDocument.Load(reader).Root;
                    if (root == null || root.Name.LocalName != "settings") return null;
                    var servers = root.Elements().FirstOrDefault(x => x.Name.LocalName == "servers");
                    if (servers == null) return null;
                    foreach (var server in servers.Elements().Where(x => x.Name.LocalName == "server"))
                    {
                        string id = Value(server, "id");
                        if (!string.Equals(id, source.ServerId, StringComparison.Ordinal)) continue;
                        string username = Value(server, "username");
                        string password = Value(server, "password");
                        if (username.Length == 0 || password.Length == 0 ||
                            password.StartsWith("{", StringComparison.Ordinal) ||
                            password.Contains("${") || username.Contains("${")) return null;
                        return new NetworkCredential(username, password);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is XmlException || ex is ArgumentException || ex is System.Security.SecurityException)
            { /* No secret-bearing diagnostics. */ }
            return null;
        }
        private static string Value(XElement root, string name)
        { return (root.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "").Trim(); }
    }
}
