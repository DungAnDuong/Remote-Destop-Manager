# Build mot cau hinh va chi in loi/canh bao duy nhat (dung khi phat trien).
param([string]$Platform = 'x64', [string]$Configuration = 'Debug', [switch]$Warnings)
$env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
$env:DOTNET_ROOT = "$env:ProgramFiles\dotnet"
$root = Split-Path $PSScriptRoot -Parent
$m = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
$filter = if ($Warnings) { '-clp:NoSummary' } else { '-clp:ErrorsOnly' }
$out = & $m (Join-Path $root 'RDM.sln') -restore -m -nologo -v:m "-p:Configuration=$Configuration" "-p:Platform=$Platform" $filter
$code = $LASTEXITCODE
$out | Where-Object { $_ -match 'error|warning' } | ForEach-Object { ($_ -replace [regex]::Escape("$root\"), '') -replace '\s\[[^\]]*\]$', '' } | Select-Object -Unique | Select-Object -First 60
"EXIT=$code"
