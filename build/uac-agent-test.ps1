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
RDM - Thu nghiem ho tro UAC / man hinh khoa (agent SYSTEM + named pipe)

Muc tieu: xac minh (A) agent chay quyen SYSTEM chup duoc secure desktop (UAC, man hinh khoa),
va (B) khung hinh + input di qua duoc named pipe giua agent (SYSTEM) va tien trinh nguoi dung.
Lam tren MAY BI DIEU KHIEN, mo PowerShell quyen Administrator trong thu muc nay.

1) Cai dich vu (tu khoi dong cung Windows, chay quyen SYSTEM). Dich vu se chay agent o che do --serve:
     .\RDM.HostService.exe --install
     sc start RDMHostAgent

2) (B) Kiem chung DUONG TRUYEN pipe (an toan, KHONG chup anh) - chay quyen NGUOI DUNG thuong:
     .\RDM.DesktopAgent.exe --ping
   In ra "connected  input-desktop=Default" nghia la tien trinh nguoi dung ket noi duoc toi agent SYSTEM qua pipe.

3) (A) Kiem chung CHUP secure desktop - agent tu ghi log tu ben trong (khong can client luc UAC):
     psexec -s -d .\RDM.DesktopAgent.exe --probe-loop "%ProgramData%\RDM\desktop-probe.log" 1
   Roi mo mot cua so UAC (chuot phai 1 app -> Run as administrator) GIU vai giay, hoac khoa may (Win+L) vai giay.
   Mo log: %ProgramData%\RDM\desktop-probe.log
     Dong "desktop=Winlogon  result=OK  WxH" => agent (SYSTEM) DA chup duoc secure desktop. Dat yeu cau.
     "result=FAILED" tren Winlogon => agent chua chay dung quyen SYSTEM.
   (Dung ban probe nay sau khi xong: dong cua so, hoac Stop-Process ten RDM.DesktopAgent cua phien probe.)

4) Go dich vu sau khi test:
     sc stop RDMHostAgent
     .\RDM.HostService.exe --uninstall

Trang thai: agent da phuc vu khung hinh (PNG) + nhan chuot/phim qua pipe (--serve), va client test --ping/--pullframe.
Buoc KE TIEP sau khi ban xac nhan (A) va (B) dat: ghep vao RDM.exe de khi may bi dieu khien dang o secure desktop
thi tu lay khung tu agent va chuyen chuot/phim len man hinh UAC; va them tuy chon cai dich vu trong bo cai.
'@
Set-Content -Path (Join-Path $out 'README-UAC-test.txt') -Value $readme -Encoding utf8

"=== Xong ==="
Get-ChildItem $out | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}} | Format-Table -AutoSize | Out-String
"Thu muc: $out"
