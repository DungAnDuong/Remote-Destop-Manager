# Tao assets\RDM.ico (nhieu kich thuoc, goc trong suot) va logo cho bo cai tu icon.png.
# Anh goc khong co kenh alpha: goc ngoai mau den duoc bo bang flood-fill tu 4 goc, nen mau den ben trong icon khong bi anh huong.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class IconMask {
  public static Bitmap Cut(Bitmap src, int threshold) {
    int w = src.Width, h = src.Height;
    var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(dst)) g.DrawImage(src, new Rectangle(0, 0, w, h));
    var data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new byte[w * h * 4];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    var seen = new bool[w * h];
    var q = new Queue<int>();
    foreach (var c in new[] { 0, w - 1, (h - 1) * w, h * w - 1 }) { q.Enqueue(c); seen[c] = true; }
    while (q.Count > 0) {
      var p = q.Dequeue();
      var i = p * 4;
      if (px[i] + px[i + 1] + px[i + 2] > threshold) continue;
      px[i + 3] = 0;
      int x = p % w, y = p / w;
      if (x > 0 && !seen[p - 1]) { seen[p - 1] = true; q.Enqueue(p - 1); }
      if (x < w - 1 && !seen[p + 1]) { seen[p + 1] = true; q.Enqueue(p + 1); }
      if (y > 0 && !seen[p - w]) { seen[p - w] = true; q.Enqueue(p - w); }
      if (y < h - 1 && !seen[p + w]) { seen[p + w] = true; q.Enqueue(p + w); }
    }
    Marshal.Copy(px, 0, data.Scan0, px.Length);
    dst.UnlockBits(data);
    return dst;
  }
  public static byte[] Dib(Bitmap b) {
    int w = b.Width, h = b.Height;
    var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var px = new byte[w * h * 4];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    b.UnlockBits(data);
    int maskStride = ((w + 31) / 32) * 4;
    var ms = new System.IO.MemoryStream();
    var o = new System.IO.BinaryWriter(ms);
    o.Write(40); o.Write(w); o.Write(h * 2); o.Write((short)1); o.Write((short)32); o.Write(0); o.Write(px.Length + maskStride * h); o.Write(0); o.Write(0); o.Write(0); o.Write(0);
    for (var y = h - 1; y >= 0; y--) o.Write(px, y * w * 4, w * 4);
    o.Write(new byte[maskStride * h]);
    o.Flush();
    return ms.ToArray();
  }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$src = [Drawing.Bitmap]::FromFile((Join-Path $root 'icon.png'))
$cut = [IconMask]::Cut($src, 90)
$src.Dispose()

function Scale($size) {
  $b = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($b)
  $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'; $g.SmoothingMode = 'HighQuality'
  $attr = New-Object Drawing.Imaging.ImageAttributes
  $attr.SetWrapMode('TileFlipXY')
  $g.DrawImage($cut, (New-Object Drawing.Rectangle 0, 0, $size, $size), 0, 0, $cut.Width, $cut.Height, 'Pixel', $attr)
  $g.Dispose(); $attr.Dispose()
  return $b
}

# 256 dung PNG (Windows Vista tro len), cac co nho dung DIB 32 bit de tuong thich rong nhat.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) {
  $b = Scale $s
  if ($s -eq 256) { $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $images += ,@($s, $ms.ToArray()) }
  else { $images += ,@($s, [IconMask]::Dib($b)) }
  $b.Dispose()
}
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
  $s = $i[0]; $d = $i[1]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
  $offset += $d.Length
}
foreach ($i in $images) { $w.Write([byte[]]$i[1]) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'RDM.ico'), $out.ToArray())

# Logo hien trong bo cai.
$logo = Scale 64
$logo.Save((Join-Path $assets 'RDM-logo-64.png'), [Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
$cut.Dispose()
"Da tao $assets\RDM.ico: $((Get-Item (Join-Path $assets 'RDM.ico')).Length) byte, cac co: $($sizes -join ', ')"
