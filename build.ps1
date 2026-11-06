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

$commonArgs = @(
    $project,
    '/m:4',
    '/nr:false',
    '/p:Configuration=Release',
    '/p:Platform=x64',
    "/p:RestorePackagesPath=$packagesDir",
    '/v:minimal'
)

# Build once to restore NuGet dependencies and locate PDFium plus the matching CRT files.
& $msbuild $project /restore /t:Rebuild /m:4 /nr:false /p:Configuration=Release /p:Platform=x64 "/p:RestorePackagesPath=$packagesDir" /v:minimal
if ($LASTEXITCODE -ne 0) { throw "PaperNote initial build failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outputDir 'PaperNote-PDFium.exe'
if (-not (Test-Path $exe)) { throw "Build did not produce $exe" }

$stageName = 'native-runtime-bundle-' + [Guid]::NewGuid().ToString('N')
$runtimeStage = Join-Path (Join-Path $projectRoot '.build') $stageName
$nativeRuntimeResources = Join-Path $runtimeStage 'native-runtime.resources'
New-Item -ItemType Directory -Force -Path $runtimeStage | Out-Null

Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' -File | Copy-Item -Destination $runtimeStage -Force
$pdfiumDll = Join-Path $outputDir 'x64\pdfium.dll'
if (-not (Test-Path $pdfiumDll)) { throw "PDFium runtime was not restored to $pdfiumDll" }
Copy-Item -LiteralPath $pdfiumDll -Destination (Join-Path $runtimeStage 'pdfium.dll') -Force

$redistRoot = Join-Path $vsInstall 'VC\Redist\MSVC'
if (Test-Path $redistRoot) {
    $crtFolder = Get-ChildItem -LiteralPath $redistRoot -Directory | Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($crtFolder) {
        Get-ChildItem -LiteralPath $crtFolder -Filter '*.dll' -File | Copy-Item -Destination $runtimeStage -Force
    }
}

$runtimeFiles = Get-ChildItem -LiteralPath $runtimeStage -Filter '*.dll' -File | Sort-Object Name
if ($runtimeFiles.Count -eq 0) { throw 'No native runtime DLLs were found to embed.' }
$resourceWriter = New-Object System.Resources.ResourceWriter($nativeRuntimeResources)
try {
    foreach ($runtimeFile in $runtimeFiles) {
        $resourceWriter.AddResource($runtimeFile.Name, [IO.File]::ReadAllBytes($runtimeFile.FullName))
    }
}
finally {
    $resourceWriter.Close()
}

# Rebuild with the generated resource so every native dependency is inside the EXE.
& $msbuild @commonArgs /t:Rebuild "/p:NativeRuntimeResources=$nativeRuntimeResources"
if ($LASTEXITCODE -ne 0) { throw "PaperNote single-file build failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "Final build did not produce $exe" }

$exeName = 'PaperNote-PDFium.exe'
try {
    Copy-Item -LiteralPath $exe -Destination (Join-Path $publishDir $exeName) -Force
    $config = $exe + '.config'
    if (Test-Path $config) {
        throw 'A .config file was generated. Single-file deployment needs those settings moved into the executable before release.'
    }
}
catch {
    if ($_.Exception.Message -like 'A .config file was generated.*') { throw }
    $versionSuffix = '-Updated-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Write-Warning "The current executable is in use; publishing the update as PaperNote-PDFium$versionSuffix.exe."
    $exeName = 'PaperNote-PDFium' + $versionSuffix + '.exe'
    Copy-Item -LiteralPath $exe -Destination (Join-Path $publishDir $exeName) -Force
}

$resolvedBuildRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.build'))
$resolvedRuntimeStage = [IO.Path]::GetFullPath($runtimeStage)
$buildPrefix = $resolvedBuildRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedRuntimeStage.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove generated runtime staging outside .build: $resolvedRuntimeStage"
}
Remove-Item -LiteralPath $resolvedRuntimeStage -Recurse -Force
$legacyRuntimeDir = Join-Path $publishDir 'PaperNoteRuntime-PDFium'
if (Test-Path $legacyRuntimeDir) {
    $resolvedPublishDir = [IO.Path]::GetFullPath($publishDir)
    $resolvedLegacyRuntimeDir = [IO.Path]::GetFullPath($legacyRuntimeDir)
    $publishPrefix = $resolvedPublishDir.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedLegacyRuntimeDir.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove legacy runtime outside dist: $resolvedLegacyRuntimeDir"
    }
    Remove-Item -LiteralPath $resolvedLegacyRuntimeDir -Recurse -Force
}
Write-Host "Built dist\$exeName as a single EXE. Native PDFium runtime is extracted to a per-user cache at startup."
