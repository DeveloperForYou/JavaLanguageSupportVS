using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // F4.F.2.8.1: update candidates come from repository metadata, not from
    // the potentially stale Solr `latestVersion` field. The result is advisory;
    // Gradle/Maven still perform their own dependency conflict resolution.
    internal static class JavaPackageUpdateSelector
    {
        private static readonly Regex NumericHead = new Regex(
            @"\A(?<head>\d+(?:\.\d+)*)(?<suffix>.*)\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Preliminary = new Regex(
            @"(?:^|[.\-_])(?:alpha|beta|milestone|preview|snapshot|dev|ea|rc|cr|m\d+|rc\d+)(?:[.\-_\d]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static string NewestCompatible(string installed, IEnumerable<string> versions)
        {
            if (string.IsNullOrWhiteSpace(installed) || versions == null) return null;
            string flavor = Flavor(installed);
            string best = null;
            foreach (string candidate in versions)
            {
                if (string.IsNullOrWhiteSpace(candidate) || IsPreRelease(candidate)) continue;
                // Guava publishes separate Android and JRE tracks: do not suggest
                // an Android artifact as an upgrade for an installed JRE artifact.
                if (flavor != null && !string.Equals(flavor, Flavor(candidate),
                    StringComparison.OrdinalIgnoreCase)) continue;
                if (CompareVersions(candidate, installed) <= 0) continue;
                if (best == null || CompareVersions(candidate, best) > 0) best = candidate;
            }
            return best;
        }

        private static string Flavor(string version)
        {
            if (version.EndsWith("-jre", StringComparison.OrdinalIgnoreCase)) return "jre";
            if (version.EndsWith("-android", StringComparison.OrdinalIgnoreCase)) return "android";
            return null;
        }

        private static bool IsPreRelease(string version)
        {
            return Preliminary.IsMatch(version);
        }

        // Numeric release prefix, followed by known stable qualifiers.
        // Unknown/non-numeric schemes are intentionally not called upgrades;
        // avoid reporting a downgrade as an update.
        internal static int CompareVersions(string left, string right)
        {
            var a = NumericHead.Match(left ?? "");
            var b = NumericHead.Match(right ?? "");
            if (!a.Success || !b.Success) return 0;
            var partsA = a.Groups["head"].Value.Split('.');
            var partsB = b.Groups["head"].Value.Split('.');
            for (int i = 0; i < Math.Max(partsA.Length, partsB.Length); i++)
            {
                string x = i < partsA.Length ? partsA[i].TrimStart('0') : "";
                string y = i < partsB.Length ? partsB[i].TrimStart('0') : "";
                if (x.Length != y.Length) return x.Length > y.Length ? 1 : -1;
                int cmp = string.CompareOrdinal(x, y);
                if (cmp != 0) return cmp;
            }
            string suffixA = a.Groups["suffix"].Value;
            string suffixB = b.Groups["suffix"].Value;
            if (string.Equals(suffixA, suffixB, StringComparison.OrdinalIgnoreCase)) return 0;
            // A numeric release without suffix is generally newer than its
            // pre-release of the same numeric version.
            if (suffixA.Length == 0 && IsPreRelease(suffixB)) return 1;
            if (suffixB.Length == 0 && IsPreRelease(suffixA)) return -1;
            // Don't invent ordering for incomparable qualifiers.
            return 0;
        }
    }
}
