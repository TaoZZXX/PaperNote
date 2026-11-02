$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { throw "C# compiler not found: $compiler" }
& $compiler /nologo /target:winexe /out:PaperNote-PageScaledAnnotations.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll PaperNote.cs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
Write-Host 'Built PaperNote-PageScaledAnnotations.exe'
