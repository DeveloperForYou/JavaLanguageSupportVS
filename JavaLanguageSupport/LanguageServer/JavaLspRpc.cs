using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace JavaLanguageSupport.LanguageServer
{
    // Canale già posseduto da Visual Studio: nessun secondo client LSP e nessun altro processo Java.
    // Il client standard continua a gestire didOpen/didChange/didClose.
    internal static class JavaLspRpc
    {
        private static JsonRpc rpc;
        // E3 Indexing Hotfix: documentSymbol is also used by CodeLens and
        // Navigation Bar. Serialize those requests to avoid racing Eclipse's
        // linked-resource creation during import. Never block the UI thread.
        private static readonly SemaphoreSlim DocumentSymbolsGate =
            new SemaphoreSlim(1, 1);
        internal static bool Connected => Volatile.Read(ref rpc) != null;
        internal static object ConnectionIdentity => Volatile.Read(ref rpc);

        internal static void Attach(JsonRpc value) => Volatile.Write(ref rpc, value);
        internal static void Detach() => Volatile.Write(ref rpc, null);

        internal static async Task<JToken> RequestAsync(string method, object parameters, CancellationToken token)
        {
            var current = Volatile.Read(ref rpc);
            if (current == null) return null;
            JavaLanguageStatus.LspRequest(method);
            bool gated = string.Equals(method, "textDocument/documentSymbol",
                StringComparison.Ordinal);
            if (gated) await DocumentSymbolsGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Usare lo stesso JsonRpc fornito da ILanguageClientCustomMessage2.
                var result = await current.InvokeWithParameterObjectAsync<JToken>(
                    method, parameters, token).ConfigureAwait(false);
                if (method == "textDocument/codeAction" && result is JArray)
                    JavaLanguageStatus.LspCodeActions((JArray)result);
                JavaLanguageStatus.LspResponse(method, result is JArray ? ((JArray)result).Count : (result == null ? 0 : 1),
                    result != null && method == "textDocument/hover", false);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                JavaLanguageStatus.LspFailure(method, ex.GetType().Name + ": " + ex.Message);
                // Per la rinomina l'errore deve arrivare fino alla UI: non
                // trasformarlo in WorkspaceEdit nullo nascondendo la causa.
                // F2I4-RECURSIVE-PACKAGE: never interpret an LSP
                // protocol failure as an empty, valid recursive rename.
                if (string.Equals(method, "textDocument/rename", StringComparison.Ordinal) ||
                    string.Equals(method, "workspace/willRenameFiles", StringComparison.Ordinal))
                    throw;
                return null;
            }
            finally
            {
                if (gated) DocumentSymbolsGate.Release();
            }
        }

        // Send a protocol notification (not a request). Used after successful
        // CPS package changes so JDT LS can reconcile its Java project model.
        internal static async Task<bool> NotifyAsync(string method, object parameters)
        {
            var current = Volatile.Read(ref rpc);
            if (current == null) return false;
            await current.NotifyWithParameterObjectAsync(method, parameters)
                .ConfigureAwait(false);
            // F2J.3: observe CPS bridge metadata without serializing Java text.
            if (method == "textDocument/didOpen" ||
                method == "textDocument/didChange" ||
                method == "textDocument/didClose")
                JavaDiagnosticsLatencyTracker.ObserveBridgeOutgoing(method, parameters);
            return true;
        }

        // F2I3-JAVA-MODEL-REFRESH: unlike RequestAsync, preserve errors for
        // commands which schedule an Eclipse project refresh. A void reply is OK.
        internal static async Task<bool> ExecuteWorkspaceCommandStrictAsync(
            string command, object[] arguments)
        {
            var current = Volatile.Read(ref rpc);
            if (current == null) return false;
            await current.InvokeWithParameterObjectAsync<JToken>(
                "workspace/executeCommand", new { command, arguments },
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        internal static string FileUri(ITextDocument document)
        {
            if (document == null || string.IsNullOrWhiteSpace(document.FilePath)) return null;
            return new Uri(Path.GetFullPath(document.FilePath)).AbsoluteUri;
        }

        internal static object Position(ITextSnapshot snapshot, int offset)
        {
            int safeOffset = Math.Max(0, Math.Min(offset, snapshot.Length));
            var line = snapshot.GetLineFromPosition(safeOffset);
            return new { line = line.LineNumber, character = safeOffset - line.Start.Position };
        }

        internal static bool SameUri(string left, string right)
        {
            Uri a, b;
            return Uri.TryCreate(left, UriKind.Absolute, out a)
                && Uri.TryCreate(right, UriKind.Absolute, out b)
                && Uri.Compare(a, b, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0;
        }
    }
}
