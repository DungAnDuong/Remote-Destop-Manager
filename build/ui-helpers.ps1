# Ham dung chung de thao tac giao dien RDM khi kiem thu.
# Luu y: PowerShell khong phan biet hoa thuong ten bien, tranh dat ten trung cac bien duoi day.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
# Add-Type bien dich tam: thu lai neu trinh bien dich gap loi nhat thoi (da gap BadImageFormat mot lan). Lan loi thu 3 van bao that bai.
if (-not ('W32' -as [type])) {
  $w32Source = @'
using System; using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
  for ($attempt = 1; ; $attempt++) {
    try { Add-Type -TypeDefinition $w32Source -ErrorAction Stop; break }
    catch { if ($attempt -ge 3) { throw }; Start-Sleep -Seconds 2 }
  }
}
$UiaElement = [System.Windows.Automation.AutomationElement]
$UiaScope = [System.Windows.Automation.TreeScope]
$UiaAll = [System.Windows.Automation.Condition]::TrueCondition
$SaveLabel = 'Save'
$CreateLabel = 'Create Account'
$ManageLabel = 'Device Management (sign-in required)'
$ThemeLabel = 'Light/Dark'
$LogoutLabel = 'Sign Out'
$ContinueLabel = 'Continue'
$TrustLabel = 'Trust This Device'
$AcceptLabel = 'Accept'
$StopLabel = 'Disconnect Now'

function TopWindows($procId) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($UiaElement::ProcessIdProperty, $procId)
  @($UiaElement::RootElement.FindAll($UiaScope::Children, $cond))
}
function Descend($root) { @($root.FindAll($UiaScope::Descendants, $UiaAll)) }
function FindButton($root, $name) {
  foreach ($e in (Descend $root)) { if ($e.Current.ClassName -match '(?i)button' -and $e.Current.Name -eq $name) { return $e } }
  return $null
}
function WaitButton($procId, $name, $sec = 30) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $sec) {
    foreach ($w in (TopWindows $procId)) { $b = FindButton $w $name; if ($b) { return $b } }
    Start-Sleep -Milliseconds 200
  }
  throw "Khong thay nut '$name'"
}
# Cua so co ten khop mau (phan biet hoa thuong), ke ca cua so con nhu hop thoai.
function WaitWindow($procId, $pattern, $sec = 30) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $sec) {
    foreach ($w in (TopWindows $procId)) {
      if ($w.Current.Name -cmatch $pattern) { return $w }
      foreach ($d in (Descend $w)) { if ($d.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $d.Current.Name -cmatch $pattern) { return $d } }
    }
    Start-Sleep -Milliseconds 200
  }
  throw "Khong thay cua so khop '$pattern'"
}
# Hop thoai chua nut co ten nay.
function DialogWith($procId, $buttonName) {
  foreach ($w in (TopWindows $procId)) {
    foreach ($d in (Descend $w)) {
      if ($d.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and (FindButton $d $buttonName)) { return $d }
    }
    if ($w.Current.Name -cnotmatch '^REMOTE DESKTOP MANAGER$' -and (FindButton $w $buttonName)) { return $w }
  }
  foreach ($w in (TopWindows $procId)) { if (FindButton $w $buttonName) { return $w } }
  throw "Khong thay hop thoai co nut '$buttonName'"
}
function Click($e) { [W32]::PostMessage([IntPtr]$e.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
function SetText($e, $text) { [W32]::SendMessage([IntPtr]$e.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $text) | Out-Null }
# O nhap sap theo vi tri tren man hinh, tu tren xuong.
function EditsOf($root) { @(Descend $root | Where-Object { $_.Current.ClassName -match '\.EDIT\.' } | Sort-Object { $_.Current.BoundingRectangle.Y }) }
function Label($root, $pattern) {
  foreach ($e in (Descend $root)) { if ($e.Current.ClassName -match '\.STATIC\.' -and $e.Current.Name -match $pattern) { return $e.Current.Name } }
  return $null
}
function WaitLabel($procId, $pattern, $sec = 30) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $sec) {
    foreach ($w in (TopWindows $procId)) { $l = Label $w $pattern; if ($l) { return $l } }
    Start-Sleep -Milliseconds 300
  }
  throw "Khong thay nhan khop '$pattern'"
}
function Shot($win, $file) {
  $h = [IntPtr]$win.Current.NativeWindowHandle
  $r = New-Object W32+RECT
  [W32]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  $g = [Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [W32]::PrintWindow($h, $dc, 2) | Out-Null
  $g.ReleaseHdc($dc); $g.Dispose()
  $bmp.Save((Join-Path $script:ShotDir "$($script:ShotPrefix)$file.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
