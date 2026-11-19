# Java CPS Gradle dependency snapshot launcher (Windows PowerShell 5.1).
# Used only for reading resolved dependencies. No edits to .gradle/.javaproj.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProjectDirectory,
    [Parameter(Mandatory=$true)][string]$InitScript,
    [Parameter(Mandatory=$true)][string]$OutputFile,
    [string]$CommandPath
)

$ErrorActionPreference = 'Stop'
function Fail([string]$message) {
    [Console]::Error.WriteLine('[Java Gradle Dependencies] ' + $message)
    exit 1
}

function CheckLauncher([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    try {
        $full = [IO.Path]::GetFullPath($path)
        if (-not [IO.File]::Exists($full)) { return $null }
        if ([IO.Path]::GetExtension($full) -notin @('.bat','.cmd')) { return $null }
        return $full
    } catch { return $null }
}

function ResolveGradle([string]$project, [string]$requested) {
    $wrapper = CheckLauncher (Join-Path $project 'gradlew.bat')
    if ($wrapper) { return $wrapper }

    $candidate = if ([string]::IsNullOrWhiteSpace($requested)) {
        'gradle.bat'
    } else { $requested.Trim().Trim('"') }
    $direct = CheckLauncher $candidate
    if ($direct) { return $direct }
    if ([IO.Path]::IsPathRooted($candidate) -or
        $candidate.Contains('\') -or $candidate.Contains('/')) {
        $relative = CheckLauncher (Join-Path $project $candidate)
        if ($relative) { return $relative }
        Fail ('Gradle non trovato: ' + $candidate)
    }
    if ($candidate -ne 'gradle.bat') {
        Fail ('Comando Gradle non ammesso: ' + $candidate)
    }

    $found = Get-Command 'gradle.bat' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) {
        $fromPath = CheckLauncher $found.Source
        if ($fromPath) { return $fromPath }
    }
    $gradleHome = $env:GRADLE_HOME
    if (-not [string]::IsNullOrWhiteSpace($gradleHome)) {
        $fromHome = CheckLauncher (Join-Path $gradleHome 'bin\gradle.bat')
        if ($fromHome) { return $fromHome }
    }
    # Chocolatey installations frequently do not expose their PATH to devenv.exe.
    $bases = @($env:ChocolateyInstall, $env:ProgramData) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object {
            if ($_ -eq $env:ProgramData) { Join-Path $_ 'chocolatey' } else { $_ }
        } | Select-Object -Unique
    foreach ($base in $bases) {
        foreach ($packageName in @('gradle','gradle.portable')) {
            $package = Join-Path $base ('lib\' + $packageName)
            if (-not [IO.Directory]::Exists($package)) { continue }
            $dirs = @($package) + @(
                Get-ChildItem -LiteralPath $package -Directory -ErrorAction SilentlyContinue |
                    Sort-Object Name -Descending | ForEach-Object { $_.FullName })
            foreach ($dir in $dirs) {
                $fromBin = CheckLauncher (Join-Path $dir 'bin\gradle.bat')
                if ($fromBin) { return $fromBin }
                $tools = Join-Path $dir 'tools'
                if (-not [IO.Directory]::Exists($tools)) { continue }
                $fromTools = CheckLauncher (Join-Path $tools 'bin\gradle.bat')
                if ($fromTools) { return $fromTools }
                foreach ($version in (Get-ChildItem -LiteralPath $tools -Directory -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '^gradle-[0-9]' } |
                    Sort-Object Name -Descending)) {
                    $launcher = CheckLauncher (Join-Path $version.FullName 'bin\gradle.bat')
                    if ($launcher) { return $launcher }
                }
            }
        }
    }
    Fail 'Gradle non trovato: configurare GRADLE_HOME, PATH o JavaGradleCommand.'
}

try {
    $project = [IO.Path]::GetFullPath($ProjectDirectory)
    if (-not [IO.Directory]::Exists($project)) { Fail 'Directory progetto assente.' }
    if (-not ([IO.File]::Exists((Join-Path $project 'build.gradle')) -or
               [IO.File]::Exists((Join-Path $project 'build.gradle.kts')))) {
        Fail 'build.gradle/build.gradle.kts non trovato.'
    }
    $init = [IO.Path]::GetFullPath($InitScript)
    if (-not [IO.File]::Exists($init)) { Fail 'Script init Gradle mancante.' }
    $output = [IO.Path]::GetFullPath($OutputFile)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') +
        [IO.Path]::DirectorySeparatorChar
    if (-not $output.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        -not $output.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
        Fail 'Output JSON non valido (richiesta cartella temporanea).'
    }
    $launcher = ResolveGradle $project $CommandPath
    $parameters = @(
        '--console=plain', '--no-daemon', '--no-configuration-cache', '-q',
        '-p', $project, '-I', $init,
        ('-DjavaCps.outputFile=' + $output),
        ('-DjavaCps.projectDir=' + $project),
        'javaCpsResolveDependenciesSnapshot'
    )
    Push-Location -LiteralPath $project
    try {
        # Gradle may print warnings on stderr; only its exit code is authoritative.
        $ErrorActionPreference = 'Continue'
        & $launcher @parameters
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
    } finally {
        Pop-Location
    }
    if ($null -eq $code -or [int]$code -ne 0) { Fail ('Gradle exit code: ' + $code) }
    if (-not [IO.File]::Exists($output)) { Fail 'Gradle non ha prodotto lo snapshot JSON.' }
    exit 0
} catch {
    Fail $_.Exception.Message
}
