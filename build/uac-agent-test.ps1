# Buoc 1-2 cua tinh nang UAC/lock screen: dong goi agent chup secure desktop + dich vu SYSTEM de test.
# Tao artifacts\uac-agent\ gom RDM.DesktopAgent.exe va RDM.HostService.exe (Release x64) + huong dan.
# KHONG tu cai dat; viec cai/go dich vu phai chay quyen Administrator tren may bi dieu khien.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
$out = Join-Path $root 'artifacts\uac-agent'

function Build($proj) {
  & $msbuild $proj '-t:Restore,Build' '-p:Configuration=Release' '-p:Platform=x64' '-v:q' '-nologo' '-clp:ErrorsOnly'
  if ($LASTEXITCODE -ne 0) { throw "Build that bai: $proj" }
}

"=== Build agent + service (Release x64) ==="
Build (Join-Path $root 'src\RDM.DesktopAgent\RDM.DesktopAgent.csproj')
Build (Join-Path $root 'src\RDM.HostService\RDM.HostService.csproj')

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $root 'src\RDM.DesktopAgent\bin\x64\Release\RDM.DesktopAgent.exe') $out
Copy-Item (Join-Path $root 'src\RDM.HostService\bin\x64\Release\RDM.HostService.exe') $out

$readme = @'
RDM - Thu nghiem ho tro UAC / man hinh khoa (Buoc 1-2)

Muc tieu: chung minh agent chay quyen SYSTEM chup duoc secure desktop (UAC, man hinh khoa).
Lam tren MAY BI DIEU KHIEN, mo PowerShell quyen Administrator trong thu muc nay.

1) Cai dich vu (tu khoi dong cung Windows, chay quyen SYSTEM):
     .\RDM.HostService.exe --install
     sc start RDMHostAgent

2) Kiem chung chup secure desktop:
   - Mo mot cua so UAC (vi du: chuot phai mot app -> Run as administrator) va GIU nguyen man hinh UAC vai giay.
   - Hoac khoa may (Win+L) vai giay.
   - Mo file log: %ProgramData%\RDM\desktop-probe.log
     Dong co "desktop=Winlogon  result=OK  WxH" nghia la agent (quyen SYSTEM) DA chup duoc secure desktop. Dat yeu cau.
     Neu thay "result=FAILED" tren desktop Winlogon thi agent chua chay dung quyen SYSTEM.
   - File %ProgramData%\RDM\host-service.log ghi lai viec dich vu khoi chay agent.

3) Go dich vu sau khi test:
     sc stop RDMHostAgent
     .\RDM.HostService.exe --uninstall

Kiem tra nhanh khong can dich vu (tuy chon): chay agent truc tiep quyen SYSTEM bang PsExec:
     psexec -s -d .\RDM.DesktopAgent.exe --selftest C:\Temp\secure.png
   roi mo UAC; anh C:\Temp\secure.png se la man hinh UAC neu chup duoc.

Luu y: day moi la Buoc 1-2 (chung minh chup duoc). Buoc tiep theo se dua khung hinh secure desktop
vao phien dieu khien va cho phep bam chuot/go phim len man hinh UAC.
'@
Set-Content -Path (Join-Path $out 'README-UAC-test.txt') -Value $readme -Encoding utf8

"=== Xong ==="
Get-ChildItem $out | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}} | Format-Table -AutoSize | Out-String
"Thu muc: $out"
