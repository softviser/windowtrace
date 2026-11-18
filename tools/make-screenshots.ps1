# Takes the screenshots in docs\ from the app's demo mode. The demo rows are made up, so nothing from
# this machine ends up in the pictures. Build first (build.ps1); the window appears on screen for a moment.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-screenshots.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Shot {
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
'@
[Shot]::SetProcessDPIAware() | Out-Null

$root = Split-Path $PSScriptRoot
New-Item -ItemType Directory -Force "$root\docs" | Out-Null
foreach ($shot in 'main', 'search', 'menu') {
    $p = Start-Process "$root\dist\WindowTrace.exe" "--demo $shot" -PassThru
    while ($p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 100; $p.Refresh() }
    Start-Sleep -Milliseconds 1500
    $r = New-Object Shot+RECT
    # 9 = DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame, without the invisible resize border
    [Shot]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$r, 16) | Out-Null
    # the outermost pixel on every side is a translucent border that shows whatever is behind the window
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left - 2), ($r.Bottom - $r.Top - 2)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left + 1, $r.Top + 1, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save("$root\docs\screenshot-$shot.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $p.Kill()
    Write-Host "Wrote docs\screenshot-$shot.png"
}
