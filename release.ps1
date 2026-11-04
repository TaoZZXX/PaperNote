$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$licenseFile = Join-Path $projectRoot 'LICENSE'
$thirdPartyNotices = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md'
$licenseDirectory = Join-Path $projectRoot 'LICENSES'
$distDirectory = Join-Path $projectRoot 'dist'
$releaseDirectory = Join-Path $projectRoot 'release'
$stagingDirectory = Join-Path $releaseDirectory 'PaperNote'
$archivePath = Join-Path $releaseDirectory 'PaperNote.zip'

if (-not (Test-Path -LiteralPath $licenseFile)) {
    throw 'The project license is not selected yet. Add a root LICENSE file before creating the release.'
}

& (Join-Path $projectRoot 'build.ps1')

$publishedExe = Get-ChildItem -LiteralPath $distDirectory -Filter 'PaperNote-PDFium*.exe' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $publishedExe) {
    throw 'The build did not produce a PaperNote executable in dist.'
}

$runtimeSuffix = ''
if ($publishedExe.BaseName -match '^PaperNote-PDFium(?<Suffix>-Updated-[0-9-]+)$') {
    $runtimeSuffix = $Matches.Suffix
}

$runtimeSource = Join-Path $distDirectory ("PaperNoteRuntime-PDFium$runtimeSuffix")
if (-not (Test-Path -LiteralPath (Join-Path $runtimeSource 'pdfium.dll'))) {
    throw "PDFium runtime is missing beside $($publishedExe.Name)."
}

New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
if (Test-Path -LiteralPath $stagingDirectory) {
    $resolvedReleaseRoot = (Resolve-Path -LiteralPath $releaseDirectory).Path
    $resolvedStaging = (Resolve-Path -LiteralPath $stagingDirectory).Path
    if (-not $resolvedStaging.StartsWith($resolvedReleaseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a staging directory outside release/: $resolvedStaging"
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null
Copy-Item -LiteralPath $publishedExe.FullName -Destination (Join-Path $stagingDirectory 'PaperNote.exe')
Copy-Item -LiteralPath $runtimeSource -Destination (Join-Path $stagingDirectory 'PaperNoteRuntime-PDFium') -Recurse
Copy-Item -LiteralPath $licenseFile -Destination $stagingDirectory
Copy-Item -LiteralPath $thirdPartyNotices -Destination $stagingDirectory
Copy-Item -LiteralPath $licenseDirectory -Destination $stagingDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'README-RELEASE.md') -Destination (Join-Path $stagingDirectory 'README.md')

$publishedConfig = $publishedExe.FullName + '.config'
if (Test-Path -LiteralPath $publishedConfig) {
    Copy-Item -LiteralPath $publishedConfig -Destination (Join-Path $stagingDirectory 'PaperNote.exe.config')
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Compress-Archive -Path $stagingDirectory -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Created release: release\PaperNote.zip"
