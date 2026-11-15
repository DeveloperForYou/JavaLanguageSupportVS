# JavaLanguageSupportVS: conservative Plain-JDK build helper.
# Called by JavaProject.NativeBuild.targets; not an independent build system.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Build', 'Rebuild', 'Clean')]
    [string]$Action,
    [Parameter(Mandatory = $true)]
    [string]$ProjectDirectory,
    [string]$SourcesFile
)

$ErrorActionPreference = 'Stop'
try {
    $root = [System.IO.Path]::GetFullPath($ProjectDirectory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw 'La directory del progetto non esiste.'
    }
    $cacheRoot = Join-Path $root '.java-vs'
    $classes = Join-Path $cacheRoot 'classes'
    # A junction at .java-vs or classes must never redirect Clean outside the project.
    foreach ($safeDir in @($cacheRoot, $classes)) {
        if (Test-Path -LiteralPath $safeDir) {
            $entry = Get-Item -LiteralPath $safeDir -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Directory di output collegata non supportata: ' + $safeDir)
            }
        }
    }

    if ($Action -eq 'Clean' -or $Action -eq 'Rebuild') {
        if (Test-Path -LiteralPath $classes) {
            $directory = [System.IO.Path]::GetFullPath($classes)
            if (-not $directory.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                (Split-Path -Leaf $directory) -ne 'classes') {
                throw 'Cartella classi non sicura: Clean interrotto.'
            }
            # Never recurse through a redirected/symlinked classes directory.
            $entry = Get-Item -LiteralPath $directory -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Cartella classi collegata: Clean interrotto.'
            }
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
        Write-Output '[Java Build] Output Plain JDK pulito (.java-vs/classes).'
        if ($Action -eq 'Clean') { exit 0 }
    }

    if ([string]::IsNullOrWhiteSpace($SourcesFile) -or
        -not (Test-Path -LiteralPath $SourcesFile -PathType Leaf)) {
        throw 'Elenco dei sorgenti JavaCompile mancante.'
    }
    $sources = @(Get-Content -LiteralPath $SourcesFile -Encoding UTF8 | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    } | Select-Object -Unique)
    if ($sources.Count -eq 0) {
        throw 'Nessun sorgente Java da compilare: controllare JavaCompile nel .javaproj.'
    }
    if ($sources.Count -gt 10000) {
        throw 'Oltre 10.000 sorgenti JavaCompile: Build Plain non supportata.'
    }
    $normalized = New-Object 'System.Collections.Generic.List[string]'
    foreach ($source in $sources) {
        $file = [System.IO.Path]::GetFullPath([string]$source)
        if (-not $file.StartsWith($root + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not $file.EndsWith('.java', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw ('Sorgente JavaCompile esterno, mancante o non Java: ' + $file)
        }
        if ($file.IndexOfAny([char[]]@([char]34, [char]10, [char]13)) -ge 0) {
            throw ('Percorso del sorgente non valido per javac @argfile: ' + $file)
        }
        $normalized.Add($file.Replace('\', '/'))
    }

    # Use the shared Java compiler diagnostic formatter.
    $diagnosticsScript = Join-Path $PSScriptRoot 'JavaProject.BuildDiagnostics.ps1'
    if (-not [IO.File]::Exists($diagnosticsScript)) {
        throw ('Helper diagnostiche mancante: ' + $diagnosticsScript)
    }
    . $diagnosticsScript

    $javaHome = [Environment]::GetEnvironmentVariable('JAVA_HOME')
    if (-not [string]::IsNullOrWhiteSpace($javaHome)) {
        $javac = Join-Path $javaHome.Trim().Trim('"') 'bin\javac.exe'
        if (-not (Test-Path -LiteralPath $javac -PathType Leaf)) {
            throw ('JAVA_HOME impostata ma javac.exe assente: ' + $javac)
        }
    } else {
        $found = Get-Command 'javac.exe' -ErrorAction SilentlyContinue
        if ($null -eq $found) {
            throw 'javac.exe non trovato: configurare JAVA_HOME o aggiungere un JDK al PATH.'
        }
        $javac = $found.Source
    }

    $buildDirectory = Join-Path $cacheRoot 'build'
    [IO.Directory]::CreateDirectory($classes) | Out-Null
    [IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
    $argfile = Join-Path $buildDirectory 'javac.args'
    $entries = New-Object 'System.Collections.Generic.List[string]'
    $entries.Add('-encoding')
    $entries.Add('UTF-8')
    $entries.Add('-d')
    $entries.Add('"' + $classes.Replace('\', '/') + '"')
    foreach ($source in $normalized) { $entries.Add('"' + $source + '"') }
    [IO.File]::WriteAllLines($argfile, $entries, ([System.Text.UTF8Encoding]::new($false)))

    Write-Output ('[Java Build] javac: ' + $sources.Count + ' sorgenti, output ' + $classes)
    Push-Location -LiteralPath $root
    try {
        $ErrorActionPreference = 'Continue' # javac stderr must not become a terminating PowerShell error.
        & $javac ('@' + $argfile) 2>&1 | ForEach-Object {
            $line = if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $_.Exception.Message
            } else { [string]$_ }
            $mappedDiagnostic = Convert-JavaBuildDiagnostic -Text $line -Tool Plain -ProjectDirectory $root
            if ($null -ne $mappedDiagnostic) { Write-Output $mappedDiagnostic }
            else { Write-Output $line }
        }
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($null -eq $exitCode -or [int]$exitCode -ne 0) {
        throw ('javac terminato con codice ' + $exitCode)
    }
    Write-Output '[Java Build] Build Plain JDK completata.'
    exit 0
} catch {
    [Console]::Error.WriteLine('[Java Build] ERRORE: ' + $_.Exception.Message)
    exit 1
}
