# Kiem tra bo cai ma KHONG cai vao may: bung noi dung tung goi MSI ra thu muc tam (msiexec /a), roi kiem tra va chay thu file da bung.
# Khong ghi vao Program Files hay registry, khong can quyen Administrator, khong tao shortcut hay luat tuong lua.
# Vi vay script nay KHONG kiem chung duoc buoc cai dat that, nang cap, go cai dat, shortcut, luat tuong lua.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$obj = Join-Path $root 'artifacts\installer-obj'
$out = Join-Path $root 'artifacts\installers'
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
$tmp = Join-Path $env:TEMP ('rdm-verify-installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
$failures = New-Object Collections.ArrayList

function PeMachine($path) {
  $b = [IO.File]::ReadAllBytes($path); $pe = [BitConverter]::ToInt32($b, 60)
  switch ([BitConverter]::ToUInt16($b, $pe + 4)) { 0x14c { 'x86' } 0x8664 { 'x64' } default { 'khac' } }
}
function Check($ok, $text) {
  if ($ok) { "  OK    $text" } else { "  LOI   $text"; [void]$failures.Add($text) }
}
# Icon trong file co giong icon RDM khong, thay vi icon mac dinh cua .NET.
function HasRdmIcon($path) {
  $a = [Drawing.Icon]::ExtractAssociatedIcon($path).ToBitmap()
  $i = New-Object Drawing.Icon (Join-Path $root 'assets\RDM.ico'), $a.Width, $a.Height
  $b = $i.ToBitmap(); $same = 0
  for ($y = 0; $y -lt $a.Height; $y++) { for ($x = 0; $x -lt $a.Width; $x++) { if ($a.GetPixel($x, $y) -eq $b.GetPixel($x, $y)) { $same++ } } }
  return $same -gt ($a.Width * $a.Height * 0.9)
}
function Extract($msi, $dir) {
  $log = "$dir.log"
  $p = Start-Process msiexec.exe -ArgumentList '/a', "`"$msi`"", '/qn', "TARGETDIR=`"$dir`"", '/L*', "`"$log`"" -Wait -PassThru
  return $p.ExitCode
}
# Windows Installer bao VersionNT = 603 ca tren Windows 10 tro len. Dieu kien doi lon hon se chan cai dat tren moi may (loi da gap that).
function Check-LaunchConditions([string]$msi) {
  $installer = New-Object -ComObject WindowsInstaller.Installer
  $db = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, [object[]]@([string]$msi, [int]0))
  $view = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, [object[]]@("SELECT Condition, Description FROM LaunchCondition"))
  [void]$view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
  $bad = 0
  while ($true) {
    $rec = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -eq $rec) { break }
    $cond = $rec.GetType().InvokeMember("StringData", "GetProperty", $null, $rec, [object[]]@([int]1))
    $text = $rec.GetType().InvokeMember("StringData", "GetProperty", $null, $rec, [object[]]@([int]2))
    foreach ($m in [regex]::Matches($cond, "VersionNT\s*(>=|>|=)\s*(\d+)")) { if ([int]$m.Groups[2].Value -gt 603) { $bad++ } }
    if ($text -notmatch "^[\x20-\x7E]+$" -or $text -match "\b(can|hoac|moi hon|cai dat)\b") { $bad++ }
  }
  [void]$view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
  Check ($bad -eq 0) "Dieu kien cai dat khong doi VersionNT lon hon 603, thong bao bang tieng Anh"
}
function RunsAndStays($exe, $name) {
  $p = Start-Process $exe -PassThru
  Start-Sleep -Seconds 4
  $alive = -not $p.HasExited
  $hasWindow = $false
  if ($alive) { $p.Refresh(); $hasWindow = $p.MainWindowHandle -ne [IntPtr]::Zero; $p.Kill(); [void]$p.WaitForExit(3000) }
  Check ($alive -and $hasWindow) "$name khoi dong va mo cua so tu file da bung"
}

