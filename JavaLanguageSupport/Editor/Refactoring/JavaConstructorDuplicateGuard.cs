using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Conservative preflight to prevent generating a constructor whose signature
    /// already occurs in the current unsaved Java source. JDT LS performs the edit.
    /// Complex/ambiguous signatures are rejected rather than risking duplicates.
    /// </summary>
    internal static class JavaConstructorDuplicateGuard
    {
        internal static bool Allowed(ITextSnapshot snapshot, JArray constructors,
            JArray fields, out string reason)
        {
            reason = null;
            if (snapshot == null || constructors == null || constructors.Count == 0) return false;
            string source = snapshot.GetText();
            // Mask comments/strings to exclude constructor-looking text in examples.
            string masked = Mask(source);
            string name = null;
            var nameMatch = Regex.Match(masked,
                @"\b(?:class|enum|record)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\b");
            if (nameMatch.Success) name = nameMatch.Groups["name"].Value;
            if (name == null) { reason = "Impossibile individuare la classe: generazione non sicura."; return false; }
            // Nested/multiple types cannot be attributed safely from this
            // lightweight source check: prefer cancel to inserting duplicates.
            if (Regex.Matches(masked,
                @"\b(?:class|enum|record)\s+[A-Za-z_$][A-Za-z0-9_$]*\b").Count > 1)
            {
                reason = "Più tipi nel file: verifica prima i costruttori già presenti.";
                return false;
            }
            var existing = new List<string[]>();
            // Match declarations only, not new ClassName(...), calls, or commented text.
            var pattern = @"(?<![\w$.])" + Regex.Escape(name) +
                @"\s*\((?<args>[^()]*)\)\s*(?:throws\s+[\w.,\s]+)?\s*\{";
            foreach (Match match in Regex.Matches(masked, pattern))
            {
                string[] args;
                if (!ParseParameters(source.Substring(match.Groups["args"].Index,
                    match.Groups["args"].Length), out args))
                {
                    reason = "Firma di un costruttore esistente non riconosciuta. Controlla manualmente.";
                    return false;
                }
                existing.Add(args);
            }
            var selectedFields = fields == null ? new List<string>() : fields.OfType<JObject>()
                .Select(x => (string)x["type"] ?? "").ToList();
            foreach (JObject ctor in constructors.OfType<JObject>())
            {
                var types = new List<string>();
                var parameters = ctor["parameters"] as JArray;
                if (parameters != null)
                    types.AddRange(parameters.Select(x => (string)x ?? ""));
                types.AddRange(selectedFields);
                var normalized = types.Select(Normalize).ToArray();
                if (normalized.Any(x => x.Length == 0))
                { reason = "Il server ha restituito un tipo di parametro non riconosciuto."; return false; }
                foreach (var other in existing)
                    if (other.Length == normalized.Length && other.Select(Normalize)
                        .SequenceEqual(normalized, StringComparer.Ordinal))
                    {
                        reason = "Un costruttore con questa firma esiste gia': " +
                            name + "(" + string.Join(", ", types) + ").";
                        return false;
                    }
            }
            return true;
        }
        private static string Normalize(string name)
        {
            name = (name ?? "").Trim().Replace("...", "[]").Replace(" ", "");
            // Java's standard simple and fully qualified type forms.
            name = name.Replace("java.lang.", "");
            return name;
        }
        private static bool ParseParameters(string text, out string[] types)
        {
            types = new string[0];
            if (string.IsNullOrWhiteSpace(text)) return true;
            var chunks = new List<string>();
            int level = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '<') level++;
                if (text[i] == '>') level--;
                if (text[i] == ',' && level == 0)
                { chunks.Add(text.Substring(start, i - start)); start = i + 1; }
            }
            if (level != 0) return false;
            chunks.Add(text.Substring(start));
            var result = new List<string>();
            foreach (string chunk in chunks)
            {
                string p = Regex.Replace(chunk.Trim(), @"\bfinal\b", "").Trim();
                p = Regex.Replace(p, @"@[\w.]+(?:\([^)]*\))?", "").Trim();
                var match = Regex.Match(p, @"^(?<type>.+?)\s+(?<name>[A-Za-z_$][\w$]*)(?<array>\s*(?:\[\s*\])*)$");
                if (!match.Success) return false;
                result.Add(match.Groups["type"].Value.Trim() + match.Groups["array"].Value.Replace(" ", ""));
            }
            types = result.ToArray(); return true;
        }
        private static string Mask(string text)
        {
            var chars = text.ToCharArray();
            int state = 0; // 0 normal,1 line comment,2 block,3 string,4 char literal
            for (int i = 0; i < chars.Length; i++)
            {
                char c = text[i]; char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (state == 0)
                {
                    if (c == '/' && next == '/') { state = 1; chars[i] = chars[i + 1] = ' '; i++; }
                    else if (c == '/' && next == '*') { state = 2; chars[i] = chars[i + 1] = ' '; i++; }
                    else if (c == '"') { state = 3; chars[i] = ' '; }
                    else if (c == '\'') { state = 4; chars[i] = ' '; }
                }
                else if (state == 1) { if (c == '\n' || c == '\r') state = 0; else chars[i] = ' '; }
                else if (state == 2)
                {
                    if (c == '*' && next == '/') { chars[i] = chars[i + 1] = ' '; i++; state = 0; }
                    else if (c != '\n' && c != '\r') chars[i] = ' ';
                }
                else if (state == 3 || state == 4)
                {
                    if (c == '\\' && i + 1 < text.Length) { chars[i] = chars[i + 1] = ' '; i++; }
                    else if (c == (state == 3 ? '"' : '\'')) { chars[i] = ' '; state = 0; }
                    else if (c != '\n' && c != '\r') chars[i] = ' ';
                }
            }
            return new string(chars);
        }
    }
}
