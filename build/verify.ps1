# Nghiem thu: build + test ca 4 cau hinh, kiem tra kien truc file PE va SQLite native, chay exe that tren x86 va x64.
param([Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $OutDir | Out-Null

function PeMachine($path) {
  $b = [IO.File]::ReadAllBytes($path)
  $pe = [BitConverter]::ToInt32($b, 60)
  switch ([BitConverter]::ToUInt16($b, $pe + 4)) { 0x14c { 'x86' } 0x8664 { 'x64' } default { 'khac' } }
}

$summary = @()
foreach ($cfg in 'Debug','Release') {
  foreach ($plat in 'x86','x64') {
    $out = & (Join-Path $root 'build\errs.ps1') -Platform $plat -Configuration $cfg
    $build = ($out | Where-Object { $_ -like 'EXIT=*' })
    $errors = @($out | Where-Object { $_ -match ': error ' }).Count
    $trx = Join-Path $OutDir "$cfg-$plat.trx"
    $vt = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\Extensions\TestPlatform\vstest.console.exe"
    & $vt (Join-Path $root "tests\RDM.Tests\bin\$plat\$cfg\RDM.Tests.dll") "/Platform:$plat" '/Framework:.NETFramework,Version=v4.8' '/TestCaseFilter:Category!=RealDesktop' "/Logger:trx;LogFileName=$trx" | Out-Null
    $x = [xml](Get-Content $trx -Encoding UTF8)
    $c = $x.TestRun.ResultSummary.Counters
    $dir = Join-Path $root "src\RDM.App\bin\$plat\$cfg"
    $sqlite = Join-Path $dir "$plat\SQLite.Interop.dll"
    $sqliteArch = if (Test-Path $sqlite) { PeMachine $sqlite } else { 'THIEU' }
    $summary += [pscustomobject]@{ Config = "$cfg|$plat"; Build = $build; Errors = $errors; Tests = $c.total; Passed = $c.passed; Failed = $c.failed; Exe = (PeMachine (Join-Path $dir 'RDM.exe')); SQLiteNative = $sqliteArch }
    if ($c.failed -ne '0') {
      $x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { "FAILED [$cfg|$plat] $($_.testName): $($_.Output.ErrorInfo.Message)" }
    }
    $x.TestRun.Results.UnitTestResult | Where-Object { $_.testName -match 'Timing|FiveThousand' } | ForEach-Object { "[$cfg|$plat] $($_.Output.StdOut)" }
  }
}
$summary | Format-Table -AutoSize | Out-String -Width 160
$failedConfigs = @($summary | Where-Object { $_.Failed -ne '0' -or $_.Errors -ne 0 -or $_.Build -ne 'EXIT=0' })
# Moi kich ban chay trong tien trinh rieng va phai tu bao hoan tat: loi giua chung khong duoc lot qua.
$smokeFailures = 0
foreach ($plat in 'x86','x64') {
  foreach ($s in @(@('ui-smoke','smoke'),@('e2e-smoke','e2e'))) {
    $lines = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build\$($s[0]).ps1") -Platform $plat -Configuration Release -OutDir (Join-Path $OutDir $s[1]) 2>&1 | ForEach-Object { "$_" }
    $lines
    if (-not ($lines -match '^KET QUA ')) { $smokeFailures++; "KICH BAN KHONG HOAN TAT: $($s[0]) $plat" }
  }
}
if ($smokeFailures -gt 0) { "KHONG DAT: $smokeFailures kich ban khong hoan tat"; exit 1 }
if ($failedConfigs.Count -gt 0) { "KHONG DAT: $($failedConfigs.Count) cau hinh co loi"; exit 1 }
"TAT CA CAU HINH DAT"
