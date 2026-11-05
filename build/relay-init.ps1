# Sinh mot lan bo khoa cua Relay trong deploy\relay (khong dua vao git):
#   relay.pfx            chung thu + khoa rieng, chi may chu moi can
#   relay-password.dat   mat khau PFX, boc DPAPI theo tai khoan Windows dang chay
#   client-defaults.txt  dong goi vao ban Release cua RDM
# Khong can quyen Administrator. Chay lai se bi tu choi: doi khoa thi moi may da cai phai cai lai ban moi.
param(
  [string]$RelayHost = 'erp247.ddns.net',
  [int]$Port = 31408,
  [int]$Days = 3650
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root 'deploy\relay'
$pfx = Join-Path $dir 'relay.pfx'
$server = Join-Path $root 'artifacts\publish\server\RDM.Server.exe'
if (Test-Path $pfx) { throw "$pfx already exists. Replacing the key would disconnect every installed computer." }
if (-not (Test-Path $server)) { throw "$server not found. Run build\build-installer.ps1 once first." }
Add-Type -AssemblyName System.Security
New-Item -ItemType Directory -Force $dir | Out-Null

function New-Secret {
  $bytes = New-Object byte[] 32
  $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
  $rng.GetBytes($bytes); $rng.Dispose()
  return [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
}

$password = New-Secret
$enrollment = New-Secret
$env:RDM_CERT_PASSWORD = $password
try {
  $pin = (& $server --generate-cert $pfx $RelayHost $Days | Select-Object -Last 1).Trim()
  if ($LASTEXITCODE -ne 0 -or $pin -notmatch '^[0-9a-f]{64}$') { throw "Could not generate the certificate: $pin" }
} finally { Remove-Item Env:\RDM_CERT_PASSWORD -ErrorAction SilentlyContinue }

$protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($password), $null, 'CurrentUser')
[IO.File]::WriteAllBytes((Join-Path $dir 'relay-password.dat'), $protected)
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $dir 'client-defaults.txt'), "host=$RelayHost`nport=$Port`npin=$pin`nenrollment=$enrollment`n", $utf8)

"Relay keys created in $dir"
"Host:       $RelayHost"
"Port:       $Port"
"Pinned key: $pin"
"Valid for:  $Days days"
