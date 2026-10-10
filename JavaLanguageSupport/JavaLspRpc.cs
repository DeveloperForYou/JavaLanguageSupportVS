using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace JavaLanguageSupport
{
    // Canale già posseduto da Visual Studio: nessun secondo client LSP e nessun altro processo Java.
    // Il client standard continua a gestire didOpen/didChange/didClose.
    internal static class JavaLspRpc
    {
        private static JsonRpc rpc;
        internal static bool Connected => Volatile.Read(ref rpc) != null;

        internal static void Attach(JsonRpc value) => Volatile.Write(ref rpc, value);
        internal static void Detach() => Volatile.Write(ref rpc, null);

        internal static async Task<JToken> RequestAsync(string method, object parameters, CancellationToken token)
        {
            var current = Volatile.Read(ref rpc);
            if (current == null) return null;
            JavaLanguageStatus.LspRequest(method);
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
                return null;
            }
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
