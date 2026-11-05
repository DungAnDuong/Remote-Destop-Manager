# Build Release va tao 3 bo cai trong artifacts\installers:
#   RDM_Setup_x64.exe, RDM_Setup_x86.exe (chuong trinh RDM cho moi may), RDM_Server_Setup.exe (Relay Server)
# Yeu cau: Visual Studio 2022, .NET SDK x64, WiX 5 (dotnet tool install --global wix --version 5.0.2).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$dotnetDir = Join-Path $env:ProgramFiles 'dotnet'
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_ROOT = $dotnetDir
$dotnet = Join-Path $dotnetDir 'dotnet.exe'
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
$out = Join-Path $root 'artifacts\installers'
$obj = Join-Path $root 'artifacts\installer-obj'
$icon = Join-Path $root 'assets\RDM.ico'
$logo = Join-Path $root 'assets\RDM-logo-64.png'

function Run($exe, [string[]]$arguments) {
  $output = & $exe @arguments 2>&1 | ForEach-Object { "$_" }
  $code = $LASTEXITCODE
  $output
  if ($code -ne 0) { throw "That bai ($code): $exe $($arguments -join ' ')" }
  # WIX8601 nghia la bo qua ca khoi file: bo cai se thieu thu vien du build van bao thanh cong.
  if ($output -match 'WIX8601') { throw "WiX bao thieu thu muc khi lay file: $exe $($arguments -join ' ')" }
}

# Thieu file nay thi bo cai se khong co Relay mac dinh ma build van bao thanh cong.
if (-not (Test-Path (Join-Path $root 'deploy\relay\client-defaults.txt'))) { throw 'Thieu deploy\relay\client-defaults.txt: hay chay build\relay-init.ps1 truoc.' }

"=== Chuan bi WiX extension ==="
$have = & $wix extension list -g | Out-String
foreach ($e in 'WixToolset.BootstrapperApplications.wixext','WixToolset.Util.wixext','WixToolset.Firewall.wixext','WixToolset.Netfx.wixext') {
  if ($have -notmatch [regex]::Escape($e)) { Run $wix @('extension','add','-g',"$e/5.0.2") }
}

Remove-Item $out, $obj -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, $obj | Out-Null

"=== Build Release x86, x64 ==="
foreach ($p in 'x86','x64') {
  # Rebuild: tranh dong goi file cu con sot lai.
  Run $msbuild @((Join-Path $root 'RDM.sln'),'-t:Rebuild','-restore','-m','-nologo','-v:q','-clp:ErrorsOnly','-p:Configuration=Release',"-p:Platform=$p")
}

"=== Do lo hong thu vien ==="
foreach ($p in 'RDM.Server','RDM.Shared','RDM.UI','RDM.Data','RDM.Controller','RDM.Client','RDM.App') {
  $report = & $dotnet list (Join-Path $root "src\$p\$p.csproj") package --vulnerable --include-transitive | Out-String
  if ($report -match 'has the following vulnerable') { throw "Thu vien co lo hong, khong dong goi: $p`n$report" }
}
"Khong phat hien goi co lo hong da cong bo (may quet NuGet khong thay SQLite nhung trong System.Data.SQLite)."

"=== Publish Relay Server (tu chua runtime) ==="
$serverPublish = Join-Path $root 'artifacts\publish\server'
Remove-Item $serverPublish -Recurse -Force -ErrorAction SilentlyContinue
Run $dotnet @('publish',(Join-Path $root 'src\RDM.Server\RDM.Server.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-p:Platform=x64','-o',$serverPublish,'-v','q','--nologo')

# Moi san pham mot UpgradeCode co dinh: khong duoc doi, neu khong nang cap se khong thay the ban cu.
$items = @(
  @{ Arch='x64'; Other='x86'; Msi='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E1006}'; Bundle='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E2006}'; OldA='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E2002}'; OldB='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E2004}'; Out='RDM_Setup_x64.exe' },
  @{ Arch='x86'; Other='x64'; Msi='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E1007}'; Bundle='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E2007}'; OldA='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E2001}'; OldB='{0D1B7A10-3C55-4E0A-9E31-6A5B0C2E2003}'; Out='RDM_Setup_x86.exe' }
)
foreach ($i in $items) {
  "=== $($i.Out) ==="
  $bin = Join-Path $root "src\RDM.App\bin\$($i.Arch)\Release"
  $msi = Join-Path $obj "RDM_$($i.Arch).msi"
  Run $wix @('build',(Join-Path $root 'installer\Package.wxs'),'-arch',$i.Arch,'-o',$msi,
    '-ext','WixToolset.Firewall.wixext','-ext','WixToolset.Netfx.wixext',
    '-d',"Version=$version",'-d',"UpgradeCode=$($i.Msi)",'-d',"BinDir=$bin",'-d',"OtherArch=$($i.Other)",'-d',"IconFile=$icon")
  Run $wix @('build',(Join-Path $root 'installer\Bundle.wxs'),'-arch','x86','-o',(Join-Path $out $i.Out),
    '-ext','WixToolset.BootstrapperApplications.wixext','-ext','WixToolset.Util.wixext',
    '-d',"DisplayName=Remote Desktop Manager ($($i.Arch))",'-d',"Version=$version",'-d',"BundleCode=$($i.Bundle)",'-d',"IconFile=$icon",'-d',"LogoFile=$logo",'-d',"MsiFile=$msi",'-d','NeedsNetFx=yes','-d',"Arch=$($i.Arch)",'-d',"OldBundleA=$($i.OldA)",'-d',"OldBundleB=$($i.OldB)")
}

"=== RDM_Server_Setup.exe ==="
$msi = Join-Path $obj 'RDM_Server.msi'
Run $wix @('build',(Join-Path $root 'installer\Server.wxs'),'-arch','x64','-o',$msi,
  '-d',"Version=$version",'-d','UpgradeCode={0D1B7A10-3C55-4E0A-9E31-6A5B0C2E1005}','-d',"BinDir=$serverPublish",'-d',"IconFile=$icon",'-d',"GuideFile=$(Join-Path $root 'docs\RELAY-DEPLOYMENT.md')")
Run $wix @('build',(Join-Path $root 'installer\Bundle.wxs'),'-arch','x86','-o',(Join-Path $out 'RDM_Server_Setup.exe'),
  '-ext','WixToolset.BootstrapperApplications.wixext','-ext','WixToolset.Util.wixext',
  '-d','DisplayName=RDM Relay Server','-d',"Version=$version",'-d','BundleCode={0D1B7A10-3C55-4E0A-9E31-6A5B0C2E2005}','-d',"IconFile=$icon",'-d',"LogoFile=$logo",'-d',"MsiFile=$msi",'-d','NeedsNetFx=no','-d','Arch=x64')

# Chi giu bo cai .exe; file .wixpdb chi dung khi go loi.
Get-ChildItem $out -Filter *.wixpdb | Remove-Item -Force
$lines = Get-ChildItem $out -Filter *.exe | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), $lines)
"=== Xong, phien ban $version ==="
Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize | Out-String
