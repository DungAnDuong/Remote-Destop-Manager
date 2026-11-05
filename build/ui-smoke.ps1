# Kiem thu khoi chay THAT: chay RDM.exe voi du lieu tam, chup cua so chinh, mo phan quan ly, tao tai khoan, mo tung trang.
# Vi du: .\build\ui-smoke.ps1 -Platform x86 -OutDir C:\temp\shots
param(
  [ValidateSet('x86','x64')][string]$Platform = 'x64',
  [string]$Configuration = 'Debug',
  [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ui-helpers.ps1')
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src\RDM.App\bin\$Platform\$Configuration\RDM.exe"
# Kiem thu khong bao gio duoc dang ky len Relay that dong goi trong ban Release.
$env:RDM_NO_RELAY_DEFAULTS = '1'
$data = Join-Path $env:TEMP ("rdm-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $OutDir, $data | Out-Null
$env:RDM_DATA_DIR = "$data\app"
$env:RDM_CLIENT_DATA_DIR = "$data\client"
$env:RDM_SESSION_BIND = '127.0.0.1'
$script:ShotPrefix = "$Platform-"
$script:ShotDir = $OutDir
$p = Start-Process $exe -PassThru
$results = New-Object System.Collections.ArrayList
try {
  $home_ = WaitWindow $p.Id '^REMOTE DESKTOP MANAGER$'
  Start-Sleep -Milliseconds 1200
  Shot $home_ '00-home'
  [void]$results.Add('OK  Cua so chinh')
  Click (WaitButton $p.Id $ManageLabel)
  $create = WaitButton $p.Id $CreateLabel
  Start-Sleep -Milliseconds 400
  Shot (WaitWindow $p.Id '^REMOTE DESKTOP MANAGER$') '01-setup'
  $edits = EditsOf (DialogWith $p.Id $CreateLabel)
  if ($edits.Count -lt 4) { throw "Man hinh tao tai khoan co $($edits.Count) o nhap" }
  $pw = 'Smoke-Test-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
  SetText $edits[0] 'admin'; SetText $edits[1] 'Smoke Test'; SetText $edits[2] $pw; SetText $edits[3] $pw
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Click $create
  $main = WaitWindow $p.Id 'Device Management$' 60
  [void]$results.Add("Tao tai khoan + dang nhap: $([int]$sw.ElapsedMilliseconds) ms")
  $n = 2
  foreach ($page in 'Dashboard','My Computers','Groups','Recent Connections','Favorites','Active Sessions','File Transfer','Connection History','User Management','Security Center','Settings','About','Dashboard') {
    $b = FindButton $main $page
    if (-not $b) { throw "Khong thay nut Sidebar: $page" }
    Click $b
    Start-Sleep -Milliseconds 700
    if ($p.HasExited) { throw "Ung dung thoat khi mo trang $page" }
    if (-not $p.Responding) { throw "Ung dung TREO khi mo trang $page" }
    Shot $main ('{0:00}-{1}' -f $n, ($page -replace ' ', '_'))
    [void]$results.Add("OK  $page")
    $n++
  }
  Click (FindButton $main $ThemeLabel)
  Start-Sleep -Milliseconds 800
  Shot $main '20-dark'
  if (-not $p.Responding) { throw 'Ung dung TREO sau khi doi theme' }
  [void]$results.Add('OK  Doi theme')
  # Dang xuat quay ve cua so chinh, khong thoat chuong trinh.
  Click (FindButton $main $LogoutLabel)
  Start-Sleep -Milliseconds 1000
  if ($p.HasExited) { throw 'Dang xuat lam thoat chuong trinh' }
  [void](WaitWindow $p.Id '^REMOTE DESKTOP MANAGER$')
  [void]$results.Add('OK  Dang xuat ve cua so chinh')
  $proc = Get-Process -Id $p.Id
  [void]$results.Add("handles=$($proc.HandleCount), RAM=$([int]($proc.WorkingSet64/1MB)) MB")
  $results
  "KET QUA $Platform`: DAT"
}
finally {
  if (-not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(3000) }
  Start-Sleep -Milliseconds 500
  foreach ($v in 'RDM_DATA_DIR','RDM_CLIENT_DATA_DIR','RDM_SESSION_BIND') { Remove-Item "Env:\$v" -ErrorAction SilentlyContinue }
  Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
