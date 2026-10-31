$ErrorActionPreference='Stop'
$desktop=$PSScriptRoot
$root=Split-Path (Split-Path $desktop -Parent) -Parent
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output=Join-Path $root 'ArgonmineDesktop.exe'
$manifest=Join-Path $desktop 'app.manifest'
$source=Join-Path $desktop 'ArgonmineDesktop.cs'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output" "/win32manifest:$manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll $source
if($LASTEXITCODE -ne 0){throw 'GUI build failed'}
$detectSource=Join-Path $desktop 'GpuDetect.cs'
$detectOutput=Join-Path $root 'bin/argon-gpu-detect.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$detectOutput" /r:System.Web.Extensions.dll $detectSource
if($LASTEXITCODE -ne 0){throw 'GPU detector build failed'}
