# Build va chay kiem thu cho x86/x64. Dung dotnet x64 va MSBuild cua VS 2022 bang duong dan tuong minh,
# vi tren mot so may dotnet x86 dung truoc trong PATH va khong chay duoc SDK.
# Vi du: .\build\build.ps1 -Configuration Release -Test
param(
  [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
  [ValidateSet('x86','x64','all')][string]$Platform = 'all',
  [switch]$Test
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnetDir = Join-Path $env:ProgramFiles 'dotnet'
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_ROOT = $dotnetDir
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
if (-not $vs) { throw 'Khong tim thay Visual Studio 2022 co MSBuild.' }
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
$vstest = Join-Path $vs 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
$platforms = if ($Platform -eq 'all') { 'x86','x64' } else { ,$Platform }
$failed = $false
foreach ($p in $platforms) {
  Write-Host "=== Build $Configuration|$p ===" -ForegroundColor Cyan
  & $msbuild (Join-Path $root 'RDM.sln') -restore -m -nologo -v:m "-p:Configuration=$Configuration" "-p:Platform=$p"
  if ($LASTEXITCODE -ne 0) { throw "Build that bai: $Configuration|$p" }
  if ($Test) {
    Write-Host "=== Test $Configuration|$p ===" -ForegroundColor Cyan
    $dll = Join-Path $root "tests\RDM.Tests\bin\$p\$Configuration\RDM.Tests.dll"
    & $vstest $dll "/Platform:$p" '/Framework:.NETFramework,Version=v4.8' '/TestCaseFilter:Category!=RealDesktop' "/Logger:trx;LogFileName=rdm-$Configuration-$p.trx" "/ResultsDirectory:$(Join-Path $root 'TestResults')"
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "CO KIEM THU THAT BAI: $p" -ForegroundColor Red }
  }
}
if ($failed) { exit 1 }
