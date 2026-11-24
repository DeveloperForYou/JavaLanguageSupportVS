# JavaLanguageSupportVS: conservative Plain-JDK build helper.
# Called by JavaProject.NativeBuild.targets; not an independent build system.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Build', 'Rebuild', 'Clean')]
    [string]$Action,
    [Parameter(Mandatory = $true)]
    [string]$ProjectDirectory,
    [string]$SourcesFile,
    [string]$ClasspathFile
)

$ErrorActionPreference = 'Stop'
try {
    $root = [System.IO.Path]::GetFullPath($ProjectDirectory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw 'La directory del progetto non esiste.'
    }
    $cacheRoot = Join-Path $root '.java-vs'
    $classes = Join-Path $cacheRoot 'classes'
    $testClasses = Join-Path $cacheRoot 'test-classes'
    # JPM-S1-PLAIN-SCOPES
    # A junction at .java-vs or classes must never redirect Clean outside the project.
    foreach ($safeDir in @($cacheRoot, $classes, $testClasses)) {
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
        if (Test-Path -LiteralPath $testClasses) {
            $directory = [IO.Path]::GetFullPath($testClasses)
            if (-not $directory.StartsWith($root + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase) -or
                (Split-Path -Leaf $directory) -ne 'test-classes') {
                throw 'Cartella test-classes non sicura: Clean interrotto.'
            }
            $entry = Get-Item -LiteralPath $directory -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Cartella test-classes collegata: Clean interrotto.'
            }
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
        Write-Output '[Java Build] Output Plain JDK pulito (.java-vs/classes e test-classes).' 
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

    # JPM-S1-PLAIN-SCOPES: explicit <FullPath>|<JavaPackageManagerScope> lines.
    # Legacy lines without separator remain compile. No fixed JAR count limit.
    $compileJarFiles = New-Object 'System.Collections.Generic.List[string]'
    $runtimeJarFiles = New-Object 'System.Collections.Generic.List[string]'
    $testJarFiles = New-Object 'System.Collections.Generic.List[string]'
    if (-not [string]::IsNullOrWhiteSpace($ClasspathFile) -and
        (Test-Path -LiteralPath $ClasspathFile -PathType Leaf)) {
        $jarItems = @(Get-Content -LiteralPath $ClasspathFile -Encoding UTF8 |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
        foreach ($jarItem in $jarItems) {
            $parts = ([string]$jarItem).Split([char[]]@('|'), 2)
            $jarPath = [IO.Path]::GetFullPath([string]$parts[0])
            $scope = if ($parts.Length -lt 2 -or [string]::IsNullOrWhiteSpace($parts[1])) {
                'compile'
            } else { $parts[1].Trim().ToLowerInvariant() }
            if ($scope -notin @('compile', 'provided', 'runtime', 'test')) {
                throw ('Scope JAR sconosciuto: ' + $scope + ' (' + $jarPath + ')')
            }
            if (-not $jarPath.EndsWith('.jar', [StringComparison]::OrdinalIgnoreCase) -or
                -not [IO.File]::Exists($jarPath)) {
                throw ('JAR esterno non trovato o non valido: ' + $jarPath)
            }
            if ($jarPath.IndexOf(';') -ge 0 -or
                $jarPath.IndexOfAny([char[]]@([char]34, [char]10, [char]13)) -ge 0) {
                throw ('Percorso JAR non valido per il classpath javac: ' + $jarPath)
            }
            $jarPath = $jarPath.Replace('\', '/')
            if ($scope -in @('compile','provided') -and -not $compileJarFiles.Contains($jarPath)) {
                $compileJarFiles.Add($jarPath)
            }
            if ($scope -in @('compile','runtime') -and -not $runtimeJarFiles.Contains($jarPath)) {
                $runtimeJarFiles.Add($jarPath)
            }
            if (-not $testJarFiles.Contains($jarPath)) { $testJarFiles.Add($jarPath) }
        }
    }

    $buildDirectory = Join-Path $cacheRoot 'build'
    [IO.Directory]::CreateDirectory($classes) | Out-Null
    [IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
    $mainSources = New-Object 'System.Collections.Generic.List[string]'
    $testSources = New-Object 'System.Collections.Generic.List[string]'
    foreach ($source in $normalized) {
        # Only canonical test source roots are considered test-only; all other
        # JavaCompile inputs continue to belong to the existing main build.
        if ($source -match '(?i)/src/test/java/' -or $source -match '(?i)/src/test/') {
            $testSources.Add($source)
        } else { $mainSources.Add($source) }
    }
    $runtimeList = Join-Path $buildDirectory 'runtime-jars.txt'
    [IO.File]::WriteAllLines($runtimeList, [string[]]$runtimeJarFiles.ToArray(),
        ([System.Text.UTF8Encoding]::new($false)))

    function Invoke-ScopedJavac {
        param([string]$Phase, [string]$OutputDirectory, [string[]]$PhaseSources,
            [string[]]$PhaseClasspath, [string]$ArgumentsFile)
        if ($PhaseSources.Count -eq 0) { return }
        [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
        $entries = New-Object 'System.Collections.Generic.List[string]'
        $entries.Add('-encoding'); $entries.Add('UTF-8')
        $entries.Add('-d'); $entries.Add('"' + $OutputDirectory.Replace('\','/') + '"')
        if ($PhaseClasspath.Count -gt 0) {
            $entries.Add('-classpath')
            $entries.Add('"' + [string]::Join(';', $PhaseClasspath) + '"')
        }
        foreach ($source in $PhaseSources) { $entries.Add('"' + $source + '"') }
        [IO.File]::WriteAllLines($ArgumentsFile, $entries,
            ([System.Text.UTF8Encoding]::new($false)))
        Write-Output ('[Java Build] ' + $Phase + ': ' + $PhaseSources.Count +
            ' sorgenti; ' + $PhaseClasspath.Count + ' riferimenti classpath')
        $ErrorActionPreference = 'Continue' # javac stderr must be converted, not terminate the pipeline.
        & $javac ('@' + $ArgumentsFile) 2>&1 | ForEach-Object {
            $line = if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $_.Exception.Message
            } else { [string]$_ }
            $mappedDiagnostic = Convert-JavaBuildDiagnostic -Text $line -Tool Plain -ProjectDirectory $root
            if ($null -ne $mappedDiagnostic) { Write-Output $mappedDiagnostic }
            else { Write-Output $line }
        }
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode -or [int]$exitCode -ne 0) {
            throw ('javac ' + $Phase + ' terminato con codice ' + $exitCode)
        }
    }

    Push-Location -LiteralPath $root
    try {
        Invoke-ScopedJavac -Phase 'main' -OutputDirectory $classes `
            -PhaseSources ([string[]]$mainSources.ToArray()) `
            -PhaseClasspath ([string[]]$compileJarFiles.ToArray()) `
            -ArgumentsFile (Join-Path $buildDirectory 'javac-main.args')
        if ($testSources.Count -gt 0) {
            $testClasspath = New-Object 'System.Collections.Generic.List[string]'
            $testClasspath.Add($classes.Replace('\','/'))
            foreach ($jar in $testJarFiles) { if (-not $testClasspath.Contains($jar)) {
                $testClasspath.Add($jar)
            } }
            Invoke-ScopedJavac -Phase 'test' -OutputDirectory $testClasses `
                -PhaseSources ([string[]]$testSources.ToArray()) `
                -PhaseClasspath ([string[]]$testClasspath.ToArray()) `
                -ArgumentsFile (Join-Path $buildDirectory 'javac-test.args')
        }
    } finally { Pop-Location }
    Write-Output '[Java Build] Build Plain JDK completata.'
    exit 0
} catch {
    [Console]::Error.WriteLine('[Java Build] ERRORE: ' + $_.Exception.Message)
    exit 1
}
