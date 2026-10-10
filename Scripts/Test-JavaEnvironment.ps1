# Esegui in PowerShell: .\Scripts\Test-JavaEnvironment.ps1
# Non dipende da py/python e non modifica l'ambiente del sistema.
$ErrorActionPreference = 'Stop'

Write-Host '=== Java Language Support: diagnostica prerequisiti ==='
$jdk = $env:JAVA_HOME
$server = $env:JDTLS_HOME
if (-not $jdk) {
    Write-Warning 'JAVA_HOME non impostata. Installare JDK 21+ e riavviare Visual Studio.'
} else {
    $java = Join-Path $jdk 'bin\java.exe'
    if (Test-Path $java) {
        Write-Host "JAVA_HOME: $jdk"
        & $java -version
        if ($LASTEXITCODE -ne 0) { Write-Warning "java -version ha restituito $LASTEXITCODE" }
    } else {
        Write-Warning "Impossibile trovare: $java"
    }
}

if (-not $server) {
    Write-Warning 'JDTLS_HOME non impostata.'
} else {
    Write-Host "JDTLS_HOME: $server"
    foreach ($name in @('plugins', 'config_win')) {
        if (Test-Path (Join-Path $server $name)) {
            Write-Host "OK: $name"
        } else {
            Write-Warning "Manca: $name"
        }
    }
    $launchers = Get-ChildItem (Join-Path $server 'plugins') -Filter 'org.eclipse.equinox.launcher_*.jar' -ErrorAction SilentlyContinue
    if ($launchers) {
        Write-Host ("OK: launcher " + ($launchers | Select-Object -First 1).Name)
    } else {
        Write-Warning 'Manca launcher org.eclipse.equinox.launcher_*.jar'
    }
}

Write-Host '\nQuesto script controlla prerequisiti, NON testa la connessione LSP in Visual Studio.'
Write-Host 'Per verificare la connessione apri un .java e usa Strumenti > Java: Stato IntelliSense.'
