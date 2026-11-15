# F4.B.1: read-only Maven dependency:tree for the CPS virtual tree.
# Maven is authoritative; no modification of pom.xml or .javaproj.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProjectDirectory,
    [Parameter(Mandatory=$true)][string]$OutputFile,
    [string]$CommandPath
)
$ErrorActionPreference = 'Stop'

function Fail([string]$reason) {
    [Console]::Error.WriteLine('[Java Maven Dependencies] ' + $reason)
    exit 1
}
function CheckLauncher([string]$candidate) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    try {
        $full = [IO.Path]::GetFullPath($candidate.Trim().Trim('"'))
        if (([IO.Path]::GetExtension($full) -notin @('.cmd', '.bat')) -or
            -not [IO.File]::Exists($full)) { return $null }
        return $full
    } catch { return $null }
}
function ResolveMaven([string]$project, [string]$requested) {
    $wrapper = CheckLauncher (Join-Path $project 'mvnw.cmd')
    if ($wrapper) { return $wrapper }

    $candidate = if ([string]::IsNullOrWhiteSpace($requested)) { 'mvn.cmd' } else { $requested.Trim().Trim('"') }
    $direct = CheckLauncher $candidate
    if ($direct) { return $direct }
    if ([IO.Path]::IsPathRooted($candidate) -or $candidate.Contains('\') -or $candidate.Contains('/')) {
        $relative = CheckLauncher (Join-Path $project $candidate)
        if ($relative) { return $relative }
        Fail ('Eseguibile Maven non trovato: ' + $candidate)
    }
    if ($candidate -ne 'mvn.cmd') { Fail ('Nome comando Maven non consentito: ' + $candidate) }
    $found = Get-Command -Name 'mvn.cmd' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        $path = CheckLauncher $found.Source
        if ($path) { return $path }
    }
    foreach ($mavenHome in @($env:MAVEN_HOME, $env:M2_HOME)) {
        if ([string]::IsNullOrWhiteSpace($mavenHome)) { continue }
        $path = CheckLauncher (Join-Path $mavenHome 'bin\mvn.cmd')
        if ($path) { return $path }
    }
    $bases = @($env:ChocolateyInstall, (Join-Path $env:ProgramData 'chocolatey')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($base in $bases) {
        $package = Join-Path $base 'lib\maven'
        if (-not [IO.Directory]::Exists($package)) { continue }
        foreach ($dir in (@($package) + @(
            Get-ChildItem -LiteralPath $package -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending | ForEach-Object { $_.FullName }))) {
            $path = CheckLauncher (Join-Path $dir 'bin\mvn.cmd')
            if ($path) { return $path }
        }
    }
    Fail 'Maven non trovato: configurare MAVEN_HOME o il PATH.'
}
try {
    $project = [IO.Path]::GetFullPath($ProjectDirectory)
    if (-not [IO.Directory]::Exists($project) -or
        -not [IO.File]::Exists((Join-Path $project 'pom.xml'))) {
        Fail 'Directory Maven / pom.xml non disponibile.'
    }
    $output = [IO.Path]::GetFullPath($OutputFile)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $output.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        $output -notmatch '\.json$') {
        Fail 'Output JSON esterno alla directory temporanea.'
    }
    $launcher = ResolveMaven $project $CommandPath
    Push-Location -LiteralPath $project
    try {
        # Maven Dependency Plugin >= 3.7.0 supports JSON output.
        # -N: only the current project, do not resolve unrelated reactor modules.
        # Maven can fetch missing plugin/dependencies; do not call on the UI thread.
        $ErrorActionPreference = 'Continue'
        & $launcher '-B' '-ntp' '-N' 'org.apache.maven.plugins:maven-dependency-plugin:3.7.1:tree' '-DoutputType=json' ('-DoutputFile=' + $output)
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
    } finally {
        Pop-Location
    }
    if ($null -eq $code -or [int]$code -ne 0) { Fail ('Maven exit code: ' + $code) }
    if (-not [IO.File]::Exists($output)) { Fail 'Maven non ha creato il JSON.' }
    exit 0
} catch {
    Fail $_.Exception.Message
}
