# Java CPS F2: rigenera gli ZIP dai sorgenti .vstemplate e .java (Windows PowerShell 5.1+).
param([string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$templateRoot = Join-Path $Repo 'JavaLanguageSupport\ProjectSystem\Templates'
# Alternativa: se il file e' nel repository, ricava direttamente Templates.
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Sources')) { $templateRoot = $PSScriptRoot }
$src = Join-Path $templateRoot 'Sources'
$dest = Join-Path $templateRoot 'ItemTemplates\Java\1033'
if (-not (Test-Path -LiteralPath $src)) { throw "Sorgenti non trovati: $src" }
[IO.Directory]::CreateDirectory($dest) | Out-Null
Get-ChildItem -LiteralPath $src -Directory | ForEach-Object {
    $vstemplate = @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.vstemplate' -File)
    if ($vstemplate.Count -ne 1) { throw "Template non valido: $($_.FullName)" }
    [void][xml]([IO.File]::ReadAllText($vstemplate[0].FullName))
    $zipPath = Join-Path $dest ($_.Name + '.zip')
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($_.FullName, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "GENERATO $zipPath" -ForegroundColor Green
}
