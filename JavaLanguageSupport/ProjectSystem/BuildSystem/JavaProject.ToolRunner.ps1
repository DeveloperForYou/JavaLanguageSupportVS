# Java CPS: resolve Maven/Gradle as a real file path before MSBuild Exec calls it.
# This is a build-time helper, NOT a language server or a general-purpose shell.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Maven','Gradle')][string]$Tool,
    [Parameter(Mandatory=$true)][ValidateSet('Build','Rebuild','Clean')][string]$Action,
    [Parameter(Mandatory=$true)][string]$ProjectDirectory,
    [string]$CommandPath
)

$ErrorActionPreference = 'Stop'
function Fail([string]$message) {
    [Console]::Error.WriteLine('[Java Build] ' + $message)
    exit 1
}

function CheckPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    try {
        $full = [System.IO.Path]::GetFullPath($path)
        if (![System.IO.File]::Exists($full)) { return $null }
        if ([System.IO.Path]::GetExtension($full) -notin @('.cmd','.bat')) { return $null }
        return $full
    } catch { return $null }
}

function FindExecutable([string]$tool, [string]$requested, [string]$project) {
    $localWrapper = if ($tool -eq 'Maven') { 'mvnw.cmd' } else { 'gradlew.bat' }
    $standard = if ($tool -eq 'Maven') { 'mvn.cmd' } else { 'gradle.bat' }
    $candidate = if ($requested) { $requested.Trim().Trim('"') } else { $standard }

    # The .targets deliberately prefers the checked-in local wrapper.
    $direct = CheckPath $candidate
    if ($direct) { return $direct }
    if ([System.IO.Path]::IsPathRooted($candidate) -or
        $candidate.Contains('\') -or $candidate.Contains('/')) {
        $relative = CheckPath (Join-Path $project $candidate)
        if ($relative) { return $relative }
        Fail ('Eseguibile non trovato: ' + $candidate)
    }

    if ($candidate -notin @($standard, $localWrapper)) {
        Fail ('Nome comando non ammesso: ' + $candidate + '. Specificare un percorso .cmd/.bat completo.')
    }
    $found = Get-Command -Name $candidate -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) {
        $fromPath = CheckPath $found.Source
        if ($fromPath) { return $fromPath }
    }

    # MSBuild's cmd.exe environment can differ from the interactive PowerShell PATH.
    # Explicit Maven/Gradle homes are portable and need no global PATH changes.
    $homes = if ($tool -eq 'Maven') { @($env:MAVEN_HOME, $env:M2_HOME) } else { @($env:GRADLE_HOME) }
    foreach ($toolHome in $homes) {
        if ([string]::IsNullOrWhiteSpace($toolHome)) { continue }
        $fromHome = CheckPath (Join-Path $toolHome ('bin\' + $standard))
        if ($fromHome) { return $fromHome }
    }

    # Chocolatey installs often expose a shim only to a different shell, while
    # the actual batch launcher lives under lib\maven or lib\gradle.
    $chocolateyBases = @($env:ChocolateyInstall, (Join-Path $env:ProgramData 'chocolatey')) |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($base in $chocolateyBases) {
        $lib = Join-Path $base 'lib'
        if (![System.IO.Directory]::Exists($lib)) { continue }
        $packageDirs = @()
        if ($tool -eq 'Maven') {
            $packageDirs = @((Join-Path $lib 'maven'))
        } else {
            $packageDirs = @((Join-Path $lib 'gradle'), (Join-Path $lib 'gradle.portable'))
        }
        foreach ($packageDir in $packageDirs) {
            if (![System.IO.Directory]::Exists($packageDir)) { continue }
            $candidates = @($packageDir) + @(
                Get-ChildItem -LiteralPath $packageDir -Directory -ErrorAction SilentlyContinue |
                    Sort-Object -Property Name -Descending | ForEach-Object { $_.FullName })
            foreach ($dir in $candidates) {
                $resolved = CheckPath (Join-Path $dir ('bin\' + $standard))
                if ($resolved) { return $resolved }

                # Gradle Chocolatey: il launcher si trova anche sotto tools\gradle-<versione>\bin.
                # La variabile macchina GRADLE_HOME potrebbe non essere ancora visibile a Visual Studio.
                if ($tool -eq 'Gradle') {
                    $toolsDir = Join-Path $dir 'tools'
                    if ([System.IO.Directory]::Exists($toolsDir)) {
                        $directTools = CheckPath (Join-Path $toolsDir ('bin\' + $standard))
                        if ($directTools) { return $directTools }
                        $gradleVersions = Get-ChildItem -LiteralPath $toolsDir -Directory -ErrorAction SilentlyContinue |
                            Where-Object { $_.Name -match '^gradle-[0-9]' } |
                            Sort-Object -Property Name -Descending
                        foreach ($gradleVersion in $gradleVersions) {
                            $toolBinary = CheckPath (Join-Path $gradleVersion.FullName ('bin\' + $standard))
                            if ($toolBinary) { return $toolBinary }
                        }
                    }
                }
            }
        }
    }
    Fail ('Impossibile trovare ' + $standard + '. Installare il tool, impostare MAVEN_HOME/GRADLE_HOME o Java' + $tool + 'Command nel .javaproj.')
}

try {
    $project = [System.IO.Path]::GetFullPath($ProjectDirectory)
    if (![System.IO.Directory]::Exists($project)) { Fail ('Directory progetto assente: ' + $project) }
    $executable = FindExecutable $Tool $CommandPath $project
    $parameters = if ($Tool -eq 'Maven') {
        switch ($Action) {
            'Build'   { @('-B', '-DskipTests', 'package') }
            'Rebuild' { @('-B', '-DskipTests', 'clean', 'package') }
            'Clean'   { @('-B', 'clean') }
        }
    } else {
        switch ($Action) {
            'Build'   { @('--console=plain', 'build') }
            'Rebuild' { @('--console=plain', 'clean', 'build') }
            'Clean'   { @('--console=plain', 'clean') }
        }
    }
    [Console]::Out.WriteLine('[Java Build] ' + $Tool + ' risolto: ' + $executable)
    Push-Location -LiteralPath $project
    try {
        # Native launchers may write non-fatal warnings to stderr.
        # Respect their exit code rather than PowerShell error-stream heuristics.
        $ErrorActionPreference = 'Continue'
        & $executable @parameters
        $ErrorActionPreference = 'Stop'
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($null -eq $code) { Fail ('Nessun codice di uscita restituito da ' + $Tool) }
    exit [int]$code
} catch {
    Fail $_.Exception.Message
}
