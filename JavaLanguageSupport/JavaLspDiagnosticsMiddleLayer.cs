using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport
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
            return methodName == "initialize"
                || methodName == "workspace/executeCommand"
                || methodName == "textDocument/hover"
                || methodName == "textDocument/completion"
                || methodName == "completionItem/resolve"
                || methodName == "textDocument/codeAction";
        }

        private static async Task TryPrefetchCompletionDetailsAsync(JToken response)
        {
            if (!JavaLspRpc.Connected || response == null) return;
            var array = response as JArray ?? (response as JObject)?["items"] as JArray;
            if (array == null) return;
            var candidates = array.OfType<JObject>()
                .Where(item => item["data"] != null &&
                    (item["documentation"] == null || item["additionalTextEdits"] == null))
                .Take(8).ToArray();
            if (candidates.Length == 0) return;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(450)))
            {
                try
                {
                    await Task.WhenAll(candidates.Select(async item =>
                    {
                        var detail = await JavaLspRpc.RequestAsync("completionItem/resolve",
                            item.DeepClone(), timeout.Token).ConfigureAwait(false) as JObject;
                        if (detail == null) return;
                        foreach (var key in new[] { "documentation", "additionalTextEdits", "detail", "textEdit" })
                        {
                            if (detail[key] != null && item[key] == null)
                                item[key] = detail[key].DeepClone();
                        }
                    })).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* Nessun blocco sull'editor. */ }
                catch (Exception ex)
                {
                    JavaLanguageStatus.LspFailure("completionItem/resolve", ex.GetType().Name);
                }
            }
        }

        public async Task HandleNotificationAsync(string methodName, JToken methodParam,
            Func<JToken, Task> sendNotification)
        {
            await sendNotification(methodParam);
        }

        public async Task<JToken> HandleRequestAsync(string methodName, JToken methodParam,
            Func<JToken, Task<JToken>> sendRequest)
        {
            // Manteniamo esattamente la semantica delle richieste originali.
            // Anche quando il server risponde con un errore, non lo assorbiamo.
            JavaLanguageStatus.LspRequest(methodName);
            if (methodName == "workspace/executeCommand")
                JavaLanguageStatus.LspExecuteCommand(methodParam);
            try
            {
                JToken response = await sendRequest(methodParam);
                // Le capabilities determinano se Visual Studio PUO' richiedere hover/resolve.
                // Non modifichiamo nessun messaggio LSP.
                if (methodName == "initialize")
                {
                    JavaLanguageStatus.LspInitialize(methodParam, response);
                    return response;
                }

                // Visual Studio talvolta non invia completionItem/resolve dopo la
                // selezione. Risolviamo in anticipo pochi elementi, con budget breve,
                // per ottenere documentazione e additionalTextEdits (import).
                // Se il server e' lento, restituiamo i risultati originali.
                if (methodName == "textDocument/completion")
                    await TryPrefetchCompletionDetailsAsync(response);

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
