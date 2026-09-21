Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$p = Get-Process -Name 'Kit.Settings' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { 'no process'; return }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$h = $p.MainWindowHandle
[W]::ShowWindow($h, 9) | Out-Null
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1500
$r = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
Write-Host ('window: {0},{1} {2}x{3}' -f $r.Left, $r.Top, $w, $ht)
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$out = 'C:\Users\Zen\Repos\Codings\Kit\shot.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host ('saved ' + $out + ' (' + (Get-Item $out).Length + ' bytes)')