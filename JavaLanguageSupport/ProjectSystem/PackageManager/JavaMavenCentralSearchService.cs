using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    /// <summary>
    /// F4.F.1: read-only Maven Central Solr search service. Does not change any
    /// .javaproj, pom.xml, Gradle build, library or JDT LS workspace.
    /// Intentionally independent of Visual Studio UI and CPS for reuse by F4.F.2.
    /// </summary>
    internal sealed class JavaMavenCentralSearchService : IDisposable
    {
        private const string Endpoint = "https://search.maven.org/solrsearch/select";
        private const int MaxResponseBytes = 2 * 1024 * 1024;
        private const int MaximumPageSize = 50;
        private static readonly Regex Coordinates = new Regex(
            @"\A(?<g>[A-Za-z0-9_][A-Za-z0-9_.-]*):(?<a>[A-Za-z0-9_][A-Za-z0-9_.-]*)\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex MavenIdentifier = new Regex(
            @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly HttpClient client;
        private readonly bool ownsClient;

        private readonly object cacheSync = new object();
        private readonly Dictionary<string, CachedResponse> cachedResponses =
            new Dictionary<string, CachedResponse>(StringComparer.Ordinal);
        private const int MaxCacheEntries = 128;
        private bool disposed;

        private sealed class CachedResponse
        {
            internal object Page;
            internal DateTime ExpiresUtc;
        }

        internal JavaMavenCentralSearchService()
            : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), true)
        {
        }


        private JavaMavenCentralSearchService(HttpClient client, bool ownsClient)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.ownsClient = ownsClient;
        }

        /// <summary>
        /// Searches Maven Central by free text or exact "groupId:artifactId".
        /// One based page numbers are used by the public UI-facing API.
        /// </summary>
        internal Task<JavaCentralPage<JavaCentralArtifact>> SearchAsync(
            string search, int page = 1, int pageSize = 20,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(search) || search.Length > 160)
                throw new ArgumentException("Inserire 1-160 caratteri di ricerca.", nameof(search));
            string trimmed = search.Trim();
            if (trimmed.Any(char.IsControl))
                throw new ArgumentException("La ricerca contiene caratteri non validi.", nameof(search));
            string q;
            Match coordinates = Coordinates.Match(trimmed);
            if (coordinates.Success)
            {
                q = ExactQuery(coordinates.Groups["g"].Value,
                    coordinates.Groups["a"].Value);
            }
            else
            {
                // Escaped terms, not raw Solr query language from an untrusted UI.
                string[] words = Regex.Split(trimmed, @"\s+");
                if (words.Length > 12)
                    throw new ArgumentException("Usare al massimo 12 termini.", nameof(search));
                q = string.Join(" AND ", words.Select(x => "\"" + EscapeSolrTerm(x) + "\""));
            }
            return QueryAsync(q, null, page, pageSize, ParseArtifact, cancellationToken);
        }

        // F4F21034-SUGGESTION-CANDIDATES: retrieve up to 50 for local quality ranking.
        // F4F2103-COORDINATE-DISCOVERY: use the search index for suggestions only.
        // The existing metadata catalog remains authoritative for availability/versions.
        internal Task<JavaCentralPage<JavaCentralArtifact>> SuggestAsync(
            string search, CancellationToken cancellationToken)
        {
            string term = (search ?? "").Trim();
            if (term.Length < 3 || term.Length > 160 || term.Any(char.IsControl))
                throw new ArgumentException("Inserire 3-160 caratteri di ricerca.", nameof(search));
            int colon = term.IndexOf(':');
            if (colon >= 0)
            {
                string group = term.Substring(0, colon);
                string artifact = term.Substring(colon + 1);
                if (group.Length < 3 || !MavenIdentifier.IsMatch(group) ||
                    (artifact.Length != 0 && !MavenIdentifier.IsMatch(artifact)))
                    throw new ArgumentException("Prefisso Maven non valido.", nameof(search));
                // Wildcards are generated here, never accepted from the user.
                string q = "g:" + EscapeSolrTerm(group) + "*";
                if (artifact.Length != 0)
                    q += " AND a:" + EscapeSolrTerm(artifact) + "*";
                return QueryAsync(q, null, 1, 50, ParseArtifact, cancellationToken);
            }
            if (MavenIdentifier.IsMatch(term))
            {
                string escaped = EscapeSolrTerm(term);
                string q = "(a:" + escaped + "* OR g:" + escaped + "* OR \"" + escaped + "\")";
                return QueryAsync(q, null, 1, 50, ParseArtifact, cancellationToken);
            }
            // The existing escaped full-text path handles multiple words.
            return SearchAsync(term, 1, 50, cancellationToken);
        }


        // F4F27-REPOSITORY-METADATA: the Solr gav index is for discovery, not
        // the authoritative complete version catalog. Read Maven's repository
        // metadata once, then paginate locally; retain Solr only as a fallback.
        internal async Task<JavaCentralPage<JavaCentralVersion>> GetVersionsAsync(
            string groupId, string artifactId, int page = 1, int pageSize = 30,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string exactQuery = ExactQuery(groupId, artifactId); // validate input before HTTP
            ThrowIfDisposed();
            if (pageSize < 1 || pageSize > MaximumPageSize)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Da 1 a 50 elementi per pagina.");
            if (page < 1 || (long)(page - 1) * pageSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(page), "Pagina non valida.");
            cancellationToken.ThrowIfCancellationRequested();

            string key = groupId + ":" + artifactId;
            IReadOnlyList<string> allVersions;
            if (!TryGetVersionCatalog(key, out allVersions))
            {
                try
                {
                    allVersions = await DownloadVersionCatalogAsync(groupId, artifactId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (JavaMavenCentralSearchException ex) when (
                    ex.StatusCode != 429 && ex.StatusCode != 403)
                {
                    System.Diagnostics.Trace.WriteLine("[F4.F.2.7] Metadata " + key +
                        " non disponibile: " + ex.Message);
                    allVersions = null; // old Solr path is a best-effort fallback
                }
                catch (Exception ex) when (ex is XmlException ||
                    ex is InvalidDataException || ex is HttpRequestException ||
                    ex is IOException || ex is DecoderFallbackException)
                {
                    System.Diagnostics.Trace.WriteLine("[F4.F.2.7] Metadata " + key +
                        " non leggibile: " + ex.Message);
                    allVersions = null; // metadata malformed or unreachable
                }
                if (allVersions == null || allVersions.Count == 0)
                {
                    System.Diagnostics.Trace.WriteLine("[F4.F.2.7] " + key +
                        ": uso fallback Solr gav.");
                    return await QueryAsync(exactQuery, "gav", page, pageSize,
                        ParseVersion, cancellationToken).ConfigureAwait(false);
                }
                StoreVersionCatalog(key, allVersions);
            }
            cancellationToken.ThrowIfCancellationRequested();
            int offset = checked((page - 1) * pageSize);
            var items = allVersions.Skip(offset).Take(pageSize)
                .Select(version => new JavaCentralVersion(groupId, artifactId,
                    version, string.Empty, null, new string[0])).ToList();
            return new JavaCentralPage<JavaCentralVersion>(items,
                allVersions.Count, page, pageSize);
        }

        // Repository coordinates are path segments, not Solr expressions.
        private const string VersionMetadataRoot = "https://repo.maven.apache.org/maven2/";
        private readonly Dictionary<string, VersionCatalogEntry> versionCatalogs =
            new Dictionary<string, VersionCatalogEntry>(StringComparer.Ordinal);
        private sealed class VersionCatalogEntry
        {
            internal IReadOnlyList<string> Versions;
            internal DateTime ExpiresUtc;
        }

        private bool TryGetVersionCatalog(string key, out IReadOnlyList<string> versions)
        {
            lock (cacheSync)
            {
                VersionCatalogEntry value;
                if (versionCatalogs.TryGetValue(key, out value))
                {
                    if (value.ExpiresUtc > DateTime.UtcNow)
                    {
                        versions = value.Versions;
                        return true;
                    }
                    versionCatalogs.Remove(key);
                }
            }
            versions = null;
            return false;
        }

        private void StoreVersionCatalog(string key, IReadOnlyList<string> versions)
        {
            lock (cacheSync)
            {
                if (disposed) return;
                if (versionCatalogs.Count >= 64 && !versionCatalogs.ContainsKey(key))
                {
                    string oldest = versionCatalogs.OrderBy(x => x.Value.ExpiresUtc)
                        .Select(x => x.Key).FirstOrDefault();
                    if (oldest != null) versionCatalogs.Remove(oldest);
                }
                versionCatalogs[key] = new VersionCatalogEntry
                {
                    Versions = versions,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(30)
                };
            }
        }

        private async Task<IReadOnlyList<string>> DownloadVersionCatalogAsync(
            string groupId, string artifactId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string url = VersionMetadataRoot + groupId.Replace('.', '/') + "/" +
                artifactId + "/maven-metadata.xml";
            // No additional CancelAfter: preserve the user's HTTP timeout policy.
            // The caller can still cancel on selection change or close.
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/xml"));
                request.Headers.UserAgent.ParseAdd("JavaLanguageSupportVS/1.0");
                using (HttpResponseMessage response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 404) return null;
                        throw new JavaMavenCentralSearchException(
                            "Repository Maven Central: HTTP " + (int)response.StatusCode + ".",
                            (int)response.StatusCode);
                    }
                    string xml = await ReadBoundedAsync(response.Content, token)
                        .ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null
                    };
                    using (var reader = XmlReader.Create(new StringReader(xml), settings))
                    {
                        var document = XDocument.Load(reader);
                        XElement metadata = document.Root;
                        if (metadata == null || metadata.Name.LocalName != "metadata")
                            throw new InvalidDataException("maven-metadata.xml non valido.");
                        string declaredGroup = ChildValue(metadata, "groupId");
                        string declaredArtifact = ChildValue(metadata, "artifactId");
                        if ((!string.IsNullOrEmpty(declaredGroup) && declaredGroup != groupId) ||
                            (!string.IsNullOrEmpty(declaredArtifact) && declaredArtifact != artifactId))
                            throw new InvalidDataException("Coordinate metadata non corrispondenti.");
                        var versioning = metadata.Elements()
                            .FirstOrDefault(e => e.Name.LocalName == "versioning");
                        var list = versioning == null ? null : versioning.Elements()
                            .FirstOrDefault(e => e.Name.LocalName == "versions");
                        if (list == null) return null;
                        // Metadata lists versions in repository order; show its
                        // newest entries first, without inventing semver ordering.
                        var versions = list.Elements()
                            .Where(e => e.Name.LocalName == "version")
                            .Select(e => e.Value.Trim())
                            .Where(v => v.Length > 0 && v.Length <= 512 &&
                                !v.Any(char.IsControl))
                            .Distinct(StringComparer.Ordinal)
                            .Reverse().ToList();
                        return versions;
                    }
                }
            }
        }

        private static string ChildValue(XElement parent, string localName)
        {
            var child = parent.Elements()
                .FirstOrDefault(e => e.Name.LocalName == localName);
            return child == null ? null : child.Value.Trim();
        }

        /// <summary>Returns null if an exact coordinate does not exist.</summary>
        internal async Task<JavaCentralArtifact> GetArtifactAsync(
            string groupId, string artifactId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string q = ExactQuery(groupId, artifactId);
            var response = await QueryAsync(q, null, 1, 1, ParseArtifact,
                cancellationToken).ConfigureAwait(false);
            return response.Items.FirstOrDefault(x =>
                string.Equals(x.GroupId, groupId, StringComparison.Ordinal) &&
                string.Equals(x.ArtifactId, artifactId, StringComparison.Ordinal));
        }

        // F4F25-RESILIENT-REQUEST: cache successful pages, then retry at most once
        // for a timeout network failure or HTTP 408/502/503/504. Never
        // retry HTTP 429 or 403: Maven Central asks clients to reduce traffic.
        private async Task<JavaCentralPage<T>> QueryAsync<T>(
            string q, string core, int page, int pageSize,
            Func<JObject, T> mapper, CancellationToken cancellationToken)
            where T : class
        {
            ThrowIfDisposed();
            if (page < 1 || page > 1000)
                throw new ArgumentOutOfRangeException(nameof(page), "Pagina da 1 a 1000.");
            if (pageSize < 1 || pageSize > MaximumPageSize)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Da 1 a 50 elementi per pagina.");
            cancellationToken.ThrowIfCancellationRequested();
            int start = checked((page - 1) * pageSize);
            string query = Endpoint + "?q=" + Uri.EscapeDataString(q) +
                "&rows=" + pageSize.ToString(CultureInfo.InvariantCulture) +
                "&start=" + start.ToString(CultureInfo.InvariantCulture) +
                "&wt=json" + (core == null ? "" : "&core=" + core);
            JavaCentralPage<T> cached;
            if (TryGetCached(query, out cached)) return cached;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await QueryOnceAsync(query, page, pageSize,
                        mapper, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    StoreCached(query, result,
                        core == "gav" ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(10));
                    return result;
                }
                catch (JavaMavenCentralSearchException ex) when (
                    attempt == 0 && IsRetryable(ex) && !cancellationToken.IsCancellationRequested)
                {
                    // A short bounded delay, not a retry storm.
                    await Task.Delay(650, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("Ricerca Maven Central: tentativi esauriti.");
        }

        private static bool IsRetryable(JavaMavenCentralSearchException ex)
        {
            if (ex.StatusCode == 408 || ex.StatusCode == 502 ||
                ex.StatusCode == 503 || ex.StatusCode == 504) return true;
            return ex.StatusCode == 0 &&
                (ex.Message.StartsWith("Timeout durante", StringComparison.Ordinal) ||
                 ex.Message.StartsWith("Connessione a Maven Central", StringComparison.Ordinal));
        }

        private bool TryGetCached<T>(string key, out JavaCentralPage<T> page) where T : class
        {
            lock (cacheSync)
            {
                CachedResponse entry;
                if (cachedResponses.TryGetValue(key, out entry))
                {
                    if (entry.ExpiresUtc > DateTime.UtcNow && entry.Page is JavaCentralPage<T>)
                    {
                        page = (JavaCentralPage<T>)entry.Page;
                        return true;
                    }
                    cachedResponses.Remove(key);
                }
            }
            page = null;
            return false;
        }

        private void StoreCached<T>(string key, JavaCentralPage<T> result, TimeSpan ttl)
            where T : class
        {
            lock (cacheSync)
            {
                if (disposed) return;
                if (cachedResponses.Count >= MaxCacheEntries &&
                    !cachedResponses.ContainsKey(key))
                {
                    // Bounded per-window cache: remove the oldest expiry first.
                    string oldest = cachedResponses.OrderBy(p => p.Value.ExpiresUtc)
                        .Select(p => p.Key).FirstOrDefault();
                    if (oldest != null) cachedResponses.Remove(oldest);
                }
                cachedResponses[key] = new CachedResponse
                {
                    Page = result, ExpiresUtc = DateTime.UtcNow.Add(ttl)
                };
            }
        }

        private async Task<JavaCentralPage<T>> QueryOnceAsync<T>(
            string query, int page, int pageSize,
            Func<JObject, T> mapper, CancellationToken cancellationToken)
            where T : class
        {
            // JDT LS/Visual Studio are never blocked on this HTTP request.
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, query))
                    {
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        request.Headers.UserAgent.ParseAdd("JavaLanguageSupportVS/1.0");
                        using (HttpResponseMessage response = await client.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, linked.Token)
                            .ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                throw new JavaMavenCentralSearchException(
                                    "Maven Central ha risposto con HTTP " +
                                    (int)response.StatusCode + ".",
                                    (int)response.StatusCode);
                            }
                            string json = await ReadBoundedAsync(response.Content,
                                linked.Token).ConfigureAwait(false);
                            JObject root;
                            try { root = JObject.Parse(json); }
                            catch (JsonException ex)
                            {
                                throw new JavaMavenCentralSearchException(
                                    "Risposta JSON non valida da Maven Central.", 0, ex);
                            }
                            JObject envelope = root["response"] as JObject;
                            JArray docs = envelope == null ? null : envelope["docs"] as JArray;
                            if (envelope == null || docs == null ||
                                envelope["numFound"] == null)
                                throw new JavaMavenCentralSearchException(
                                    "Risposta incompleta da Maven Central.");
                            long found;
                            try { found = envelope.Value<long>("numFound"); }
                            catch (Exception ex) when (ex is FormatException ||
                                ex is InvalidCastException || ex is OverflowException)
                            {
                                throw new JavaMavenCentralSearchException(
                                    "Numero di risultati non valido.", 0, ex);
                            }
                            if (found < 0 || docs.Count > pageSize)
                                throw new JavaMavenCentralSearchException(
                                    "Paginazione non valida da Maven Central.");
                            var items = new List<T>(docs.Count);
                            foreach (JObject doc in docs.OfType<JObject>())
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                T item = mapper(doc);
                                if (item != null) items.Add(item);
                            }
                            return new JavaCentralPage<T>(items, found, page, pageSize);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new JavaMavenCentralSearchException(
                        "Timeout durante la ricerca su Maven Central.");
                }
                catch (HttpRequestException ex)
                {
                    throw new JavaMavenCentralSearchException(
                        "Connessione a Maven Central non riuscita.", 0, ex);
                }
                catch (IOException ex)
                {
                    throw new JavaMavenCentralSearchException(
                        "Lettura della risposta Maven Central non riuscita.", 0, ex);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new JavaMavenCentralSearchException(
                        "La risposta Maven Central non e' UTF-8 valido.", 0, ex);
                }
            }
        }

        private static async Task<string> ReadBoundedAsync(
            HttpContent content, CancellationToken token)
        {
            if (content == null)
                throw new JavaMavenCentralSearchException("Risposta HTTP senza contenuto.");
            if (content.Headers.ContentLength.HasValue &&
                content.Headers.ContentLength.Value > MaxResponseBytes)
                throw new JavaMavenCentralSearchException("Risposta Maven Central troppo grande.");

            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, token)
                    .ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxResponseBytes)
                        throw new JavaMavenCentralSearchException("Risposta Maven Central troppo grande.");
                    buffer.Write(chunk, 0, read);
                }
                token.ThrowIfCancellationRequested();
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }

        private static JavaCentralArtifact ParseArtifact(JObject doc)
        {
            string group = (string)doc["g"];
            string artifact = (string)doc["a"];
            if (!IsValidCoordinate(group) || !IsValidCoordinate(artifact)) return null;
            return new JavaCentralArtifact(group, artifact,
                (string)doc["latestVersion"] ?? string.Empty,
                (string)doc["p"] ?? string.Empty,
                (int?)doc["versionCount"] ?? 0,
                FromUnixMilliseconds(doc["timestamp"]),
                GetExtensions(doc));
        }

        private static JavaCentralVersion ParseVersion(JObject doc)
        {
            string group = (string)doc["g"];
            string artifact = (string)doc["a"];
            string version = (string)doc["v"];
            if (!IsValidCoordinate(group) || !IsValidCoordinate(artifact) ||
                string.IsNullOrWhiteSpace(version)) return null;
            return new JavaCentralVersion(group, artifact, version,
                (string)doc["p"] ?? string.Empty,
                FromUnixMilliseconds(doc["timestamp"]), GetExtensions(doc));
        }

        private static IReadOnlyList<string> GetExtensions(JObject doc)
        {
            var extensions = doc["ec"] as JArray;
            return new ReadOnlyCollection<string>(extensions == null
                ? new List<string>() : extensions.Values<string>()
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList());
        }

        private static DateTimeOffset? FromUnixMilliseconds(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            long timestamp;
            if (!long.TryParse(value.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out timestamp)) return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static string ExactQuery(string groupId, string artifactId)
        {
            if (!IsValidCoordinate(groupId) || !IsValidCoordinate(artifactId))
                throw new ArgumentException("Coordinate Maven non valide (groupId:artifactId).");
            return "g:\"" + groupId + "\" AND a:\"" + artifactId + "\"";
        }

        private static bool IsValidCoordinate(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= 160 &&
                MavenIdentifier.IsMatch(value) &&
                !value.StartsWith(".", StringComparison.Ordinal) &&
                !value.EndsWith(".", StringComparison.Ordinal) &&
                !value.Contains("..");
        }

        private static string EscapeSolrTerm(string value)
        {
            // Solr reserved operators: prevent a search box from turning into
            // an unrestricted field/expression query. '%' is URL-escaped below.
            var result = new StringBuilder(value.Length * 2);
            foreach (char c in value)
            {
                if ("+-!(){}[]^\"~*?:\\/|&".IndexOf(c) >= 0)
                    result.Append('\\');
                result.Append(c);
            }
            return result.ToString();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(JavaMavenCentralSearchService));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (cacheSync) { cachedResponses.Clear(); versionCatalogs.Clear(); }
            if (ownsClient) client.Dispose();
        }
    }

    internal sealed class JavaCentralPage<T>
    {
        internal JavaCentralPage(IList<T> items, long total, int page, int pageSize)
        {
            Items = new ReadOnlyCollection<T>(items);
            TotalCount = total;
            Page = page;
            PageSize = pageSize;
        }
        internal IReadOnlyList<T> Items { get; private set; }
        internal long TotalCount { get; private set; }
        internal int Page { get; private set; }
        internal int PageSize { get; private set; }
        internal bool HasNextPage { get { return (long)Page * PageSize < TotalCount; } }
    }

    public sealed class JavaCentralArtifact
    {
        internal JavaCentralArtifact(string group, string artifact, string latest,
            string packaging, int versionCount, DateTimeOffset? published,
            IReadOnlyList<string> extensions)
        {
            GroupId = group; ArtifactId = artifact; LatestVersion = latest;
            Packaging = packaging; VersionCount = versionCount;
            PublishedUtc = published; Extensions = extensions;
        }
        public string GroupId { get; private set; }
        public string ArtifactId { get; private set; }
        public string LatestVersion { get; private set; }
        public string Packaging { get; private set; }
        public int VersionCount { get; private set; }
        public DateTimeOffset? PublishedUtc { get; private set; }
        public IReadOnlyList<string> Extensions { get; private set; }
        public string Coordinate { get { return GroupId + ":" + ArtifactId; } }
    }

    public sealed class JavaCentralVersion
    {
        internal JavaCentralVersion(string group, string artifact, string version,
            string packaging, DateTimeOffset? published, IReadOnlyList<string> extensions)
        {
            GroupId = group; ArtifactId = artifact; Version = version;
            Packaging = packaging; PublishedUtc = published; Extensions = extensions;
        }
        public string GroupId { get; private set; }
        public string ArtifactId { get; private set; }
        public string Version { get; private set; }
        public string Packaging { get; private set; }
        public DateTimeOffset? PublishedUtc { get; private set; }
        public IReadOnlyList<string> Extensions { get; private set; }
        public string Coordinate { get { return GroupId + ":" + ArtifactId + ":" + Version; } }
    }

    internal sealed class JavaMavenCentralSearchException : Exception
    {
        internal JavaMavenCentralSearchException(string message, int statusCode = 0,
            Exception inner = null) : base(message, inner)
        {
            StatusCode = statusCode;
        }
        internal int StatusCode { get; private set; }
        internal bool IsTransient { get { return StatusCode == 429 || StatusCode >= 500; } }
    }
}
