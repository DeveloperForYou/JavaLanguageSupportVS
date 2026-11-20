using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly Regex Coordinates = new Regex(
            @"\A(?<g>[A-Za-z0-9_][A-Za-z0-9_.-]*):(?<a>[A-Za-z0-9_][A-Za-z0-9_.-]*)\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex MavenIdentifier = new Regex(
            @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly HttpClient client;
        private readonly bool ownsClient;
        private bool disposed;

        internal JavaMavenCentralSearchService()
            : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), true)
        {
        }

        // Inject a controlled HttpClient in offline tests and in future UI tests.
        // The caller remains responsible for disposing an injected HttpClient.
        internal JavaMavenCentralSearchService(HttpClient client)
            : this(client, false)
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

        /// <summary>All versions of one exact coordinate, newest publication first.</summary>
        internal Task<JavaCentralPage<JavaCentralVersion>> GetVersionsAsync(
            string groupId, string artifactId, int page = 1, int pageSize = 30,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string q = ExactQuery(groupId, artifactId);
            return QueryAsync(q, "gav", page, pageSize, ParseVersion, cancellationToken);
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
            // JDT LS/Visual Studio are never blocked on this HTTP request.
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                linked.CancelAfter(RequestTimeout);
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

    internal sealed class JavaCentralArtifact
    {
        internal JavaCentralArtifact(string group, string artifact, string latest,
            string packaging, int versionCount, DateTimeOffset? published,
            IReadOnlyList<string> extensions)
        {
            GroupId = group; ArtifactId = artifact; LatestVersion = latest;
            Packaging = packaging; VersionCount = versionCount;
            PublishedUtc = published; Extensions = extensions;
        }
        internal string GroupId { get; private set; }
        internal string ArtifactId { get; private set; }
        internal string LatestVersion { get; private set; }
        internal string Packaging { get; private set; }
        internal int VersionCount { get; private set; }
        internal DateTimeOffset? PublishedUtc { get; private set; }
        internal IReadOnlyList<string> Extensions { get; private set; }
        internal string Coordinate { get { return GroupId + ":" + ArtifactId; } }
    }

    internal sealed class JavaCentralVersion
    {
        internal JavaCentralVersion(string group, string artifact, string version,
            string packaging, DateTimeOffset? published, IReadOnlyList<string> extensions)
        {
            GroupId = group; ArtifactId = artifact; Version = version;
            Packaging = packaging; PublishedUtc = published; Extensions = extensions;
        }
        internal string GroupId { get; private set; }
        internal string ArtifactId { get; private set; }
        internal string Version { get; private set; }
        internal string Packaging { get; private set; }
        internal DateTimeOffset? PublishedUtc { get; private set; }
        internal IReadOnlyList<string> Extensions { get; private set; }
        internal string Coordinate { get { return GroupId + ":" + ArtifactId + ":" + Version; } }
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
