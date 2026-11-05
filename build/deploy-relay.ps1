# Cai hoac cap nhat Relay Server tren may nay thanh dich vu Windows. Chay bang PowerShell quyen Administrator,
# cung tai khoan da chay relay-init.ps1 (mat khau PFX boc DPAPI theo tai khoan do):
#   .\build\deploy-relay.ps1
# Khong dung toi Caddy hay dich vu khac. Dich vu chay bang LocalService, khong phai SYSTEM,
# vi tien trinh nay nhan ket noi tu Internet.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'deploy\relay'
$setup = Join-Path $root 'artifacts\installers\RDM_Server_Setup.exe'
$exe = 'C:\Program Files\RDM\Relay Server\RDM.Server.exe'
$dataDir = Join-Path $env:ProgramData 'RDM\Relay'
$pfx = Join-Path $dataDir 'relay.pfx'
$serviceName = 'RDMRelay'
$ruleName = 'RDM Relay'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated PowerShell (Run as administrator).' }
foreach ($f in (Join-Path $src 'relay.pfx'), (Join-Path $src 'relay-password.dat'), (Join-Path $src 'client-defaults.txt'), $setup) {
  if (-not (Test-Path $f)) { throw "Missing $f" }
}

$settings = @{}
foreach ($line in Get-Content (Join-Path $src 'client-defaults.txt')) {
  $parts = $line -split '=', 2
  if ($parts.Length -eq 2) { $settings[$parts[0]] = $parts[1] }
}
$port = [int]$settings['port']

Add-Type -AssemblyName System.Security
$password = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes((Join-Path $src 'relay-password.dat')), $null, 'CurrentUser'))

$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
  "Stopping the existing Relay service..."
  Stop-Service $serviceName -Force
  $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
$busy = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
if ($busy) { throw "Port $port is already in use by process $($busy[0].OwningProcess)." }

"Installing Relay Server..."
$p = Start-Process $setup -ArgumentList '/quiet', '/norestart' -Wait -PassThru
if ($p.ExitCode -notin 0, 3010) { throw "RDM_Server_Setup.exe failed with exit code $($p.ExitCode)." }
if (-not (Test-Path $exe)) { throw "$exe not found after installing." }

"Copying the certificate..."
New-Item -ItemType Directory -Force $dataDir | Out-Null
Copy-Item (Join-Path $src 'relay.pfx') $pfx -Force
# Khoa rieng: chi Administrators, SYSTEM va tai khoan dich vu. Mat khau nam trong registry dich vu nen file nay phai duoc khoa rieng.
$acl = New-Object Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($who in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM') {
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, 'FullControl', 'Allow')))
}
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('NT AUTHORITY\LOCAL SERVICE', 'Read', 'Allow')))
Set-Acl $pfx $acl
# LocalService can ghi registry thiet bi trong thu muc nay.
$dirAcl = Get-Acl $dataDir
$dirAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('NT AUTHORITY\LOCAL SERVICE', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl $dataDir $dirAcl
# Thua ke tu thu muc khong duoc mo rong quyen tren file khoa rieng.
Set-Acl $pfx $acl

"Hashing the enrollment code..."
# Tinh truc tiep: chuyen qua duong ong toi chuong trinh, PowerShell 5.1 co the chen BOM lam sai ma bam.
$sha = [Security.Cryptography.SHA256]::Create()
$hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($settings['enrollment'])))).Replace('-', '').ToLowerInvariant()
$sha.Dispose()

"Configuring the service..."
if (-not $existing) {
  & sc.exe create $serviceName binPath= "`"$exe`"" start= auto obj= 'NT AUTHORITY\LocalService' DisplayName= 'RDM Relay Server' | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
}
# PowerShell 5.1 lam mat dau ngoac kep khi truyen cho sc.exe; duong dan co dau cach khong ngoac la lo hong "unquoted service path".
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath -Value "`"$exe`""
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
$environment = @(
  "RDM_CERT_PASSWORD=$password",
  "Relay__CertificatePath=$pfx",
  "Relay__Port=$port",
  "Relay__EnrollmentTokenSha256=$hash"
)
New-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null

"Opening the firewall for TCP $port only..."
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $port -Program $exe -Action Allow | Out-Null

"Starting the service..."
Start-Service $serviceName

$health = $null
foreach ($i in 1..15) {
  Start-Sleep -Seconds 1
  try { $health = Invoke-RestMethod -UseBasicParsing -TimeoutSec 3 http://127.0.0.1:7080/health; break } catch { }
}
if (-not $health) {
  "RESULT: FAILED. The service did not answer on /health. Windows event log:"
  Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-3) } -MaxEvents 8 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'RDM|Relay' } | ForEach-Object { $_.Message }
  exit 1
}
if (-not (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)) {
  "RESULT: FAILED. /health answers but nothing listens on port $port. Check the certificate access and the event log."
  exit 1
}
"RESULT: OK. Relay service is running and listening on TCP $port."
""
"Next step: on your router, forward external TCP $port to this computer."
"Use the same internal address as your existing port 443 forward."
