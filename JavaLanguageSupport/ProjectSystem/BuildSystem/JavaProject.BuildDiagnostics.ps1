# Java CPS build diagnostics: map Java compiler output to native MSBuild messages.
# No project files or diagnostics from JDT LS are modified by this helper.
# This file is dot-sourced by ToolRunner and PlainBuild (Windows PowerShell 5.1).

function Convert-JavaBuildDiagnostic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][ValidateSet('Maven', 'Gradle', 'Plain')][string]$Tool,
        [Parameter(Mandatory = $true)][string]$ProjectDirectory
    )

    # Only diagnostics with an unambiguous .java file and a positive line
    # are mapped. General build tool failures remain in the original log.
    $source = $null
    $lineNumber = 0
    $columnNumber = 1
    $severity = $null
    $message = $null
    $matchResult = $null

    if ($Tool -eq 'Maven') {
        # maven-compiler-plugin: [ERROR] /path/Example.java:[12,5] cannot find symbol
        # A Maven summary may repeat the same source diagnostic; caller deduplicates.
        $matchResult = [regex]::Match($Text,
            '^\[(?<level>ERROR|WARNING)\]\s+(?<file>.+\.java):\[(?<line>\d+)(?:,\s*(?<column>\d+))?\]\s*(?<message>.+)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($matchResult.Success) {
            $source = $matchResult.Groups['file'].Value
            $lineNumber = [int]$matchResult.Groups['line'].Value
            if ($matchResult.Groups['column'].Success) {
                $columnNumber = [int]$matchResult.Groups['column'].Value
            }
            $severity = if ($matchResult.Groups['level'].Value -ieq 'ERROR') { 'error' } else { 'warning' }
            $message = $matchResult.Groups['message'].Value
        }
    }

    if ($null -eq $source) {
        # javac, including output forwarded by Gradle:
        # C:\project\src\Example.java:12: error: cannot find symbol
        # Optional source column is supported by compilers that provide it.
        $matchResult = [regex]::Match($Text,
            '^(?<file>.+\.java):(?<line>\d+):(?:(?<column>\d+):)?\s*(?<level>error|warning):\s*(?<message>.+)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $matchResult.Success) { return $null }
        $source = $matchResult.Groups['file'].Value
        $lineNumber = [int]$matchResult.Groups['line'].Value
        if ($matchResult.Groups['column'].Success) {
            $columnNumber = [int]$matchResult.Groups['column'].Value
        }
        $severity = $matchResult.Groups['level'].Value.ToLowerInvariant()
        $message = $matchResult.Groups['message'].Value
    }

    if ($lineNumber -lt 1 -or $columnNumber -lt 1 -or
        [string]::IsNullOrWhiteSpace($message)) { return $null }

    try {
        $projectRoot = [IO.Path]::GetFullPath($ProjectDirectory).TrimEnd('\', '/')
        $source = $source.Trim()
        # Maven on Windows can report /C:/path instead of C:/path.
        if ($source -match '^/[A-Za-z]:[/\\]') { $source = $source.Substring(1) }
        $source = $source.Replace('/', '\')
        if (-not [IO.Path]::IsPathRooted($source)) {
            $source = [IO.Path]::Combine($projectRoot, $source)
        }
        $fullPath = [IO.Path]::GetFullPath($source)
        if (-not $fullPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not $fullPath.EndsWith('.java', [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.File]::Exists($fullPath)) { return $null }
    } catch { return $null }

    # Native MSBuild/Visual Studio format; works for Error List and double-click.
    $code = if ($severity -eq 'error') { 'JAVA0001' } else { 'JAVA0002' }
    $message = $message.Trim()
    return ('{0}({1},{2}): {3} {4}: {5}' -f
        $fullPath, $lineNumber, $columnNumber, $severity, $code, $message)
}
