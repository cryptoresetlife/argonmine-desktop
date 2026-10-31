$ErrorActionPreference='Stop'
$root=$PSScriptRoot
New-Item -ItemType Directory -Force (Join-Path $root 'bin') | Out-Null
$go=(Get-Command go -ErrorAction Stop).Source
Push-Location (Join-Path $root 'source')
try {
    & $go test -mod=readonly ./controller -run 'TestDuty|TestMainnet'
    if($LASTEXITCODE -ne 0){throw 'CPU tests failed'}
    & $go build -mod=readonly -trimpath -o ../bin/argonmine-gpu.exe ./controller
    if($LASTEXITCODE -ne 0){throw 'Miner build failed'}
    & $go build -mod=readonly -trimpath -o ../bin/argonmined.exe ./cmd/argonmined
    if($LASTEXITCODE -ne 0){throw 'Node build failed'}
    & cmd.exe /d /c build-worker.cmd
    if($LASTEXITCODE -ne 0){throw 'CUDA build failed'}
} finally { Pop-Location }
& (Join-Path $root 'source/desktop/BUILD-GUI.ps1')
$manifest=@(Get-ChildItem (Join-Path $root 'bin') -Filter '*.exe' | ForEach-Object {
    [pscustomobject]@{file="bin/$($_.Name)";sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'binaries.json') -Encoding utf8
Write-Output 'Build complete. Launch ArgonmineDesktop.exe; mining starts only after clicking Start.'
