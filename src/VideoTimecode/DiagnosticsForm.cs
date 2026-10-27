using System.Diagnostics;
using System.Text;

namespace VideoTimecode;

// Shows the diagnostics report, keeps a copy in the settings folder, and offers the fixes
// that most often help: checking the GPUs again, a fresh FFmpeg, and a real test encode
sealed class DiagnosticsForm : Form
{
    readonly Func<IReadOnlyList<Worker>?> getWorkers;
    readonly Func<AppSettings> getSettings;
    readonly Func<Task> recheck;
    readonly Func<Task<bool>> redownload;
    readonly Func<string?> firstVideo;
    readonly bool busy;

    readonly TextBox text = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9F), BackColor = SystemColors.Window,
    };
    readonly Label lblInfo = new() { Dock = DockStyle.Top, AutoSize = false, Height = 30, TextAlign = ContentAlignment.MiddleLeft };
    readonly FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    readonly Button btnRecheck, btnTrial, btnDownload;
    string? trial;
    CancellationTokenSource? cts;

    public DiagnosticsForm(Func<IReadOnlyList<Worker>?> getWorkers, Func<AppSettings> getSettings, Func<Task> recheck,
        Func<Task<bool>> redownload, Func<string?> firstVideo, bool busy)
    {
        this.getWorkers = getWorkers;
        this.getSettings = getSettings;
        this.recheck = recheck;
        this.redownload = redownload;
        this.firstVideo = firstVideo;
        this.busy = busy;

        Text = "Video Timecode - diagnostics";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        // Laid out at 96 dpi and scaled up to the screen's
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(1040, 700);
        MinimumSize = new Size(760, 400);
        Padding = new Padding(10);

        btnRecheck = Btn("Re-check GPUs", 120, async () => await Work("Checking the GPU encoders again...", recheck));
        btnTrial = Btn("Test encoders on a video...", 180, TrialAsync);
        btnDownload = Btn("Re-download FFmpeg", 140, async () => await Work("Downloading FFmpeg...", async () =>
        {
            if (await redownload()) await recheck();
        }));
        buttons.Controls.AddRange(new Control[]
        {
            btnRecheck, btnTrial, btnDownload,
            Btn("Copy", 70, () => { Clipboard.SetText(text.Text); lblInfo.Text = "Copied to the clipboard."; return Task.CompletedTask; }),
            Btn("Save report as...", 120, SaveAs),
            Btn("Open folder", 100, () =>
            {
                if (File.Exists(Diagnostics.ReportPath)) Process.Start("explorer.exe", $"/select,\"{Diagnostics.ReportPath}\"");
                return Task.CompletedTask;
            }),
            Btn("Close", 80, () => { Close(); return Task.CompletedTask; }),
        });
        if (busy)
            foreach (var b in new[] { btnRecheck, btnTrial, btnDownload }) b.Enabled = false;

        Controls.Add(text);
        Controls.Add(lblInfo);
        Controls.Add(buttons);
    }

    Button Btn(string label, int width, Func<Task> onClick)
    {
        var b = new Button { Text = label, MinimumSize = new Size(width, 30), AutoSize = true };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshReport();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        cts?.Cancel();
        base.OnFormClosing(e);
    }

    async Task RefreshReport()
    {
        SetBusy(true, "Collecting details about this PC and FFmpeg...");
        string report;
        try
        {
            cts = new CancellationTokenSource();
            var workers = getWorkers();
            var settings = getSettings();
            report = await Task.Run(() => Diagnostics.BuildAsync(workers, settings, trial, cts.Token));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { report = "Building the report failed:\r\n" + ex; }
        if (IsDisposed) return;
        text.Text = report.Replace("\r\n", "\n").Replace("\n", "\r\n");
        string saved;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(Diagnostics.ReportPath, text.Text, new UTF8Encoding(false));
            saved = "Saved as diagnostics.txt in the settings folder. Send that file when asking for help: Open folder shows it.";
        }
        catch (Exception ex) { saved = "Couldn't save the report: " + ex.Message; }
        SetBusy(false, saved);
    }

    async Task Work(string message, Func<Task> action)
    {
        SetBusy(true, message);
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Video Timecode", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        await RefreshReport();
    }

    // Burns the first few seconds of a video with each encoder, to show which ones really work
    async Task TrialAsync()
    {
        var video = firstVideo();
        if (video == null)
        {
            using var d = new OpenFileDialog { Title = "Choose a video to test the encoders with" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            video = d.FileName;
        }
        var workers = getWorkers();
        if (workers == null) { lblInfo.Text = "The encoders haven't been checked yet; click Re-check GPUs first."; return; }

        SetBusy(true, $"Test-encoding the first 3 seconds of {Path.GetFileName(video)} with each encoder...");
        var lines = new StringBuilder();
        lines.AppendLine($"Video: {video}");
        cts = new CancellationTokenSource();
        try
        {
            var settings = getSettings();
            await Task.Run(() => JobRunner.TrialAsync(video, settings, workers, l =>
            {
                lock (lines) lines.AppendLine(l);
                var first = l.TrimStart();
                if (first.StartsWith("OK") || first.StartsWith("FAIL") || first.StartsWith("SKIP"))
                    BeginInvoke(() => { if (!IsDisposed) lblInfo.Text = first.Length > 140 ? first[..140] + "..." : first; });
            }, cts.Token));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { lines.AppendLine("FAILED: " + ex.Message); }
        trial = lines.ToString();
        await RefreshReport();
        // Jump to the results
        int at = text.Text.IndexOf("== Encoder test on a video", StringComparison.Ordinal);
        if (at >= 0) { text.SelectionStart = at; text.SelectionLength = 0; text.ScrollToCaret(); }
    }

    Task SaveAs()
    {
        using var d = new SaveFileDialog
        {
            FileName = "Video Timecode diagnostics.txt",
            Filter = "Text files|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(d.FileName, text.Text, new UTF8Encoding(false));
            lblInfo.Text = "Saved to " + d.FileName;
        }
        return Task.CompletedTask;
    }

    void SetBusy(bool on, string message)
    {
        lblInfo.Text = message;
        UseWaitCursor = on;
        foreach (var b in new[] { btnRecheck, btnTrial, btnDownload }) b.Enabled = !on && !busy;
    }
}
