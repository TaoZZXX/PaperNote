$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Set-Location $projectRoot

$project = Join-Path $projectRoot 'src\PaperNote.csproj'
$outputDir = Join-Path $projectRoot '.build\bin\Release'
$publishDir = Join-Path $projectRoot 'dist'
$packagesDir = Join-Path $projectRoot '.build\packages'
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio 2022 MSBuild was not found.' }
$vsInstall = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsInstall) { throw 'Install Visual Studio 2022 or Build Tools with the .NET desktop build tools.' }
$msbuild = Join-Path $vsInstall 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $project)) { throw 'PaperNote.csproj was not found.' }

& $msbuild $project /restore /t:Rebuild /m:4 /nr:false /p:Configuration=Release /p:Platform=x64 "/p:RestorePackagesPath=$packagesDir" /v:minimal
if ($LASTEXITCODE -ne 0) { throw "PaperNote build failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outputDir 'PaperNote-PDFium.exe'
if (-not (Test-Path $exe)) { throw "Build did not produce $exe" }
$config = $exe + '.config'
$redistRoot = Join-Path $vsInstall 'VC\Redist\MSVC'
$exeName = 'PaperNote-PDFium.exe'
$runtimeName = 'PaperNoteRuntime-PDFium'
try {
    Copy-Item -LiteralPath $exe -Destination (Join-Path $publishDir $exeName) -Force
    if (Test-Path $config) { Copy-Item -LiteralPath $config -Destination (Join-Path $publishDir ($exeName + '.config')) -Force }
} catch {
    $versionSuffix = '-Updated-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Write-Warning "The current executable is in use; publishing the update as PaperNote-PDFium$versionSuffix.exe."
    $exeName = 'PaperNote-PDFium' + $versionSuffix + '.exe'
    $runtimeName = 'PaperNoteRuntime-PDFium' + $versionSuffix
    Copy-Item -LiteralPath $exe -Destination (Join-Path $publishDir $exeName) -Force
    if (Test-Path $config) { Copy-Item -LiteralPath $config -Destination (Join-Path $publishDir ($exeName + '.config')) -Force }
}

$runtimeDir = Join-Path $publishDir $runtimeName
New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' -File | Copy-Item -Destination $runtimeDir -Force
$pdfiumDll = Join-Path $outputDir 'x64\pdfium.dll'
if (-not (Test-Path $pdfiumDll)) { throw "PDFium runtime was not restored to $pdfiumDll" }
Copy-Item -LiteralPath $pdfiumDll -Destination (Join-Path $runtimeDir 'pdfium.dll') -Force
if (Test-Path $redistRoot) {
    $crtFolder = Get-ChildItem -LiteralPath $redistRoot -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($crtFolder) { Get-ChildItem -LiteralPath $crtFolder -Filter '*.dll' -File | Copy-Item -Destination $runtimeDir -Force }
}
Write-Host "Built dist\$exeName. Keep the adjacent $runtimeName folder with the executable."
