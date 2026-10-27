// SendThisVid UI (WPF, part of .NET Framework). The layout and styles are in Ui.xaml, embedded as a resource.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    string lastOutput;

    T F<T>(string name) { return (T)w.FindName(name); }

    public Ui(string file)
    {
        using (var s = typeof(Ui).Assembly.GetManifestResourceStream("Ui.xaml")) w = (Window)XamlReader.Load(s);

        var grid = F<Panel>("Presets");
        var style = (Style)w.FindResource("Preset");
        for (int i = 0; i < Presets.Length; i++)
        {
            cards[i] = new RadioButton { Content = Presets[i].Name, Tag = Presets[i].Detail, Style = style, GroupName = "preset" };
            cards[i].Checked += (s, e) => F<UIElement>("CustomRow").Visibility = Selected().Suffix == null ? Visibility.Visible : Visibility.Collapsed;
            grid.Children.Add(cards[i]);
        }

        int idx = 0;
        string custom = "25";
        try
        {
            var parts = File.ReadAllText(SettingsFile).Split(';');
            idx = Math.Max(0, Math.Min(int.Parse(parts[0]), Presets.Length - 1));
            custom = parts[1];
        }
        catch (Exception) { } // first run or unreadable settings: defaults
        F<TextBox>("CustomMB").Text = custom;
        cards[idx].IsChecked = true;

        F<Button>("MinBtn").Click += (s, e) => w.WindowState = WindowState.Minimized;
        F<Button>("CloseBtn").Click += (s, e) => w.Close();
        F<Button>("CancelBtn").Click += (s, e) => { if (job != null) job.Cancel(); };
        F<Button>("ShowBtn").Click += (s, e) => Process.Start("explorer.exe", "/select,\"" + lastOutput + "\"");
        F<Button>("AgainBtn").Click += (s, e) => Show("IdleView");
        F<Button>("RetryBtn").Click += (s, e) => Show("IdleView");

        var zone = F<Grid>("Zone");
        zone.MouseLeftButtonUp += (s, e) => { if (F<UIElement>("IdleView").IsVisible) Browse(); };
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
            if (files != null && files.Length > 0) Start(files[0]);
        };
        w.Closing += (s, e) =>
        {
            if (job == null) return;
            job.Cancel();
            worker.Join(5000); // let the worker delete the partial output
        };
        if (file != null) w.Loaded += (s, e) => Start(file);
    }

    public Window Window { get { return w; } }

    Preset Selected()
    {
        for (int i = 0; i < cards.Length; i++) if (cards[i].IsChecked == true) return Presets[i];
        return Presets[0];
    }

    void Show(string view)
    {
        foreach (var v in new[] { "IdleView", "WorkView", "DoneView", "ErrorView" })
            F<UIElement>(v).Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
        F<Grid>("Zone").Cursor = view == "IdleView" ? Cursors.Hand : null;
    }

    void Hot(bool on)
    {
        var border = F<Rectangle>("ZoneBorder");
        border.Stroke = on ? (Brush)w.FindResource("Accent") : IdleStroke;
        border.StrokeThickness = on ? 2 : 1.5;
        F<Border>("ZoneBg").Background = on ? HotFill : IdleFill;
    }

    void Browse()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "Videos|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.flv;*.3gp;*.ts;*.mts;*.gif|All files|*.*" };
        if (d.ShowDialog(w) == true) Start(d.FileName);
    }

    void SetProgress(double f)
    {
        f = Math.Max(0, Math.Min(1, f));
        F<TextBlock>("Percent").Text = (int)(f * 100) + "%";
        F<Border>("Bar").BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(F<Grid>("BarTrack").ActualWidth * f, TimeSpan.FromMilliseconds(250)));
    }

    void Start(string file)
    {
        if (job != null) return;
        var p = Selected();
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
            File.WriteAllText(SettingsFile, Array.IndexOf(Presets, p) + ";" + customBox.Text);
        }
        catch (Exception) { } // remembering the preset is best effort

        foreach (var c in cards) c.IsEnabled = false;
        customBox.IsEnabled = false;
        F<TextBlock>("FileName").Text = Path.GetFileName(file);
        F<TextBlock>("Status").Text = "Starting…";
        F<Border>("Bar").BeginAnimation(FrameworkElement.WidthProperty, null);
        F<Border>("Bar").Width = 0;
        F<TextBlock>("Percent").Text = "0%";
        Show("WorkView");

        var j = job = new Encoder();
        j.Status = msg => w.Dispatcher.BeginInvoke((Action)(() => F<TextBlock>("Status").Text = msg));
        j.Progress = f => w.Dispatcher.BeginInvoke((Action)(() => SetProgress(f)));
        worker = new Thread(() =>
        {
            string result = null;
            Exception err = null;
            try { result = j.Encode(file, mb, suffix); } catch (Exception ex) { err = ex; }
            w.Dispatcher.BeginInvoke((Action)(() => Done(result, err, mb)));
        }) { IsBackground = true };
        worker.Start();
    }

    void Done(string output, Exception err, double mb)
    {
        job = null;
        foreach (var c in cards) c.IsEnabled = true;
        F<TextBox>("CustomMB").IsEnabled = true;
        if (err is OperationCanceledException) { Show("IdleView"); return; }
        if (err != null)
        {
            var lines = err.Message.Split(new[] { '\n' }, 2);
            F<TextBlock>("ErrorTitle").Text = lines[0];
            F<TextBlock>("ErrorText").Text = lines.Length > 1 ? lines[1].Trim() : "";
            Show("ErrorView");
            return;
        }
        double size = new FileInfo(output).Length / 1e6;
        F<TextBlock>("DoneSize").Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", size) + (size > mb ? "  · above target" : " · ready to send");
        F<TextBlock>("DoneName").Text = Path.GetFileName(output);
        lastOutput = output;
        Show("DoneView");
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Headless mode (used by test.ps1): SendThisVid.exe <video> <targetMB>
        if (args.Length == 2)
        {
            try
            {
                var mb = double.Parse(args[1], CultureInfo.InvariantCulture);
                var e = new Encoder { Status = Console.Error.WriteLine };
                var output = e.Encode(args[0], mb, args[1] + "mb");
                Console.WriteLine(output);
                return new FileInfo(output).Length <= mb * 1e6 ? 0 : 2;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        return new Application().Run(new Ui(args.Length == 1 ? args[0] : null).Window);
    }
}
