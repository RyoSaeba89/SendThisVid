// SendThisVid UI (WPF, part of .NET Framework). The layout and styles are in Ui.xaml, embedded as a resource.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rectangle = System.Windows.Shapes.Rectangle;

class Preset
{
    public string Name, Detail, Suffix;
    public double TargetMB;
    public Preset(string name, string detail, double mb, string suffix) { Name = name; Detail = detail; TargetMB = mb; Suffix = suffix; }
}

class Ui
{
    static readonly Preset[] Presets =
    {
        new Preset("Discord", "10 MB · free", 9.5, "discord"),
        new Preset("Nitro Basic", "50 MB", 48, "nitrobasic"),
        new Preset("Nitro", "500 MB", 480, "nitro"),
        new Preset("Email", "25 MB · Gmail, Outlook", 18, "email"),
        new Preset("GitHub", "10 MB", 9.5, "github"),
        new Preset("Messenger", "25 MB", 24, "messenger"),
        new Preset("WhatsApp", "16 MB", 15, "whatsapp"),
        new Preset("Bluesky", "100 MB · 3 min", 95, "bluesky"),
        new Preset("Custom", "any size", 0, null),
    };
    static readonly string SettingsFile = Path.Combine(Encoder.DataDir, "settings.txt");
    static readonly Brush IdleStroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x47));
    static readonly Brush IdleFill = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x1A));
    static readonly Brush HotFill = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x26));

    readonly Window w;
    readonly RadioButton[] cards = new RadioButton[Presets.Length];
    Encoder job;
    Thread worker;
    readonly ObservableCollection<FileInfo> queue = new ObservableCollection<FileInfo>();
    string lastOutput;

    T F<T>(string name) { return (T)w.FindName(name); }

    public Ui(string[] startFiles)
    {
        using (var s = typeof(Ui).Assembly.GetManifestResourceStream("Ui.xaml")) w = (Window)XamlReader.Load(s);

        var grid = F<Panel>("Presets");
        var style = (Style)w.FindResource("Preset");
        for (int i = 0; i < Presets.Length; i++)
        {
            var p = Presets[i];
            cards[i] = new RadioButton { Content = p.Name, Tag = p.Detail, Style = style, GroupName = "preset" };
            cards[i].Click += (s, e) => Pick(p);
            grid.Children.Add(cards[i]);
        }

        var customBox = F<TextBox>("CustomMB");
        customBox.Text = "25";
        try
        {
            var parts = File.ReadAllText(SettingsFile).Split(';');
            customBox.Text = parts[parts.Length - 1]; // 1.0.0 stored "preset;size", now just the size
        }
        catch (Exception) { } // first run or unreadable settings: defaults
        var customPreset = Presets[Presets.Length - 1];
        F<Button>("ConvertBtn").Click += (s, e) => Start(customPreset);
        customBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) Start(customPreset); };

        F<Button>("MinBtn").Click += (s, e) => w.WindowState = WindowState.Minimized;
        F<Button>("CloseBtn").Click += (s, e) => w.Close();
        F<Button>("CancelBtn").Click += (s, e) => { if (job != null) job.Cancel(); };
        F<Button>("ShowBtn").Click += (s, e) => Process.Start("explorer.exe", "/select,\"" + lastOutput + "\"");
        F<Button>("BackBtn").Click += (s, e) => Show("ReadyView");
        F<Button>("AddBtn").Click += (s, e) => Browse(false);
        F<Button>("ReplaceBtn").Click += (s, e) => Browse(true);

        var list = F<ItemsControl>("QueueList");
        list.ItemsSource = queue;
        // The only buttons in the list are the row × buttons
        list.AddHandler(Button.ClickEvent, (RoutedEventHandler)((s, e) => Remove((FileInfo)((FrameworkElement)e.OriginalSource).DataContext)));
        queue.CollectionChanged += (s, e) => F<TextBlock>("QueueTitle").Text = queue.Count == 1 ? "1 video" : queue.Count + " videos";

        var zone = F<Grid>("Zone");
        zone.MouseLeftButtonUp += (s, e) => { if (F<UIElement>("IdleView").IsVisible) Browse(false); };
        w.DragEnter += (s, e) => Hot(job == null && e.Data.GetDataPresent(DataFormats.FileDrop));
        w.DragLeave += (s, e) => Hot(false);
        w.DragOver += (s, e) =>
        {
            e.Effects = job == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        w.Drop += (s, e) =>
        {
            Hot(false);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) Add(files);
        };
        w.Closing += (s, e) =>
        {
            if (job == null) return;
            job.Cancel();
            worker.Join(5000); // let the worker delete the partial output
        };
        Show("IdleView");
        Add(startFiles);
    }

    public Window Window { get { return w; } }

    void Show(string view)
    {
        foreach (var v in new[] { "IdleView", "ReadyView", "WorkView", "DoneView", "ErrorView" })
            F<UIElement>(v).Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
        bool picking = view == "ReadyView", browsing = picking || view == "IdleView";
        F<Grid>("Zone").Cursor = view == "IdleView" ? Cursors.Hand : null;
        // Destinations are clickable only once a video is loaded; the picked one stays lit while encoding
        foreach (var c in cards)
        {
            c.IsEnabled = picking;
            if (browsing) c.IsChecked = false;
        }
        F<UIElement>("BackBtn").Visibility = view == "DoneView" || view == "ErrorView" ? Visibility.Visible : Visibility.Collapsed;
        F<UIElement>("PresetHint").Visibility = view == "IdleView" ? Visibility.Visible : Visibility.Collapsed;
        if (browsing) F<UIElement>("CustomRow").Visibility = Visibility.Collapsed;
        F<TextBox>("CustomMB").IsEnabled = picking;
    }

    void Add(IEnumerable<string> paths)
    {
        if (job != null) return;
        foreach (var path in paths)
        {
            var f = new FileInfo(path);
            if (f.Exists && !queue.Any(q => q.FullName.Equals(f.FullName, StringComparison.OrdinalIgnoreCase))) queue.Add(f);
        }
        Show(queue.Count > 0 ? "ReadyView" : "IdleView");
    }

    void Remove(FileInfo f)
    {
        queue.Remove(f);
        if (queue.Count == 0) Show("IdleView");
    }

    void Pick(Preset p)
    {
        if (p.Suffix != null) { Start(p); return; }
        // Custom needs a size first: show the field, Convert or Enter starts
        F<UIElement>("CustomRow").Visibility = Visibility.Visible;
        var box = F<TextBox>("CustomMB");
        box.Focus();
        box.SelectAll();
    }

    void Hot(bool on)
    {
        var border = F<Rectangle>("ZoneBorder");
        border.Stroke = on ? (Brush)w.FindResource("Accent") : IdleStroke;
        border.StrokeThickness = on ? 2 : 1.5;
        F<Border>("ZoneBg").Background = on ? HotFill : IdleFill;
    }

    void Browse(bool replace)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Videos|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.flv;*.3gp;*.ts;*.mts;*.gif|All files|*.*" };
        if (d.ShowDialog(w) != true) return;
        if (replace) queue.Clear();
        Add(d.FileNames);
    }

    void SetProgress(double f)
    {
        f = Math.Max(0, Math.Min(1, f));
        F<TextBlock>("Percent").Text = (int)(f * 100) + "%";
        F<Border>("Bar").BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(F<Grid>("BarTrack").ActualWidth * f, TimeSpan.FromMilliseconds(250)));
    }

    void Start(Preset p)
    {
        if (job != null || queue.Count == 0) return;
        var customBox = F<TextBox>("CustomMB");
        double mb = p.TargetMB;
        if (p.Suffix == null && (!double.TryParse(customBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out mb) || mb < 1))
        {
            customBox.Focus();
            customBox.SelectAll();
            return;
        }
        string suffix = p.Suffix ?? mb.ToString(CultureInfo.InvariantCulture) + "mb";
        try
        {
            Directory.CreateDirectory(Encoder.DataDir);
            File.WriteAllText(SettingsFile, customBox.Text);
        }
        catch (Exception) { } // remembering the custom size is best effort

        Show("WorkView");

        var j = job = new Encoder();
        var files = queue.Select(f => f.FullName).ToArray();
        j.Status = msg => w.Dispatcher.BeginInvoke((Action)(() => F<TextBlock>("Status").Text = msg));
        j.Progress = f => w.Dispatcher.BeginInvoke((Action)(() => SetProgress(f)));
        worker = new Thread(() =>
        {
            var outputs = new List<string>();
            var failed = new List<string>();
            Exception err = null;
            for (int i = 0; i < files.Length; i++)
            {
                int n = i;
                w.Dispatcher.BeginInvoke((Action)(() => Begin(files[n], n, files.Length)));
                try { outputs.Add(j.Encode(files[i], mb, suffix)); }
                catch (OperationCanceledException ex) { err = ex; break; }
                catch (Exception ex) { err = ex; failed.Add(Path.GetFileName(files[i])); } // one bad video doesn't stop the rest
            }
            w.Dispatcher.BeginInvoke((Action)(() => Done(outputs, failed, err, mb, files.Length)));
        }) { IsBackground = true };
        worker.Start();
    }

    void Begin(string file, int i, int n)
    {
        F<TextBlock>("FileName").Text = (n > 1 ? (i + 1) + "/" + n + " · " : "") + Path.GetFileName(file);
        F<TextBlock>("Status").Text = "Starting…";
        F<Border>("Bar").BeginAnimation(FrameworkElement.WidthProperty, null);
        F<Border>("Bar").Width = 0;
        F<TextBlock>("Percent").Text = "0%";
    }

    void Done(List<string> outputs, List<string> failed, Exception err, double mb, int n)
    {
        job = null;
        if (err is OperationCanceledException) { Show("ReadyView"); return; } // same videos, pick again
        if (outputs.Count == 0)
        {
            var lines = err.Message.Split(new[] { '\n' }, 2);
            F<TextBlock>("ErrorTitle").Text = n > 1 ? "None of the " + n + " videos could be converted" : lines[0];
            F<TextBlock>("ErrorText").Text = n > 1 ? lines[0] : lines.Length > 1 ? lines[1].Trim() : "";
            Show("ErrorView");
            return;
        }
        lastOutput = outputs[outputs.Count - 1];
        int over = outputs.Count(o => new FileInfo(o).Length / 1e6 > mb);
        if (n == 1)
        {
            double size = new FileInfo(lastOutput).Length / 1e6;
            F<TextBlock>("DoneSize").Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", size) + (over > 0 ? "  · above target" : " · ready to send");
            F<TextBlock>("DoneName").Text = Path.GetFileName(lastOutput);
        }
        else
        {
            F<TextBlock>("DoneSize").Text = failed.Count == 0 ? n + " videos ready" : outputs.Count + " of " + n + " ready";
            F<TextBlock>("DoneName").Text = failed.Count > 0 ? "Failed: " + string.Join(", ", failed)
                : over > 0 ? over + " above target" : "Saved next to the originals";
        }
        Show("DoneView");
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Headless mode (used by test.ps1): SendThisVid.exe <video> <targetMB>
        // Two videos dropped on the exe are also 2 args: headless only when the second is a number
        double mb;
        if (args.Length == 2 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out mb))
        {
            try
            {
                var e = new Encoder { Status = Console.Error.WriteLine };
                var output = e.Encode(args[0], mb, args[1] + "mb");
                Console.WriteLine(output);
                return new FileInfo(output).Length <= mb * 1e6 ? 0 : 2;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        return new Application().Run(new Ui(args).Window);
    }
}
