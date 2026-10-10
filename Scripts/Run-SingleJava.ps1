param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$FilePath
)

$ErrorActionPreference = 'Stop'
$fullPath = (Resolve-Path -LiteralPath $FilePath).Path
if ([IO.Path]::GetExtension($fullPath) -ne '.java') { throw 'Specificare un file .java' }

$sourceDir = Split-Path -Parent $fullPath
$className = [IO.Path]::GetFileNameWithoutExtension($fullPath)
$outputDir = Join-Path $sourceDir '.java-build'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$source = Get-Content -LiteralPath $fullPath -Raw
$packageName = ''
$matchPackage = [regex]::Match($source, '(?m)^\s*package\s+([\w.]+)\s*;')
if ($matchPackage.Success) { $packageName = $matchPackage.Groups[1].Value }
$mainClass = if ($packageName) { "$packageName.$className" } else { $className }

Write-Host "Compilo: $fullPath"
& javac -encoding UTF-8 -d $outputDir $fullPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Eseguo: $mainClass"
& java -cp $outputDir $mainClass
exit $LASTEXITCODE
