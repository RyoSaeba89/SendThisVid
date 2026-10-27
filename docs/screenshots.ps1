# Regenerates the README screenshots in docs/ by driving the real app. Needs ffmpeg on PATH for the sample clip.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[void][Win]::SetProcessDPIAware()
[void][Win]::SetCursorPos(0, 0)  # keep hover effects out of the shots

function Save-Window([IntPtr]$h, [string]$path) {
    $w = [Win+RECT]::new(); $f = [Win+RECT]::new()
    [void][Win]::GetWindowRect($h, [ref]$w)
    [void][Win]::DwmGetWindowAttribute($h, 9, [ref]$f, 16)  # DWMWA_EXTENDED_FRAME_BOUNDS: drops the invisible resize border
    $bmp = [Drawing.Bitmap]::new($w.R - $w.L, $w.B - $w.T)
    $g = [Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
    [void][Win]::PrintWindow($h, $dc, 2)  # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($dc); $g.Dispose()
    $crop = $bmp.Clone([Drawing.Rectangle]::new($f.L - $w.L, $f.T - $w.T, $f.R - $f.L, $f.B - $f.T), $bmp.PixelFormat)
    $crop.Save($path); $crop.Dispose(); $bmp.Dispose()
}

$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'SendThisVid.exe'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'sendthisvid-shots'
New-Item -ItemType Directory -Force $tmp | Out-Null
$clips = 'holiday-clip', 'birthday-party', 'concert' | ForEach-Object {
    $f = Join-Path $tmp "$_.mp4"
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=1920x1080:rate=30:duration=30' `
        -f lavfi -i 'sine=frequency=440:duration=30' -c:v libx264 -crf 14 -c:a aac $f
    $f
}
# Same custom size as a first run
$data = Join-Path $env:LOCALAPPDATA 'SendThisVid'
New-Item -ItemType Directory -Force $data | Out-Null
Set-Content (Join-Path $data 'settings.txt') '25' -NoNewline

function Launch($arguments) {
    $p = if ($arguments) { Start-Process $exe -ArgumentList $arguments -PassThru } else { Start-Process $exe -PassThru }
    while ($p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 100; $p.Refresh() }
    Start-Sleep -Milliseconds 800
    $p
}
function Encoders { @(Get-Process 'ffmpeg-*' -ErrorAction SilentlyContinue).Count }

$p = Launch $null
Save-Window $p.MainWindowHandle "$PSScriptRoot\idle.png"; $p.Kill()

$p = Launch ($clips | ForEach-Object { "`"$_`"" })
Save-Window $p.MainWindowHandle "$PSScriptRoot\queue.png"

# Click Discord with the keyboard: UI Automation can focus the card, Space clicks it
$h = $p.MainWindowHandle
$name = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Discord')
[Windows.Automation.AutomationElement]::FromHandle($h).FindFirst('Descendants', $name).SetFocus()
Start-Sleep -Milliseconds 300
if ([Win]::GetForegroundWindow() -ne $h) { $p.Kill(); throw "SendThisVid is not the foreground window, rerun without touching the mouse/keyboard" }
[Windows.Forms.SendKeys]::SendWait(' ')

# Encoding: captured once pass 2 of the second video is writing its output
$outs = $clips | ForEach-Object { $_ -replace '\.mp4$', '_discord.mp4' }
while (-not (Test-Path $outs[1])) { Start-Sleep -Milliseconds 50 }
Start-Sleep -Seconds 2
Save-Window $h "$PSScriptRoot\encoding.png"

$idle = 0  # ffmpeg restarts between passes, so require 1.5 s with none running
while (-not (Test-Path $outs[2]) -or $idle -lt 15) { Start-Sleep -Milliseconds 100; $idle = if (Encoders) { 0 } else { $idle + 1 } }
Save-Window $h "$PSScriptRoot\done.png"; $p.Kill()
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
"Screenshots written to docs/"
