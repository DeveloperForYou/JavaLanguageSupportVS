using System;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S1: a portable, intentionally small set of direct dependency scopes.
    // Unrecognised Maven/Gradle configurations remain readable but are never rewritten.
    internal static class JavaPackageScopes
    {
        internal const string PlainMetadata = "JavaPackageManagerScope";
        internal static readonly string[] Supported = { "compile", "provided", "runtime", "test" };

        internal static string Normalize(string scope)
        {
            return string.IsNullOrWhiteSpace(scope) ? "compile" : scope.Trim().ToLowerInvariant();
        }

        internal static bool IsSupported(string scope)
        {
            switch (Normalize(scope))
            {
                case "compile": case "provided": case "runtime": case "test": return true;
                default: return false;
            }
        }

        internal static string Require(string scope)
        {
            string value = Normalize(scope);
            if (!IsSupported(value))
                throw new NotSupportedException("Scope JVM non supportato: " + value +
                    ". Valori consentiti: compile, provided, runtime, test.");
            return value;
        }

        internal static string ToGradle(string scope)
        {
            switch (Require(scope))
            {
                case "compile": return "implementation";
                case "provided": return "compileOnly";
                case "runtime": return "runtimeOnly";
                default: return "testImplementation";
            }
        }

        internal static string FromGradle(string configuration)
        {
            switch (configuration)
            {
                case "api": case "implementation": case "compile": return "compile";
                case "compileOnly": return "provided";
                case "runtimeOnly": return "runtime";
                case "testImplementation": case "testCompileOnly": case "testRuntimeOnly": return "test";
                default: return configuration ?? "compile";
            }
        }
    }
}
