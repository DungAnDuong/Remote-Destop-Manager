# Kiem thu tren man hinh that: chup man hinh va chuot/ban phim that di qua toan bo phien E2EE.
# Chay khi DANG NGOI O MAY va da mo khoa. Chuot se di chuyen trong khoang 10 giay; dung cham chuot.
# Vi du: .\build\real-desktop-test.ps1 -Platform x64
param(
  [ValidateSet('x86','x64')][string]$Platform = 'x64',
  [string]$Configuration = 'Debug'
)
$root = Split-Path $PSScriptRoot -Parent
$vt = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\Extensions\TestPlatform\vstest.console.exe"
& $vt "$root\tests\RDM.Tests\bin\$Platform\$Configuration\RDM.Tests.dll" "/Platform:$Platform" '/Framework:.NETFramework,Version=v4.8' '/TestCaseFilter:Category=RealDesktop' '/Logger:console;verbosity=normal' | Where-Object { $_ -match 'Passed|Failed|Error Message|Man hinh|FPS|Exception|Khong chup' }
exit $LASTEXITCODE
