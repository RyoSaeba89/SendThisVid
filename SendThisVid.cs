// SendThisVid encoding engine: drop a video, get an mp4 under a target size. UI is in Ui.cs / Ui.xaml.
// Targets .NET Framework 4.x (preinstalled on Windows 10/11), built with the csc.exe shipped in Windows. C# 5 only.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

[assembly: System.Reflection.AssemblyTitle("SendThisVid")]
[assembly: System.Reflection.AssemblyProduct("SendThisVid")]
[assembly: System.Reflection.AssemblyDescription("Compress a video to fit Discord, email, GitHub and other upload limits")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

class Encoder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Regex DurRx = new Regex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
    static readonly Regex TimeRx = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
    static readonly Regex BitrateRx = new Regex(@"Duration:.*bitrate: (\d+) kb/s");
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SendThisVid");

    public Action<string> Status = delegate { };
    public Action<double> Progress = delegate { };
    string ffmpeg;
    Process proc;
    volatile bool cancelled;

    public void Cancel()
    {
        cancelled = true;
        try { var p = proc; if (p != null && !p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
    }

    // ffmpeg is embedded gzipped; extracted once to %LOCALAPPDATA% (writable without admin rights).
    static string EnsureFfmpeg()
    {
        using (var res = typeof(Encoder).Assembly.GetManifestResourceStream("ffmpeg.exe.gz"))
        {
            if (res == null) return "ffmpeg"; // dev build without bundled ffmpeg: use PATH
            Directory.CreateDirectory(DataDir);
            var path = Path.Combine(DataDir, "ffmpeg-" + res.Length + ".exe");
            if (File.Exists(path)) return path;
            var tmp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            using (var gz = new GZipStream(res, CompressionMode.Decompress))
            using (var f = File.Create(tmp)) gz.CopyTo(f);
            if (File.Exists(path)) File.Delete(tmp); else File.Move(tmp, path);
            foreach (var old in Directory.GetFiles(DataDir, "ffmpeg-*.exe")) // left behind by previous app versions
                if (old != path) try { File.Delete(old); } catch (Exception) { }
            return path;
        }
    }

    // Returns { videoKbps, audioKbps, max short side in px }. videoKbps < 100 means the target is unreachable.
    public static int[] Plan(double targetMB, double seconds, int sourceKbps, bool hasAudio)
    {
        double totalK = targetMB * 8e6 * 0.98 / seconds / 1000; // 2% mp4 container overhead
        if (sourceKbps > 0) totalK = Math.Min(totalK, sourceKbps); // never inflate past the source
        int audioK = !hasAudio ? 0 : totalK >= 1000 ? 128 : totalK >= 400 ? 96 : 64;
        int videoK = (int)(totalK - audioK);
        int side = videoK >= 8000 ? 2160 : videoK >= 2000 ? 1080 : videoK >= 900 ? 720 : videoK >= 450 ? 480 : 360;
        return new[] { videoK, audioK, side };
    }

    static double Seconds(Match m)
    {
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, Inv);
    }

    static string Tail(string log)
    {
        var lines = log.TrimEnd().Split('\n');
        return string.Join("\n", lines, Math.Max(0, lines.Length - 6), Math.Min(6, lines.Length));
    }

    string Run(string args, double duration, int pass)
    {
        if (cancelled) throw new OperationCanceledException();
        var log = new StringBuilder();
        var p = new Process { StartInfo = new ProcessStartInfo(ffmpeg, "-hide_banner -nostdin -y " + args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        p.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            var m = TimeRx.Match(e.Data);
            if (m.Success) { if (duration > 0) Progress(Math.Min(1, (pass - 1 + Seconds(m) / duration) / 2)); }
            else lock (log) log.AppendLine(e.Data);
        };
        p.Start();
        proc = p;
        if (cancelled) Cancel();
        p.BeginErrorReadLine();
        p.WaitForExit();
        if (cancelled) throw new OperationCanceledException();
        if (pass > 0 && p.ExitCode != 0) throw new Exception("ffmpeg failed:\n" + Tail(log.ToString()));
        return log.ToString();
    }

    public string Encode(string input, double targetMB, string suffix)
    {
        Status("Preparing…");
        ffmpeg = EnsureFfmpeg();
        Status("Analyzing…");
        string info = Run("-i \"" + input + "\"", 0, 0);
        var dm = DurRx.Match(info);
        if (!dm.Success || !Regex.IsMatch(info, @"Stream #\S+.*: Video:"))
            throw new Exception("Can't read this video.\nThe file is damaged or isn't a video format SendThisVid can open.");
        double dur = Seconds(dm);
        var bm = BitrateRx.Match(info);
        bool hasAudio = Regex.IsMatch(info, @"Stream #\S+.*: Audio:");
        var plan = Plan(targetMB, dur, bm.Success ? int.Parse(bm.Groups[1].Value) : 0, hasAudio);
        int videoK = plan[0], audioK = plan[1], side = plan[2];

        string output = Path.Combine(Path.GetDirectoryName(input),
            Path.GetFileNameWithoutExtension(input) + "_" + suffix + ".mp4");
        string passlog = Path.Combine(Path.GetTempPath(), "sendthisvid-" + Guid.NewGuid().ToString("N"));
        // Caps the short side (works for portrait and rotated phone videos), never upscales, keeps dims even.
        string vf = string.Format("scale=w='if(gt(iw,ih),-2,trunc(min(iw,{0})/2)*2)':h='if(gt(iw,ih),trunc(min(ih,{0})/2)*2,-2)'", side);
        string audio = hasAudio ? string.Format(" -map 0:a:0 -c:a aac -b:a {0}k -ac 2", audioK) : " -an";
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                if (videoK < 100)
                    throw new Exception(string.Format(Inv, "Video too long ({0:0} s) for {1} MB: quality would be unusable.\nPick a bigger preset or trim the video.", dur, targetMB));
                string common = string.Format(Inv, "-i \"{0}\" -map 0:v:0 -vf \"{1}\" -pix_fmt yuv420p -c:v libx264 -preset medium -b:v {2}k -passlogfile \"{3}\"",
                    input, vf, videoK, passlog);
                string label = string.Format(Inv, "{0}p · {1:0.0#} Mb/s", side, (videoK + audioK) / 1000.0) + (attempt > 1 ? " · retry" : "");
                Status("Pass 1 of 2 · " + label);
                Run(common + " -pass 1 -an -f null NUL", dur, 1);
                Status("Pass 2 of 2 · " + label);
                Run(common + " -pass 2" + audio + " -movflags +faststart \"" + output + "\"", dur, 2);
                long size = new FileInfo(output).Length;
                if (size <= targetMB * 1e6 || attempt == 3) return output;
                videoK = (int)(videoK * targetMB * 1e6 / size * 0.95);
            }
        }
        catch
        {
            try { File.Delete(output); } catch (IOException) { }
            throw;
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(passlog) + "*"))
                try { File.Delete(f); } catch (IOException) { }
        }
    }
}
