# Chay nhanh bo kiem thu va chi in ket qua can xem (dung khi phat trien).
param([string]$Platform = 'x64', [string]$Configuration = 'Debug', [string]$Filter = '')
$root = Split-Path $PSScriptRoot -Parent
$vt = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\Extensions\TestPlatform\vstest.console.exe"
$a = @("$root\tests\RDM.Tests\bin\$Platform\$Configuration\RDM.Tests.dll", "/Platform:$Platform", '/Framework:.NETFramework,Version=v4.8', '/Logger:console;verbosity=normal')
if ($Filter) { $a += "/TestCaseFilter:$Filter" }
& $vt @a | Where-Object { $_ -match '^\s+Failed |Error Message|^\s+Assert|Expected:|Actual:|Exception|Total tests|^\s+Passed:|^\s+Failed:|PBKDF2 |Import 5000|^\s+at RDM' } | Select-Object -First 80
