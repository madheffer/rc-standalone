# cs2_front.ps1 [-Shot <png>]: bring CS2's game window (title "Counter-Strike 2") to the front,
# optionally screenshot the screen at half size afterwards.
param([string]$Shot)
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
    public static IntPtr Find(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (IsWindowVisible(h) && sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
$h = [Win]::Find("Counter-Strike 2")
if ($h -eq [IntPtr]::Zero) { "no game window"; exit 1 }
# An Alt tap lets a background process take the foreground.
[Win]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Win]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
[Win]::ShowWindow($h, 9) | Out-Null
[Win]::SetForegroundWindow($h) | Out-Null
"foreground $h"
if ($Shot) {
    Start-Sleep -Milliseconds 800
    Add-Type -AssemblyName System.Windows.Forms,System.Drawing
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    (New-Object System.Drawing.Bitmap $bmp, ([int]($b.Width / 2)), ([int]($b.Height / 2))).Save($Shot)
    "shot $Shot"
}
