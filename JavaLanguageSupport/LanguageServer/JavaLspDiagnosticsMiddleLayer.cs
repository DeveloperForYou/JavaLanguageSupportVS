using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>
    /// Telemetria in memoria, senza acquisire codice sorgente o documentazione.
    /// Rileva richieste/risposte LSP gestite dall'editor Visual Studio.
    /// Per debug profondo usare il trace LSP di Visual Studio Open Folder.
    /// </summary>
    internal sealed class JavaLspDiagnosticsMiddleLayer : ILanguageClientMiddleLayer
    {
        internal static readonly JavaLspDiagnosticsMiddleLayer Instance =
            new JavaLspDiagnosticsMiddleLayer();

        private JavaLspDiagnosticsMiddleLayer() { }

        public bool CanHandle(string methodName)
        {
            return methodName == "textDocument/didOpen"
                || methodName == "textDocument/didChange"
                || methodName == "textDocument/didClose"
                || methodName == "$/progress"
                || methodName == "language/status"
                || methodName == "initialize"
                || methodName == "workspace/executeCommand"
                || methodName == "textDocument/hover"
                || methodName == "textDocument/completion"
                || methodName == "completionItem/resolve"
                || methodName == "textDocument/codeAction"
                || methodName == "textDocument/signatureHelp"
                || methodName == "textDocument/formatting"
                || methodName == "textDocument/rangeFormatting"
                || methodName == "textDocument/rename"
                || methodName == "textDocument/definition"
                || methodName == "textDocument/references"
                || methodName == "textDocument/documentSymbol"
                || methodName == "textDocument/semanticTokens/full"
                || methodName == "textDocument/codeLens"
                || methodName == "textDocument/inlayHint"
                || methodName == "textDocument/publishDiagnostics";
        }

        // Only call resolve when the server explicitly advertises that feature.
        // VS itself owns native completion, snapshot cancellation and item selection.
        private static int completionResolveSupported;

        private static async Task<JObject> ResolveCompletionItemAsync(JObject item,
            CancellationToken cancellationToken)
        {
            try
            {
                return await JavaLspRpc.RequestAsync("completionItem/resolve",
                    item.DeepClone(), cancellationToken).ConfigureAwait(false) as JObject;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                JavaLanguageStatus.LspFailure("completionItem/resolve", ex.GetType().Name);
                return null;
            }
        }

        private static async Task<int> TryPrefetchCompletionDetailsAsync(JToken response)
        {
            if (!JavaLspRpc.Connected || response == null) return 0;
            var array = response as JArray ?? (response as JObject)?["items"] as JArray;
            if (array == null) return 0;

            // Preserve the original behavior (at most two top candidates),
            // but bound the wall-clock delay even if a JSON-RPC cancellation
            // is not acknowledged immediately by the language server.
            var candidates = array.OfType<JObject>()
                .Where(item => item["data"] != null &&
                    (item["documentation"] == null || item["additionalTextEdits"] == null))
                .Take(2).ToArray();
            if (candidates.Length == 0) return 0;

            using (var timeout = new CancellationTokenSource())
            {
                var tasks = candidates.Select(item =>
                    ResolveCompletionItemAsync(item, timeout.Token)).ToArray();
                var all = Task.WhenAll(tasks);
                // Never hold up native IntelliSense for an unresponsive resolve.
                if (await Task.WhenAny(all, Task.Delay(120)).ConfigureAwait(false) != all)
                    timeout.Cancel();

                int resolvedCount = 0;
                // IMPORTANT: the asynchronous tasks touch only deep-cloned
                // requests. ONLY this method may mutate the completion result,
                // and it does so BEFORE handing the result back to Visual Studio.
                // A late resolve is safely discarded, never applied after return.
                for (int i = 0; i < tasks.Length; i++)
                {
                    if (tasks[i].Status != TaskStatus.RanToCompletion) continue;
                    JObject details = tasks[i].Result;
                    if (details == null) continue;
                    foreach (string key in new[] {
                        "documentation", "additionalTextEdits", "detail", "textEdit" })
                    {
                        if (details[key] != null && candidates[i][key] == null)
                            candidates[i][key] = details[key].DeepClone();
                    }
                    resolvedCount++;
                }
                return resolvedCount;
            }
        }

        public async Task HandleNotificationAsync(string methodName, JToken methodParam,
            Func<JToken, Task> sendNotification)
        {
            // Pack A3: invalidate the cached quick-fix diagnostics when
            // the LSP client advances a file version. Never touch the real
            // didOpen/didChange/didClose notification sent to JDT LS.
            if (methodName == "textDocument/didOpen" ||
                methodName == "textDocument/didChange" ||
                methodName == "textDocument/didClose")
            {
                try { JavaDiagnosticsMonitor.ObserveDocumentNotification(methodName, methodParam); }
                catch (Exception) { /* Monitoring must not break the LSP stream. */ }
            }
            // Observational only. Do not suppress or mutate server messages.
            if (methodName == "$/progress" || methodName == "language/status")
            {
                try { JavaWorkspaceActivity.Observe(methodName, methodParam); }
                catch (Exception) { /* A status signal must never break LSP. */ }
            }
            // Diagnostic observation is read-only: VS must receive the
            // original publishDiagnostics notification unchanged.
            if (methodName == "textDocument/publishDiagnostics")
            {
                try { JavaDiagnosticsMonitor.Observe(methodParam); }
                catch (Exception) { /* Telemetry must not break diagnostics. */ }
            }
            await sendNotification(methodParam);
        }

        public async Task<JToken> HandleRequestAsync(string methodName, JToken methodParam,
            Func<JToken, Task<JToken>> sendRequest)
        {
            // Manteniamo esattamente la semantica delle richieste originali.
            // Anche quando il server risponde con un errore, non lo assorbiamo.
            Stopwatch timing = methodName == "textDocument/completion" ?
                Stopwatch.StartNew() : null;
            if (methodName == "initialize")
                Volatile.Write(ref completionResolveSupported, 0);
            JavaLanguageStatus.LspRequest(methodName);
            if (methodName == "workspace/executeCommand")
                JavaLanguageStatus.LspExecuteCommand(methodParam);
            try
            {
                // The native VS Quick Actions handler may send an empty LSP
                // CodeActionContext.diagnostics. JDT LS requires the concrete
                // unresolved-type diagnostic to propose missing imports.
                JToken request = methodParam;
                if (methodName == "textDocument/codeAction")
                {
                    try
                    {
                        JObject original = methodParam as JObject;
                        JObject context = original == null ? null : original["context"] as JObject;
                        JArray provided = context == null ? null : context["diagnostics"] as JArray;
                        JToken at = original == null ? null : original.SelectToken("range.start");
                        string uri = original == null ? null :
                            (string)original.SelectToken("textDocument.uri");
                        if (context != null && (provided == null || provided.Count == 0) && at != null)
                        {
                            var diagnostics = JavaDiagnosticsMonitor.ForPosition(uri,
                                (int?)at["line"] ?? -1, (int?)at["character"] ?? -1);
                            if (diagnostics.Count != 0)
                            {
                                request = original.DeepClone();
                                ((JObject)((JObject)request)["context"])["diagnostics"] = diagnostics;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Never prevent native VS quick fixes if diagnostic
                        // enrichment cannot be performed.
                        request = methodParam;
                    }
                }
                JToken response = await sendRequest(request);
                // Le capabilities determinano se Visual Studio PUO' richiedere hover/resolve.
                // Non modifichiamo nessun messaggio LSP.
                if (methodName == "initialize")
                {
                    JavaLanguageStatus.LspInitialize(methodParam, response);
                    JavaSemanticLegend.ReadInitialize(response);
                    JToken provider = response == null ? null :
                        response.SelectToken("capabilities.completionProvider.resolveProvider");
                    Volatile.Write(ref completionResolveSupported,
                        provider != null && provider.Type == JTokenType.Boolean &&
                        provider.Value<bool>() ? 1 : 0);
                    return response;
                }

                // Visual Studio talvolta non invia completionItem/resolve dopo la
                // selezione. Risolviamo in anticipo pochi elementi, con budget breve,
                // per ottenere documentazione e additionalTextEdits (import).
                // Se il server e' lento, restituiamo i risultati originali.
                int prefetched = 0;
                if (methodName == "textDocument/completion" &&
                    Volatile.Read(ref completionResolveSupported) != 0)
                    prefetched = await TryPrefetchCompletionDetailsAsync(response)
                        .ConfigureAwait(false);

                // Non salviamo il contenuto dei sorgenti. Per code action
                // memorizziamo soltanto pochi titoli e flag diagnostici in RAM.
                if (methodName == "textDocument/codeAction" && response is JArray)
                {
                    JavaLanguageStatus.LspCodeActions((JArray)response);
                }
                int items = 0;
                bool hasDocumentation = false;
                bool hasAdditionalTextEdits = false;
                if (methodName == "textDocument/completion" && response != null)
                {
                    JArray arr = response as JArray;
                    if (arr == null && response is JObject)
                        arr = ((JObject)response)["items"] as JArray;
                    if (arr != null)
                    {
                        items = arr.Count;
                        hasDocumentation = arr.Any(x => x["documentation"] != null);
                        hasAdditionalTextEdits = arr.Any(x => x["additionalTextEdits"] != null);
                    }
                }
                else if (methodName == "textDocument/codeAction" && response is JArray)
                {
                    items = ((JArray)response).Count;
                }
                else if (methodName == "textDocument/hover" && response != null)
                {
                    hasDocumentation = response is JObject &&
                        ((JObject)response)["contents"] != null;
                }
                else if (methodName == "completionItem/resolve" && response != null)
                {
                    if (response is JObject)
                    {
                        hasDocumentation = ((JObject)response)["documentation"] != null;
                        hasAdditionalTextEdits = ((JObject)response)["additionalTextEdits"] != null;
                    }
                }
                JavaLanguageStatus.LspResponse(methodName, items,
                    hasDocumentation, hasAdditionalTextEdits);
                if (timing != null)
                    JavaLanguageStatus.LspCompletionMetrics(
                        timing.ElapsedMilliseconds, response, prefetched);
                return response;
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.LspFailure(methodName, ex.GetType().Name);
                throw;
            }
        }
    }
}