try {
  foreach ($item in @(@('x86','x64'),@('x64','x86'))) {
    $arch = $item[0]; $other = $item[1]
    "=== RDM_$arch ==="
    $dir = Join-Path $tmp "app-$arch"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Check-LaunchConditions (Join-Path $obj "RDM_$arch.msi")
    $code = Extract (Join-Path $obj "RDM_$arch.msi") $dir
    Check ($code -eq 0) "Bung MSI (ma thoat $code)"
    $exe = Get-ChildItem $dir -Recurse -Filter 'RDM.exe' | Select-Object -First 1
    Check ($null -ne $exe) 'Co file exe chinh'
    if (-not $exe) { continue }
    $app = $exe.DirectoryName
    Check ((PeMachine $exe.FullName) -eq $arch) "Exe dung kien truc $arch"
    Check (HasRdmIcon $exe.FullName) 'Exe mang icon RDM'
    Check (@(Get-ChildItem $app -Recurse -Filter *.pdb).Count -eq 0) 'Khong kem file .pdb'
    Check (Test-Path "$app\$arch\SQLite.Interop.dll") "Co SQLite native $arch"
    Check ((PeMachine "$app\$arch\SQLite.Interop.dll") -eq $arch) 'SQLite native dung kien truc'
    Check (-not (Test-Path "$app\$other")) "Khong kem SQLite native $other"
    foreach ($dll in 'RDM.UI.dll','RDM.Controller.dll','RDM.Client.dll','RDM.Data.dll','RDM.Shared.dll','BouncyCastle.Cryptography.dll','NLog.dll','RDM.exe.config') { Check (Test-Path "$app\$dll") "Co $dll" }
    # Moi file trong ban build phai co mat, tru pdb, xml va thu muc kien truc kia.
    $bin = Join-Path $root "src\RDM.App\bin\$arch\Release"
    $missing = New-Object Collections.ArrayList
    foreach ($f in (Get-ChildItem $bin -Recurse -File)) {
      if ($f.Extension -in '.pdb','.xml' -or $f.FullName -like "$bin\$other\*") { continue }
      if (-not (Test-Path (Join-Path $app $f.FullName.Substring($bin.Length + 1)))) { [void]$missing.Add($f.Name) }
    }
    Check ($missing.Count -eq 0) "Du moi file cua ban build (thieu $($missing.Count): $(($missing | Select-Object -First 3) -join ', '))"
    $env:RDM_DATA_DIR = Join-Path $tmp "data-$arch"
    $env:RDM_CLIENT_DATA_DIR = Join-Path $tmp "cdata-$arch"
    $env:RDM_SESSION_BIND = '127.0.0.1'
    RunsAndStays $exe.FullName "RDM $arch"
  }

  "=== RDM_Server ==="
  $dir = Join-Path $tmp 'server'
  New-Item -ItemType Directory -Force $dir | Out-Null
  Check-LaunchConditions (Join-Path $obj 'RDM_Server.msi')
  $code = Extract (Join-Path $obj 'RDM_Server.msi') $dir
  Check ($code -eq 0) "Bung MSI (ma thoat $code)"
  $exe = Get-ChildItem $dir -Recurse -Filter 'RDM.Server.exe' | Select-Object -First 1
  Check ($null -ne $exe) 'Co file exe chinh'
  if ($exe) {
    Check (HasRdmIcon $exe.FullName) 'Exe mang icon RDM'
    Check (Test-Path (Join-Path $exe.DirectoryName 'DEPLOYMENT-GUIDE.md')) 'Co huong dan trien khai'
    # Ban tu chua runtime: phai chay duoc khi KHONG tro toi dotnet cua may.
    Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
    $env:RDM_CERT_PASSWORD = [Guid]::NewGuid().ToString('N')
    $pfx = Join-Path $tmp 'relay.pfx'
    $pin = (& $exe.FullName --generate-cert $pfx localhost 7).Trim()
    Check ($pin.Length -eq 64) 'Sinh chung thu bang file da bung'
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $http = $l.LocalEndpoint.Port; $l.Stop()
    # appsettings.json co Urls rieng, chi bien URLS hoac tham so moi ghi de duoc.
    $env:URLS = "http://127.0.0.1:$http"; $env:Relay__Port = "$port"; $env:Relay__BindAddress = '127.0.0.1'
    $env:Relay__CertificatePath = $pfx; $env:Relay__DatabasePath = Join-Path $tmp 'relay.db'
    $s = Start-Process $exe.FullName -WorkingDirectory $exe.DirectoryName -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 4
    $health = $null
    try { $health = Invoke-RestMethod "http://127.0.0.1:$http/health" -TimeoutSec 5 } catch { }
    Check ($health.registry -eq 'active') 'Server chay va tra loi /health'
    if (-not $s.HasExited) { $s.Kill(); [void]$s.WaitForExit(3000) }
  }

  "=== Bo cai .exe ==="
  foreach ($name in 'RDM_Setup_x64','RDM_Setup_x86','RDM_Server_Setup') {
    $f = Join-Path $out "$name.exe"
    Check (Test-Path $f) "$name.exe ton tai"
    if (-not (Test-Path $f)) { continue }
    Check (HasRdmIcon $f) "$name.exe mang icon RDM"
    $sig = Get-AuthenticodeSignature $f
    "  CHU Y $name.exe chu ky so: $($sig.Status)"
    $x = Join-Path $tmp "burn-$name"
    New-Item -ItemType Directory -Force $x, (Join-Path $tmp "burnobj-$name") | Out-Null
    & $wix burn extract $f -o $x -intermediateFolder (Join-Path $tmp "burnobj-$name") | Out-Null
    # Goi MSI trong bundle mang ten theo ma payload, nen nhan dien bang chu ky file OLE cua MSI.
    $hasMsi = $false
    foreach ($candidate in (Get-ChildItem $x -Recurse -File)) {
      if ($candidate.Length -le 1MB) { continue }
      $head = [IO.File]::ReadAllBytes($candidate.FullName)[0..3]
      if ((($head | ForEach-Object { $_.ToString('x2') }) -join '') -eq 'd0cf11e0') { $hasMsi = $true }
    }
    Check $hasMsi "$name.exe chua goi MSI"
  }
  foreach ($line in (Get-Content (Join-Path $out 'SHA256SUMS.txt'))) {
    $parts = $line -split '\s+'
    Check ((Get-FileHash (Join-Path $out $parts[1]) -Algorithm SHA256).Hash.ToLowerInvariant() -eq $parts[0]) "SHA-256 khop: $($parts[1])"
  }
}
finally {
  foreach ($v in 'RDM_DATA_DIR','RDM_CLIENT_DATA_DIR','RDM_SESSION_BIND','RDM_CERT_PASSWORD','URLS','Relay__Port','Relay__BindAddress','Relay__CertificatePath','Relay__DatabasePath') { Remove-Item "Env:\$v" -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 800
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures.Count -gt 0) { "KHONG DAT: $($failures.Count) muc loi"; exit 1 }
"TAT CA DAT (chua gom cai dat that, xem dau file)"
