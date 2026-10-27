# End-to-end check: generates sample videos with ffmpeg (from PATH) and runs SendThisVid.exe headless on them.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'SendThisVid.exe'
$dir = Join-Path ([IO.Path]::GetTempPath()) 'sendthisvid-test'
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dir | Out-Null

function Gen($name, $size, $seconds, $extra) {
    $f = Join-Path $dir $name
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=${size}:rate=30:duration=$seconds" `
        -f lavfi -i "sine=frequency=440:duration=$seconds" -c:v libx264 -crf 12 -c:a aac @extra $f
    $f
}

function Check($name, $file, $mb, $expectExit) {
    $p = Start-Process $exe -ArgumentList "`"$file`"", $mb -Wait -PassThru -NoNewWindow
    $out = [IO.Path]::ChangeExtension($file, $null).TrimEnd('.') + "_${mb}mb.mp4"
    $size = if (Test-Path $out) { (Get-Item $out).Length / 1e6 } else { 0 }
    $ok = $p.ExitCode -eq $expectExit -and ($expectExit -ne 0 -or ($size -gt 0 -and $size -le $mb))
    "{0,-6} {1,-34} exit={2} size={3:N2} MB (target {4} MB)" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $p.ExitCode, $size, $mb
    if ($ok -and $size -gt 0) {
        $dims = ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 $out
        "       -> $dims"
    }
    $script:failed += [int](-not $ok)
}

$failed = 0
$land = Gen 'landscape.mp4' '1920x1080' 60 @()
$port = Gen 'portrait.mp4' '1080x1920' 30 @()
$silent = Gen 'silent.mp4' '1280x720' 20 @('-an')
$rot = Join-Path $dir 'rotated.mp4'
ffmpeg -hide_banner -loglevel error -y -display_rotation 90 -i $land -c copy $rot  # phone-style rotation metadata

Check '60s 1080p -> Discord 10 MB' $land 9.5 0
Check '30s portrait -> 5 MB' $port 5 0
Check 'no audio track -> 3 MB' $silent 3 0
Check 'rotated metadata -> 9.5 MB' $rot 9.5 0
Check 'tiny source, huge target' $silent 480 0
Check '60s into 1 MB (must refuse)' $land 1 1

if ($failed) { throw "$failed test(s) failed" } else { "All tests passed" }
