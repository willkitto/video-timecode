using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Text;
using System.Globalization;

namespace VideoTimecode;

sealed class MainForm : Form
{
    static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v", ".mts", ".m2ts", ".ts", ".avi", ".mkv", ".mxf", ".wmv", ".webm", ".3gp", ".mpg", ".mpeg" };

    sealed record PosItem(string Name, Anchor9 Value);
    sealed record Choice(string Text, string Value) { public override string ToString() => Text; }

    static readonly PosItem[] Positions =
    {
        new("Top left", Anchor9.TopLeft), new("Top center", Anchor9.TopCenter), new("Top right", Anchor9.TopRight),
        new("Middle left", Anchor9.MiddleLeft), new("Center", Anchor9.Center), new("Middle right", Anchor9.MiddleRight),
        new("Bottom left", Anchor9.BottomLeft), new("Bottom center", Anchor9.BottomCenter), new("Bottom right", Anchor9.BottomRight),
    };

    // Ready-made overlays for the Add menu: (menu text, overlay text, size %)
    static readonly (string Name, string Text, double Size)[] OverlayPresets =
    {
        ("Date and time", "{datetime}", 5),
        ("Elapsed time", "{elapsed}", 4),
        ("Frame counter", "Frame {frame} / {frames}", 3.5),
        ("Camera timecode", "TC {tc}", 3.5),
        ("File name", "{filename}", 3),
        ("Camera model", "{camera}", 3),
        ("GPS location", "{gps}", 3),
        ("Video info", "{resolution}  {fps} fps  {codec}", 3),
        ("Custom text", "Your text here", 4),
    };

    static readonly Anchor9[] FreePositionOrder =
    {
        Anchor9.BottomRight, Anchor9.TopLeft, Anchor9.TopRight, Anchor9.BottomLeft,
        Anchor9.TopCenter, Anchor9.BottomCenter, Anchor9.MiddleLeft, Anchor9.MiddleRight, Anchor9.Center,
    };

    AppSettings settings;
    readonly bool quickRun;
    string? startupWarning;
    bool loadingPresets;
    readonly BindingList<Overlay> overlays;
    readonly string logPath = Path.Combine(AppSettings.Dir, "last-run.log");
    List<Worker>? workers;
    Task? setupTask;
    CancellationTokenSource? cts;
    bool running, closeWhenDone;
    string edgeColor;
    string? lastOutput;
    List<string> allFonts = new(), evenFonts = new();
    readonly Dictionary<Job, double> jobProgress = new();

