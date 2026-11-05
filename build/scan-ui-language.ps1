# Tim chu tieng Viet ngoai chu thich trong ma nguon. Giao dien va thong bao phai hoan toan bang tieng Anh; chu thich thi duoc.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
function Span($a, $b) { "$([char]$a)-$([char]$b)" }
$viet = '[' + (Span 0xC0 0xC3) + (Span 0xC8 0xCA) + (Span 0xCC 0xCD) + (Span 0xD2 0xD5) + (Span 0xD9 0xDA) + [char]0xDD + (Span 0xE0 0xE3) + (Span 0xE8 0xEA) + (Span 0xEC 0xED) + (Span 0xF2 0xF5) + (Span 0xF9 0xFA) + [char]0xFD + (Span 0x102 0x103) + (Span 0x110 0x111) + (Span 0x128 0x129) + (Span 0x168 0x169) + (Span 0x1A0 0x1A1) + (Span 0x1AF 0x1B0) + (Span 0x1EA0 0x1EF9) + ']'
$hits = 0
foreach ($f in (Get-ChildItem (Join-Path $Root 'src') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
  $n = 0
  foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
    $n++
    $i = $line.IndexOf('//')
    $code = if ($i -ge 0) { $line.Substring(0, $i) } else { $line }
    # Bo loc secret trong log co chu y nhan ca tu tieng Viet, phong khi nguoi dung nhap vao; khong hien thi len giao dien.
    if ($f.Name -eq 'Validators.cs' -and $code -match 'Regex|private\[_ \]') { continue }
    if ($code -match $viet) { "$($f.Name):$n  $($code.Trim())"; $hits++ }
  }
}
"CON_LAI=$hits"
if ($hits -gt 0) { exit 1 }
