using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>Uses the token-type ordering negotiated in the real JDT LS initialize response.</summary>
    internal static class JavaSemanticLegend
    {
        private static readonly object Sync = new object();
        private static string[] types = new string[0];
        internal static event EventHandler Changed;

        internal static void ReadInitialize(JToken response)
        {
            var entries = response == null ? null :
                response.SelectToken("capabilities.semanticTokensProvider.legend.tokenTypes") as JArray;
            string[] updated = entries == null ? new string[0] :
                entries.Select(x => (string)x ?? "").ToArray();
            lock (Sync) types = updated;
            FireChanged();
        }

        internal static void Reset()
        {
            lock (Sync) types = new string[0];
            FireChanged();
        }

        internal static bool Available
        {
            get { lock (Sync) return types.Length > 0; }
        }

        internal static string TokenType(int index)
        {
            lock (Sync) return index >= 0 && index < types.Length ? types[index] : null;
        }

        private static void FireChanged()
        {
            var handler = Changed;
            if (handler != null) handler(null, EventArgs.Empty);
        }
    }
}