    readonly ListView lvFiles = new() { View = View.Details, FullRowSelect = true, AllowDrop = true, HideSelection = false, ShowItemToolTips = true, Dock = DockStyle.Fill };
    readonly RadioButton rbSubs = new() { Text = "External subtitle file (.ass next to the video)  -  fastest; video untouched", AutoSize = true };
    readonly RadioButton rbEmbed = new() { Text = "Subtitle stream in a new .mkv  -  switch on/off in the player; no quality loss", AutoSize = true };
    readonly RadioButton rbBurn = new() { Text = "Burn into the picture  -  always visible; re-encoded to match the source", AutoSize = true };
    readonly ComboBox cbCodec = Drop(), cbQuality = Drop(), cbEncoder = Drop(330), cbParallel = Drop();
    readonly DataGridView grid = new();
    readonly ComboBox cbFont = Drop(200), cbEdge = Drop(130);
    readonly CheckBox chkEven = new() { Text = "Only fonts with even-width digits (no wobble)", AutoSize = true, Checked = true };
    readonly CheckBox chkBold = new() { Text = "Bold", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    readonly Button btnEdgeColor = new() { Width = 90, Height = 26, FlatStyle = FlatStyle.Flat };
    readonly NumericUpDown numOpacity = new() { Minimum = 0, Maximum = 100, Increment = 5, Width = 55 };
    readonly PreviewPanel preview = new() { Dock = DockStyle.Fill, BackColor = SystemColors.Control, MinimumSize = new Size(320, 180) };
    readonly ComboBox cbFormat = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 230 };
    readonly ComboBox cbMarks = Drop(330), cbZone = Drop(330);
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Margin = new Padding(6, 8, 6, 8) };
    readonly Label lblStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "Starting..." };
    readonly LinkLabel lnkOpen = new() { Text = "Open output folder", AutoSize = true, Visible = false, Anchor = AnchorStyles.Left };
    readonly LinkLabel lnkLog = new() { Text = "Open log", AutoSize = true, Visible = false, Anchor = AnchorStyles.Left };
    readonly Button btnRun = new() { Text = "Run", Width = 100, Height = 32 };
    readonly Button btnCancel = new() { Text = "Close", Width = 100, Height = 32 };
    readonly ComboBox cbPreset = Drop(280);
    readonly Button btnSavePreset = new() { Text = "Save", Width = 80, Height = 28 };
    readonly Button btnMorePreset = new() { Text = "More  \u25BE", Width = 90, Height = 28 };
    Control fileButtons = null!, outputPanel = null!, encodePanel = null!, timePanel = null!, rightPanel = null!, presetBar = null!;

    static ComboBox Drop(int width = 260) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

    public MainForm(IEnumerable<string> paths, bool useSaved, string? preset, bool exitWhenDone)
    {
        settings = AppSettings.Load();
        string? startPreset = null;
        if (preset != null)
        {
            if (Presets.Load(preset) is { } p)
            {
                settings = p;
                startPreset = Presets.Names().FirstOrDefault(n => n.Equals(preset, StringComparison.OrdinalIgnoreCase)) ?? preset;
            }
            else startupWarning = $"There is no preset called \"{preset}\", so nothing was started.\n\nPick a preset at the top of the window, or save one with \"Save as\".";
        }
        quickRun = startPreset != null || (useSaved && AppSettings.Exists);
        closeWhenDone = quickRun && exitWhenDone;
        overlays = new BindingList<Overlay>(settings.Overlays.Select(o => o.Clone()).ToList());
        edgeColor = settings.EdgeColor;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        Text = "Video Timecode";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1300, 820);
        MinimumSize = new Size(1050, 720);
        AllowDrop = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        presetBar = BuildPresetBar();
        root.Controls.Add(presetBar, 0, 0);
        root.SetColumnSpan(presetBar, 2);
        root.Controls.Add(BuildLeft(), 0, 1);
        root.Controls.Add(rightPanel = BuildRight(), 1, 1);
        var bottom = BuildBottom();
        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);
        ResumeLayout(true);

        LoadUi();
        RefreshPresets(startPreset ?? Presets.LastRun);
        AddPaths(paths);
        WireEvents();
    }

    // --- layout -----------------------------------------------------------------

    static Label Header(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4),
    };

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        f.Controls.AddRange(controls);
        return f;
    }

    static TableLayoutPanel FormTable(params (string Label, Control Control)[] rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(14, 0, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (label, control) in rows)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Anchor = AnchorStyles.Left;
            t.Controls.Add(Lbl(label));
            t.Controls.Add(control);
        }
        return t;
    }

    static TableLayoutPanel Column(params (Control Control, float Percent)[] rows)
    {
        // Fills its cell when a row stretches; otherwise sizes itself to its contents
        bool stretches = rows.Any(r => r.Percent > 0);
        var t = new TableLayoutPanel { ColumnCount = 1, Margin = Padding.Empty };
        if (stretches) t.Dock = DockStyle.Fill;
        else { t.AutoSize = true; t.AutoSizeMode = AutoSizeMode.GrowAndShrink; }
        t.ColumnStyles.Add(stretches ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        foreach (var (control, percent) in rows)
        {
            t.RowStyles.Add(percent > 0 ? new RowStyle(SizeType.Percent, percent) : new RowStyle(SizeType.AutoSize));
            if (percent > 0) control.Dock = DockStyle.Fill;
            t.Controls.Add(control);
        }
        return t;
    }

    static Button Btn(string text, EventHandler onClick, int width = 100)
    {
        var b = new Button { Text = text, Width = width, Height = 28 };
        b.Click += onClick;
        return b;
    }

    Control BuildPresetBar()
    {
        var more = new ContextMenuStrip();
        var miRename = new ToolStripMenuItem("Rename...", null, (_, _) => RenamePreset());
        var miDelete = new ToolStripMenuItem("Delete", null, (_, _) => DeletePreset());
        var miDrop = new ToolStripMenuItem("Create drag-and-drop shortcut for this preset", null, (_, _) => CreateDropTarget());
        more.Items.AddRange(new ToolStripItem[]
        {
            miRename, miDelete, new ToolStripSeparator(), miDrop,
            new ToolStripMenuItem("Open presets folder", null, (_, _) =>
            {
                Directory.CreateDirectory(Presets.Dir);
                Process.Start("explorer.exe", $"\"{Presets.Dir}\"");
            }),
        });
        more.Opening += (_, _) =>
        {
            bool user = SelectedUserPreset() != null;
            miRename.Enabled = miDelete.Enabled = user;
            miDrop.Enabled = user || SelectedPreset() == Presets.LastRun;
        };
        btnMorePreset.Click += (_, _) => more.Show(btnMorePreset, new Point(0, btnMorePreset.Height));
        btnSavePreset.Click += (_, _) => SavePreset();
        cbPreset.SelectedIndexChanged += (_, _) =>
        {
            UpdatePresetButtons();
            if (!loadingPresets && SelectedPreset() is { } name) ApplyPreset(name);
        };

        var bar = Flow(
            new Label { Text = "Preset", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(0, 6, 8, 0) },
            cbPreset, btnSavePreset, Btn("Save as...", (_, _) => SavePresetAs(), 90), btnMorePreset,
            new Label { Text = "Choosing a preset loads its settings; Run also keeps them as \"Last run\".", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 7, 0, 0) });
        bar.Margin = new Padding(0, 0, 0, 4);
        return bar;
    }

    Control BuildLeft()
    {
        lvFiles.Columns.Add("File", 200);
        lvFiles.Columns.Add("Status", 300);
        fileButtons = Flow(
            Btn("Add files...", (_, _) => AddFilesDialog()),
            Btn("Add folder...", (_, _) => AddFolderDialog()),
            Btn("Remove", (_, _) => { foreach (ListViewItem i in lvFiles.SelectedItems) lvFiles.Items.Remove(i); }, 80),
            Btn("Clear", (_, _) => lvFiles.Items.Clear(), 70));

        var radios = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(14, 0, 0, 0) };
        radios.Controls.AddRange(new Control[] { rbSubs, rbEmbed, rbBurn });

        cbCodec.Items.AddRange(new object[] { "Same as source (recommended)", "H.264  -  most compatible", "HEVC / H.265  -  smaller files", "AV1  -  smallest; newer players" });
        cbQuality.Items.AddRange(new object[] { "Match source bitrate (recommended)", "Higher  -  1.5x source bitrate", "Smaller file  -  0.6x source bitrate" });
        cbParallel.Items.AddRange(new object[] { $"Automatic ({JobRunner.AutoParallel} at a time on this PC)", "1 at a time", "2 at a time", "3 at a time", "4 at a time" });
        encodePanel = FormTable(("Video codec", cbCodec), ("Quality", cbQuality), ("Encoder", cbEncoder), ("Parallel jobs", cbParallel));
        encodePanel.Margin = new Padding(34, 2, 0, 0);
        outputPanel = Column((radios, 0), (encodePanel, 0));

        cbFormat.Items.AddRange(new object[]
        {
            "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.ff", "yyyy-MM-dd HH:mm:ss", "HH:mm:ss.fff",
            "yyyy-MM-dd hh:mm:ss.fff tt", "MM/dd/yyyy hh:mm:ss.fff tt", "dd.MM.yyyy HH:mm:ss.fff", "ddd d MMM yyyy HH:mm:ss",
        });
        cbMarks.Items.AddRange(new object[] { "Start of recording  (verified on Sony FDR-AX43)", "End of recording" });
        cbZone.Items.Add(new Choice("Automatic  (camera's own offset if recorded, else this PC's zone)", ""));
        foreach (var z in TimeZoneInfo.GetSystemTimeZones()) cbZone.Items.Add(new Choice(z.DisplayName, z.Id));
        timePanel = FormTable(
            ("Label format", Flow(cbFormat, Lbl("HH=24h  hh=12h  tt=AM/PM  fff=ms"))),
            ("Embedded date is the", cbMarks),
            ("Time zone", cbZone));

        return Column(
            (Header("1. Videos  (drop files or folders here)"), 0),
            (lvFiles, 100),
            (fileButtons, 0),
            (Header("2. Output"), 0),
            (outputPanel, 0),
            (Header("4. Time"), 0),
            (timePanel, 0));
    }

    Control BuildRight()
    {
        grid.AutoGenerateColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = SystemColors.Window;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.Columns.AddRange(
            new DataGridViewCheckBoxColumn { Name = "On", DataPropertyName = nameof(Overlay.Enabled), HeaderText = "On", FillWeight = 7 },
            // A button with a menu rather than a combo box: the grid parks combo editors
            // off-screen, and WinForms then fails to rescale them when the window moves to
            // a monitor with different scaling ("Error creating window handle")
            new DataGridViewButtonColumn
            {
                Name = "Position", HeaderText = "Position", FillWeight = 22, FlatStyle = FlatStyle.Flat,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft },
            },
            new DataGridViewTextBoxColumn { Name = "Text", DataPropertyName = nameof(Overlay.Text), HeaderText = "Text  ({tokens} are filled in per frame)", FillWeight = 52 },
            new DataGridViewTextBoxColumn { Name = "Size", DataPropertyName = nameof(Overlay.Size), HeaderText = "Size %", FillWeight = 9 },
            new DataGridViewButtonColumn { Name = "Color", DataPropertyName = nameof(Overlay.Color), HeaderText = "Color", FillWeight = 12, FlatStyle = FlatStyle.Flat });
        grid.DataSource = overlays;

        var addMenu = new ContextMenuStrip();
        foreach (var p in OverlayPresets)
            addMenu.Items.Add($"{p.Name}   {p.Text}", null, (_, _) => AddOverlay(p.Text, p.Size));
        var tokenMenu = new ContextMenuStrip();
        foreach (var (token, desc) in Template.Tokens)
            tokenMenu.Items.Add($"{token}   {desc}", null, (_, _) => InsertToken(token));
        var btnAdd = new Button { Text = "Add overlay  \u25BE", Width = 120, Height = 28 };
        btnAdd.Click += (_, _) => addMenu.Show(btnAdd, new Point(0, btnAdd.Height));
        var btnToken = new Button { Text = "Insert token  \u25BE", Width = 120, Height = 28 };
        btnToken.Click += (_, _) => tokenMenu.Show(btnToken, new Point(0, btnToken.Height));
        var overlayButtons = Flow(btnAdd, btnToken,
            Btn("Remove", (_, _) => RemoveOverlay(), 80),
            Btn("Move up", (_, _) => MoveOverlay(-1), 80),
            Btn("Move down", (_, _) => MoveOverlay(1), 85));

        cbEdge.Items.AddRange(new object[] { "Outline", "Box behind text" });
        var style = FormTable(
            ("Font", Flow(cbFont, chkBold, chkEven)),
            ("Edge", Flow(cbEdge, btnEdgeColor, Lbl("Box opacity %"), numOpacity)));

        return Column(
            (Header("3. Text overlays  (several per position stack in list order)"), 0),
            (grid, 38),
            (overlayButtons, 0),
            (Header("Style"), 0),
            (style, 0),
            (Header("Preview"), 0),
            (preview, 62));
    }

    Control BuildBottom()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        for (int i = 0; i < 4; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lblStatus.AutoEllipsis = true;
        lblStatus.AutoSize = false;
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        t.Controls.AddRange(new Control[] { lblStatus, progress, lnkLog, lnkOpen, btnCancel, btnRun });
        return t;
    }

    // --- settings <-> controls ---------------------------------------------------

    void LoadUi()
    {
        rbSubs.Checked = settings.Mode == OutputMode.Subs;
        rbEmbed.Checked = settings.Mode == OutputMode.Embed;
        rbBurn.Checked = settings.Mode == OutputMode.Burn;
        cbCodec.SelectedIndex = (int)settings.Codec;
        cbQuality.SelectedIndex = (int)settings.Quality;
        cbParallel.SelectedIndex = Math.Clamp(settings.ParallelJobs, 0, 4);
        FillEncoders(settings.Encoder);

        SelectFont(settings.FontName);
        chkBold.Checked = settings.Bold;
        cbEdge.SelectedIndex = (int)settings.Edge;
        SetColorButton(btnEdgeColor, edgeColor);
        numOpacity.Value = Math.Clamp(settings.BoxOpacity, 0, 100);

        cbFormat.Text = settings.DateFormat;
        cbMarks.SelectedIndex = (int)settings.CreationMarks;
        cbZone.SelectedIndex = Math.Max(0, cbZone.Items.Cast<Choice>().ToList().FindIndex(c => c.Value == settings.TimeZoneId));
        preview.Settings = CurrentSettings;
        UpdateEnabled();
    }

    AppSettings CurrentSettings()
    {
        var s = settings.Clone();
        s.Mode = rbBurn.Checked ? OutputMode.Burn : rbEmbed.Checked ? OutputMode.Embed : OutputMode.Subs;
        s.Overlays = overlays.Select(o => o.Clone()).ToList();
        s.FontName = cbFont.SelectedItem as string ?? settings.FontName;
        s.Bold = chkBold.Checked;
        s.Edge = (EdgeStyle)Math.Max(0, cbEdge.SelectedIndex);
        s.EdgeColor = edgeColor;
        s.BoxOpacity = (int)numOpacity.Value;
        s.DateFormat = cbFormat.Text;
        s.CreationMarks = cbMarks.SelectedIndex == 1 ? CreationMarks.End : CreationMarks.Start;
        s.TimeZoneId = (cbZone.SelectedItem as Choice)?.Value ?? "";
        s.Codec = (CodecChoice)Math.Max(0, cbCodec.SelectedIndex);
        s.Quality = (QualityChoice)Math.Max(0, cbQuality.SelectedIndex);
        s.Encoder = (cbEncoder.SelectedItem as Choice)?.Value ?? settings.Encoder;
        s.ParallelJobs = Math.Max(0, cbParallel.SelectedIndex);
        return s;
    }

    void FillEncoders(string? wanted = null)
    {
        wanted ??= (cbEncoder.SelectedItem as Choice)?.Value ?? settings.Encoder;
        cbEncoder.Items.Clear();
        if (workers == null)
        {
            cbEncoder.Items.Add(new Choice("Automatic  (checking this PC's GPUs...)", "auto"));
        }
        else
        {
            var gpus = workers.Where(w => w.Vendor != Vendor.Cpu).ToList();
            cbEncoder.Items.Add(new Choice(gpus.Count > 0 ? $"Automatic  -  share across {gpus.Count} GPU encoder{(gpus.Count > 1 ? "s" : "")}" : "Automatic  -  no GPU encoder found, uses CPU", "auto"));
            foreach (var g in gpus) cbEncoder.Items.Add(new Choice(g.Name, g.Id));
            cbEncoder.Items.Add(new Choice("CPU only  -  slowest, works everywhere", "cpu"));
        }
        var items = cbEncoder.Items.Cast<Choice>().ToList();
        cbEncoder.SelectedIndex = Math.Max(0, items.FindIndex(c => c.Value == wanted));
    }

    // --- events ------------------------------------------------------------------

    void WireEvents()
    {
        foreach (var c in new Control[] { this, lvFiles })
        {
            c.DragEnter += (_, e) => { if (!running && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
            c.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] p) AddPaths(p); };
        }
        lvFiles.Resize += (_, _) => SizeColumns();
        SizeColumns();

        foreach (var rb in new[] { rbSubs, rbEmbed, rbBurn }) rb.CheckedChanged += (_, _) => UpdateEnabled();

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell?.OwningColumn?.Name == "On")
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, _) => preview.Invalidate();
        grid.DataError += (_, e) => e.ThrowException = false;
        grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is TextBox tb)
            {
                tb.TextChanged -= GridTextChanged;
                tb.TextChanged += GridTextChanged;
            }
        };
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < overlays.Count && grid.Columns[e.ColumnIndex].Name == "Position")
            {
                var pos = overlays[e.RowIndex].Position;
                e.Value = (Positions.FirstOrDefault(p => p.Value == pos)?.Name ?? pos.ToString()) + "  \u25BE";
                e.FormattingApplied = true;
            }
            if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "Color" && e.CellStyle != null)
            {
                var c = ColorUtil.Parse(e.Value as string);
                e.CellStyle.BackColor = e.CellStyle.SelectionBackColor = c;
                e.CellStyle.ForeColor = e.CellStyle.SelectionForeColor = ColorUtil.Contrasting(c);
            }
        };
        // Any click in the cell (CellContentClick misses some clicks), or Space/Enter
        grid.CellClick += (_, e) => CellButton(e.RowIndex, e.ColumnIndex);
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Space or Keys.Enter && grid.CurrentCell is { } c && CellButton(c.RowIndex, c.ColumnIndex))
                e.Handled = true;
        };

        cbFont.SelectedIndexChanged += (_, _) => preview.Invalidate();
        chkBold.CheckedChanged += (_, _) => preview.Invalidate();
        chkEven.CheckedChanged += (_, _) => FillFonts();
        cbEdge.SelectedIndexChanged += (_, _) => { numOpacity.Enabled = cbEdge.SelectedIndex == 1; preview.Invalidate(); };
        numOpacity.ValueChanged += (_, _) => preview.Invalidate();
        cbFormat.TextChanged += (_, _) => preview.Invalidate();
        btnEdgeColor.Click += (_, _) =>
        {
            if (PickColor(edgeColor) is { } hex)
            {
                edgeColor = hex;
                SetColorButton(btnEdgeColor, hex);
                preview.Invalidate();
            }
        };
        numOpacity.Enabled = cbEdge.SelectedIndex == 1;

        btnRun.Click += async (_, _) => await RunAsync(fromSetup: false);
        btnCancel.Click += (_, _) => { if (running) cts?.Cancel(); else Close(); };
        lnkOpen.LinkClicked += (_, _) =>
        {
            if (lastOutput != null && File.Exists(lastOutput))
                Process.Start("explorer.exe", $"/select,\"{lastOutput}\"");
        };
        lnkLog.LinkClicked += (_, _) =>
        {
            if (File.Exists(logPath)) Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        };
    }

    void GridTextChanged(object? sender, EventArgs e)
    {
        // Live preview while typing
        if (sender is TextBox tb && grid.CurrentCell is { } cell && cell.RowIndex < overlays.Count && cell.OwningColumn?.Name == "Text")
        {
            overlays[cell.RowIndex].Text = tb.Text;
            preview.Invalidate();
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startupWarning != null) Warn(startupWarning);
        setupTask = SetupAsync();
        await setupTask;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (running)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Stop the videos that are still being processed?", "Video Timecode",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                closeWhenDone = true;
                cts?.Cancel();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    // --- setup: fonts, ffmpeg, encoders ------------------------------------------

    async Task SetupAsync()
    {
        _ = LoadFontsAsync();
        if (!Ffmpeg.Locate() && !await DownloadFfmpegAsync())
        {
            SetStatus("FFmpeg is needed to read videos. Click Run to download it.");
            return;
        }
        await DetectEncodersAsync();
        if (quickRun) await RunAsync(fromSetup: true);
    }

    async Task<bool> DownloadFfmpegAsync()
    {
        var answer = MessageBox.Show(this,
            "Video Timecode uses FFmpeg (free and open source) to read and write video, and it isn't on this PC yet.\n\n" +
            "Download it now? About 85 MB from github.com/BtbN/FFmpeg-Builds. It is checked against its published " +
            "checksum and kept in your user profile, so this only happens once.",
            "Download FFmpeg", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return false;

        btnRun.Enabled = false;
        try
        {
            var p = new Progress<(string Message, double Fraction)>(x =>
            {
                SetStatus(x.Message);
                progress.Value = (int)(Math.Clamp(x.Fraction, 0, 1) * progress.Maximum);
            });
            await Task.Run(() => Ffmpeg.DownloadAsync(p, CancellationToken.None));
            SetStatus("FFmpeg installed.");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Downloading FFmpeg failed:\n\n" + ex.Message, "Video Timecode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            btnRun.Enabled = !running;
            progress.Value = 0;
        }
    }

    async Task DetectEncodersAsync()
    {
        SetStatus("Checking this PC's GPU encoders...");
        try
        {
            workers = await Task.Run(() => EncoderCaps.DetectAsync(m => UI(() => SetStatus(m)), CancellationToken.None));
        }
        catch (Exception ex)
        {
            workers = new() { EncoderCaps.Cpu() };
            SetStatus("GPU check failed, burn-in will use the CPU: " + ex.Message);
            FillEncoders();
            return;
        }
        FillEncoders();
        var gpus = workers.Where(w => w.Vendor != Vendor.Cpu).Select(w => w.Name).ToList();
        SetStatus(gpus.Count > 0 ? "Ready.  GPU encoders: " + string.Join(", ", gpus) : "Ready.  No GPU encoder found; burn-in will use the CPU.");
    }

    async Task LoadFontsAsync()
    {
        var (all, even) = await Task.Run(() =>
        {
            var names = new InstalledFontCollection().Families.Select(f => f.Name).ToList();
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            var evenNames = names.Where(n => EvenDigits(g, n)).ToList();
            return (names, evenNames);
        });
        allFonts = all;
        evenFonts = even;
        SelectFont(cbFont.SelectedItem as string ?? settings.FontName);
    }

    // Selects a font, showing all fonts if it isn't one with even-width digits
    void SelectFont(string name)
    {
        if (allFonts.Count == 0)
        {
            // Font list still loading: show just this one for now
            cbFont.Items.Clear();
            cbFont.Items.Add(name);
            cbFont.SelectedIndex = 0;
            return;
        }
        if (!evenFonts.Contains(name) && allFonts.Contains(name) && chkEven.Checked)
        {
            cbFont.Items.Clear();   // so the CheckedChanged refill keeps this font, not the old one
            cbFont.Items.Add(name);
            cbFont.SelectedIndex = 0;
            chkEven.Checked = false;
        }
        FillFonts(name);
    }

    // Fonts whose digits all share one width don't shift as the time ticks over
    static bool EvenDigits(Graphics g, string family)
    {
        try
        {
            using var f = new Font(family, 20, GraphicsUnit.Pixel);
            var widths = "0123456789".Select(d => TextRenderer.MeasureText(g, new string(d, 10), f).Width).ToList();
            return widths.Max() - widths.Min() <= 1;
        }
        catch { return false; }
    }

    void FillFonts(string? keep = null)
    {
        if (allFonts.Count == 0) return;
        keep ??= cbFont.SelectedItem as string ?? settings.FontName;
        cbFont.BeginUpdate();
        cbFont.Items.Clear();
        foreach (var n in chkEven.Checked ? evenFonts : allFonts) cbFont.Items.Add(n);
        int i = cbFont.Items.IndexOf(keep);
        if (i < 0) i = cbFont.Items.IndexOf("Consolas");
        cbFont.SelectedIndex = Math.Max(0, i);
        cbFont.EndUpdate();
    }

    // --- running -----------------------------------------------------------------

    async Task RunAsync(bool fromSetup)
    {
        if (running) return;
        grid.EndEdit();
        var s = CurrentSettings();

        if (lvFiles.Items.Count == 0) { Warn("Add at least one video first."); return; }
        if (!s.Overlays.Any(o => o.Enabled && !string.IsNullOrWhiteSpace(o.Text))) { Warn("Add at least one text overlay."); return; }
        try { _ = DateTime.Now.ToString(s.DateFormat, CultureInfo.InvariantCulture); }
        catch (FormatException) { Warn("The label format isn't a valid date format."); return; }

        if (!fromSetup && setupTask is { IsCompleted: false })
        {
            SetStatus("Getting ready...");
            await setupTask;
        }
        if (!Ffmpeg.Ready && !Ffmpeg.Locate() && !await DownloadFfmpegAsync()) return;
        if (workers == null) await DetectEncodersAsync();

        try { s.Save(); } catch { }
        running = true;
        UpdateEnabled();
        btnCancel.Text = "Cancel";
        lnkOpen.Visible = lnkLog.Visible = false;
        progress.Value = 0;
        SetStatus($"Working on {lvFiles.Items.Count} video{(lvFiles.Items.Count == 1 ? "" : "s")}...");
        cts = new CancellationTokenSource();

        var jobs = lvFiles.Items.Cast<ListViewItem>().Select(i => new Job((string)i.Tag!, i)).ToList();
        jobProgress.Clear();
        foreach (var w in workers!) w.Active = 0;
        foreach (ListViewItem i in lvFiles.Items) SetItem(i, "Queued", null);

        var sw = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            using var log = new StreamWriter(logPath, false) { AutoFlush = true };
            var logLock = new object();
            log.WriteLine($"Video Timecode {typeof(Program).Assembly.GetName().Version} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.WriteLine($"Mode {s.Mode}; encoders: {string.Join(", ", workers.Select(w => $"{w.Name} [{string.Join(' ', w.Caps)}]"))}");
            var runner = new JobRunner(s, workers, (job, text, f) => UI(() => UpdateJob(job, text, f, jobs)),
                line => { lock (logLock) log.WriteLine(line); });
            await Task.Run(() => runner.RunAsync(jobs, cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Video Timecode", MessageBoxButtons.OK, MessageBoxIcon.Error); }

        running = false;
        UpdateEnabled();
        btnCancel.Text = "Close";
        int ok = jobs.Count(j => j.Succeeded), failed = jobs.Count - ok;
        if (cts.IsCancellationRequested)
        {
            foreach (var j in jobs.Where(j => !j.Succeeded && !j.Failed)) SetItem((ListViewItem)j.Tag, "Cancelled", Color.DimGray);
            SetStatus("Cancelled.");
        }
        else
        {
            progress.Value = progress.Maximum;
            SetStatus($"Finished in {sw.Elapsed:m\\:ss}:  {ok} done" + (failed > 0 ? $",  {failed} failed" : "."));
        }
        lnkLog.Visible = File.Exists(logPath);
        var firstDone = jobs.FirstOrDefault(j => j.Succeeded);
        lastOutput = firstDone == null ? null : s.Mode == OutputMode.Subs ? firstDone.AssPath : firstDone.Output;
        lnkOpen.Visible = lastOutput != null;
        if (closeWhenDone) Close();
    }

    void UpdateJob(Job job, string text, double? fraction, List<Job> jobs)
    {
        var color = text.StartsWith("Failed") ? Color.Firebrick : text.StartsWith("Done") ? Color.DarkGreen : SystemColors.WindowText;
        SetItem((ListViewItem)job.Tag, text, color);
        if (fraction is { } f) jobProgress[job] = f;
        double total = jobs.Sum(j => j.Succeeded || j.Failed ? 1 : jobProgress.GetValueOrDefault(j));
        progress.Value = (int)(Math.Clamp(total / Math.Max(1, jobs.Count), 0, 1) * progress.Maximum);
    }

    static void SetItem(ListViewItem item, string status, Color? color)
    {
        while (item.SubItems.Count < 2) item.SubItems.Add("");
        item.SubItems[1].Text = status;
        if (color is { } c) item.SubItems[1].ForeColor = c;
    }

    void UpdateEnabled()
    {
        bool idle = !running;
        fileButtons.Enabled = idle;
        presetBar.Enabled = idle;
        outputPanel.Enabled = idle;
        timePanel.Enabled = idle;
        rightPanel.Enabled = idle;
        encodePanel.Enabled = idle && rbBurn.Checked;
        btnRun.Enabled = idle;
    }

    // --- files -------------------------------------------------------------------

    void AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(lvFiles.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!), StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            string p;
            try { p = Path.GetFullPath(raw); } catch { continue; }
            IEnumerable<string> files =
                Directory.Exists(p)
                    ? Directory.GetFiles(p).Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_timecode", StringComparison.OrdinalIgnoreCase)
                                 && !Path.GetFileNameWithoutExtension(f).EndsWith("_timecode_burned", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    : File.Exists(p) ? new[] { p } : Array.Empty<string>();
            foreach (var f in files)
            {
                if (!existing.Add(f)) continue;
                var item = new ListViewItem(Path.GetFileName(f)) { Tag = f, ToolTipText = f, UseItemStyleForSubItems = false };
                item.SubItems.Add("");
                lvFiles.Items.Add(item);
            }
        }
    }

    void AddFilesDialog()
    {
        using var d = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Videos|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
    }

    void AddFolderDialog()
    {
        using var d = new FolderBrowserDialog { Description = "Add every video in this folder" };
        if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath });
    }

    void SizeColumns()
    {
        int w = lvFiles.ClientSize.Width;
        if (w <= 0) return;
        lvFiles.Columns[0].Width = (int)(w * 0.42);
        lvFiles.Columns[1].Width = w - lvFiles.Columns[0].Width - 4;
    }

    // --- overlays ----------------------------------------------------------------

    void AddOverlay(string text, double size)
    {
        grid.EndEdit();
        var used = overlays.Select(o => o.Position).ToHashSet();
        var pos = FreePositionOrder.FirstOrDefault(p => !used.Contains(p), Anchor9.BottomLeft);
        overlays.Add(new Overlay { Text = text, Size = size, Position = pos, Color = "#FFFFFF" });
        grid.CurrentCell = grid.Rows[overlays.Count - 1].Cells["Text"];
        preview.Invalidate();
    }

    // Position and Color cells act as buttons; returns true if one was handled
    bool CellButton(int row, int col)
    {
        if (row < 0 || row >= overlays.Count || col < 0) return false;
        switch (grid.Columns[col].Name)
        {
            case "Position":
                // After the grid finishes handling the click, or it takes focus back and closes the menu
                BeginInvoke(() => ShowPositionMenu(row, col));
                return true;
            case "Color":
                var o = overlays[row];
                if (PickColor(o.Color) is { } hex)
                {
                    o.Color = hex;
                    grid.InvalidateRow(row);
                    preview.Invalidate();
                }
                return true;
            default:
                return false;
        }
    }

    void ShowPositionMenu(int row, int col)
    {
        var o = overlays[row];
        var menu = new ContextMenuStrip();
        foreach (var p in Positions)
        {
            var item = new ToolStripMenuItem(p.Name) { Checked = p.Value == o.Position };
            item.Click += (_, _) =>
            {
                o.Position = p.Value;
                grid.InvalidateRow(row);
                preview.Invalidate();
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var r = grid.GetCellDisplayRectangle(col, row, false);
        menu.Show(grid, new Point(r.Left, r.Bottom));
    }

    void InsertToken(string token)
    {
        int row = grid.CurrentCell?.RowIndex ?? -1;
        if (row < 0 || row >= overlays.Count) { AddOverlay(token, 4); return; }
        grid.EndEdit();
        overlays[row].Text += token;
        grid.InvalidateRow(row);
        preview.Invalidate();
    }

    void RemoveOverlay()
    {
        int row = grid.CurrentCell?.RowIndex ?? -1;
        if (row < 0 || row >= overlays.Count) return;
        grid.EndEdit();
        overlays.RemoveAt(row);
        preview.Invalidate();
    }

    void MoveOverlay(int delta)
    {
        int row = grid.CurrentCell?.RowIndex ?? -1, to = row + delta;
        if (row < 0 || to < 0 || to >= overlays.Count) return;
        grid.EndEdit();
        var o = overlays[row];
        overlays.RemoveAt(row);
        overlays.Insert(to, o);
        grid.CurrentCell = grid.Rows[to].Cells["Text"];
        preview.Invalidate();
    }

    // --- presets ----------------------------------------------------------------

    string? SelectedPreset() => (cbPreset.SelectedItem as Choice)?.Value;

    string? SelectedUserPreset() => SelectedPreset() is { } n && n != Presets.LastRun && n != Presets.Defaults ? n : null;

    // Refills the list and selects a preset without loading it again
    void RefreshPresets(string select)
    {
        loadingPresets = true;
        cbPreset.Items.Clear();
        cbPreset.Items.Add(new Choice("Last run", Presets.LastRun));
        cbPreset.Items.Add(new Choice("Defaults  (factory settings)", Presets.Defaults));
        foreach (var n in Presets.Names()) cbPreset.Items.Add(new Choice(n, n));
        var items = cbPreset.Items.Cast<Choice>().ToList();
        cbPreset.SelectedIndex = Math.Max(0, items.FindIndex(c => c.Value.Equals(select, StringComparison.OrdinalIgnoreCase)));
        loadingPresets = false;
        UpdatePresetButtons();
    }

    void UpdatePresetButtons() => btnSavePreset.Enabled = SelectedUserPreset() != null;

    void ApplyPreset(string name)
    {
        if (Presets.Load(name) is not { } s)
        {
            Warn($"The preset \"{name}\" could not be read.");
            return;
        }
        grid.EndEdit();
        settings = s.Clone();
        overlays.Clear();
        foreach (var o in settings.Overlays) overlays.Add(o.Clone());
        edgeColor = settings.EdgeColor;
        LoadUi();
        SetStatus(name switch
        {
            Presets.LastRun => "Loaded the last run's settings.",
            Presets.Defaults => "Loaded the default settings.",
            _ => $"Loaded preset \"{name}\".",
        });
    }

    void SavePreset()
    {
        if (SelectedUserPreset() is not { } name) return;
        grid.EndEdit();
        Presets.Save(name, CurrentSettings());
        SetStatus($"Saved preset \"{name}\".");
    }

    void SavePresetAs()
    {
        grid.EndEdit();
        var name = SelectedUserPreset() ?? "";
        while (true)
        {
            name = Prompt("Save preset", "Name for this preset:", name)?.Trim();
            if (name == null) return;
            if (Presets.Problem(name) is { } problem) { Warn(problem); continue; }
            if (Presets.Exists(name) && MessageBox.Show(this, $"Replace the existing preset \"{name}\"?", "Save preset",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) continue;
            break;
        }
        Presets.Save(name, CurrentSettings());
        RefreshPresets(name);
        SetStatus($"Saved preset \"{name}\".");
    }

    void RenamePreset()
    {
        if (SelectedUserPreset() is not { } from) return;
        var to = from;
        while (true)
        {
            to = Prompt("Rename preset", $"New name for \"{from}\":", to)?.Trim();
            if (to == null || to == from) return;
            if (Presets.Problem(to) is { } problem) { Warn(problem); continue; }
            if (Presets.Exists(to) && !to.Equals(from, StringComparison.OrdinalIgnoreCase)) { Warn($"There is already a preset called \"{to}\"."); continue; }
            break;
        }
        Presets.Rename(from, to);
        // Keep its drag-and-drop shortcut working
        if (File.Exists(DropTargetPath(from)))
        {
            try { File.Delete(DropTargetPath(from)); WriteDropTarget(to); } catch { }
        }
        RefreshPresets(to);
        SetStatus($"Renamed preset to \"{to}\".");
    }

    void DeletePreset()
    {
        if (SelectedUserPreset() is not { } name) return;
        bool hasShortcut = File.Exists(DropTargetPath(name));
        if (MessageBox.Show(this, $"Delete the preset \"{name}\"?" + (hasShortcut ? "\n\nIts drag-and-drop shortcut will be deleted too." : ""),
                "Delete preset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Presets.Delete(name);
        if (hasShortcut) try { File.Delete(DropTargetPath(name)); } catch { }
        RefreshPresets(Presets.LastRun);
        SetStatus($"Deleted preset \"{name}\". The current settings are unchanged.");
    }

    // A .bat next to the exe: dropping videos on it runs them with this preset
    void CreateDropTarget()
    {
        var name = SelectedPreset();
        if (name == null || name == Presets.Defaults) return;
        try
        {
            var path = WriteDropTarget(name);
            SetStatus($"Created \"{Path.GetFileName(path)}\" next to the app. Drop videos on it to run this preset.");
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Warn("Couldn't create the shortcut next to the app (is it in a protected folder?):\n\n" + ex.Message);
        }
    }

    static string DropTargetPath(string name) => Path.Combine(AppContext.BaseDirectory,
        name == Presets.LastRun ? "Video Timecode - use last settings.bat" : $"Video Timecode - {name}.bat");

    static string WriteDropTarget(string name)
    {
        var path = DropTargetPath(name);
        var args = name == Presets.LastRun ? "--use-saved" : $"--preset \"{name}\"";
        File.WriteAllText(path,
            "@echo off\r\n" +
            $"rem Drop videos on this file to run them with the Video Timecode {(name == Presets.LastRun ? "last run's settings" : $"\"{name}\" preset")}\r\n" +
            $"start \"\" \"%~dp0VideoTimecode.exe\" {args} %*\r\n");
        return path;
    }

    // A minimal text prompt; returns null if cancelled
    string? Prompt(string title, string label, string value)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = Font, AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi, Padding = new Padding(12),
        };
        var tb = new TextBox { Text = value, Width = 340 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 3, 3, 6) });
        t.Controls.Add(tb);
        var buttons = Flow(ok, cancel);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 10, 0, 0);
        t.Controls.Add(buttons);
        f.Controls.Add(t);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Shown += (_, _) => tb.SelectAll();
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }

    // --- helpers -----------------------------------------------------------------

    string? PickColor(string current)
    {
        using var d = new ColorDialog { FullOpen = true, Color = ColorUtil.Parse(current) };
        return d.ShowDialog(this) == DialogResult.OK ? ColorUtil.ToHex(d.Color) : null;
    }

    static void SetColorButton(Button b, string hex)
    {
        var c = ColorUtil.Parse(hex);
        b.BackColor = c;
        b.ForeColor = ColorUtil.Contrasting(c);
        b.Text = ColorUtil.ToHex(c);
    }

    void SetStatus(string text) => lblStatus.Text = text;

    void Warn(string text) => MessageBox.Show(this, text, "Video Timecode", MessageBoxButtons.OK, MessageBoxIcon.Information);

    void UI(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(a);
        else a();
    }
}
