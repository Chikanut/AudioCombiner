using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace AudioCombiner;

sealed class DeviceItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsLoopback { get; init; }
    public override string ToString() => (IsLoopback ? "[Вихід, перехоплення]  " : "[Вхід]  ") + Name;
}

public sealed class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "AudioCombiner";
    const string VbCableUrl = "https://vb-audio.com/Cable/";

    readonly MMDeviceEnumerator enumerator = new();
    readonly DeviceWatcher watcher = new();
    readonly AudioEngine engine;
    readonly AppSettings settings = AppSettings.Load();
    readonly List<TargetRow> rows = new();

    readonly ComboBox sourceBox;
    readonly FlowLayoutPanel targetsPanel;
    readonly ProgressBar levelBar;
    readonly Label sourceStatus, statusLabel;
    readonly Button startButton;
    readonly CheckBox metronomeBox, autoRelayBox, autostartBox, updatesBox;
    readonly LinkLabel updateLink;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem trayToggle, trayUpdate;
    readonly System.Windows.Forms.Timer uiTimer, deviceDebounce, updateTimer;
    readonly Icon iconIdle, iconActive;
    readonly EventWaitHandle showSignal;

    bool allowShow, reallyClose, loadingUi, settingsDirty, balloonShown, updateBusy;
    UpdateInfo? update;
    float levelDisplay;
    int tick;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public MainForm(bool startHidden, bool startRelay = false)
    {
        allowShow = !startHidden;
        engine = new AudioEngine(enumerator);

        iconIdle = MakeIcon(Color.FromArgb(115, 115, 125));
        iconActive = MakeIcon(Color.FromArgb(0, 140, 90));

        Text = "Audio Combiner — ретрансляція звуку";
        Icon = iconActive;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(S(700), S(720));
        MinimumSize = new Size(S(560), S(520));
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(12)) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        void AddRow(Control c, bool fill = false)
        {
            root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c, 0, root.RowStyles.Count - 1);
        }

        Label Header(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Margin = new Padding(0, S(6), 0, S(4)) };
            l.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
            return l;
        }

        // --- Джерело ---
        AddRow(Header("1. Звідки брати звук"));

        var srcLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = Padding.Empty };
        srcLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srcLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        srcLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        var refreshBtn = new Button { Text = "Оновити", AutoSize = true };
        var soundBtn = new Button { Text = "Звук Windows…", AutoSize = true };
        srcLine.Controls.Add(sourceBox, 0, 0);
        srcLine.Controls.Add(refreshBtn, 1, 0);
        srcLine.Controls.Add(soundBtn, 2, 0);
        AddRow(srcLine);

        var levelLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, S(4), 0, 0) };
        levelLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(160)));
        levelLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        levelBar = new ProgressBar { Height = S(12), Anchor = AnchorStyles.Left | AnchorStyles.Right, Maximum = 100 };
        sourceStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
        levelLine.Controls.Add(levelBar, 0, 0);
        levelLine.Controls.Add(sourceStatus, 1, 0);
        AddRow(levelLine);

        const string hint =
            "Найкраще для двох гарнітур: встановіть безкоштовний VB-Cable, у «Звук Windows» зробіть «CABLE Input» " +
            "пристроєм за замовчуванням, а тут виберіть «[Вхід] CABLE Output». Тоді обидві гарнітури — рівноправні цілі. " +
            "Без VB-Cable можна перехоплювати вихід, який ви не слухаєте (наприклад, HDMI-монітор).";
        var hintLabel = new LinkLabel
        {
            Text = hint,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, S(6), 0, S(4)),
        };
        hintLabel.LinkArea = new LinkArea(hint.IndexOf("VB-Cable", StringComparison.Ordinal), "VB-Cable".Length);
        hintLabel.LinkClicked += (_, _) => OpenShell(VbCableUrl);
        AddRow(hintLabel);

        // --- Цілі ---
        AddRow(Header("2. Куди ретранслювати"));
        targetsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty,
        };
        targetsPanel.Resize += (_, _) => FitRows();
        AddRow(targetsPanel, fill: true);

        // --- Опції ---
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
        metronomeBox = new CheckBox { Text = "Метроном (клац раз на секунду) — щоб підігнати затримку на слух", AutoSize = true };
        autoRelayBox = new CheckBox { Text = "Починати ретрансляцію одразу після запуску програми", AutoSize = true, Checked = settings.AutoStartRelay };
        autostartBox = new CheckBox { Text = "Запускати разом з Windows (згорнутою в трей)", AutoSize = true, Checked = IsAutostartEnabled() };
        updatesBox = new CheckBox { Text = "Автоматично перевіряти оновлення", AutoSize = true, Checked = settings.CheckUpdates };
        opts.Controls.AddRange([metronomeBox, autoRelayBox, autostartBox, updatesBox]);
        AddRow(opts);

        updateLink = new LinkLabel { AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
        updateLink.LinkClicked += (_, _) => _ = OnUpdateClicked();
        AddRow(updateLink);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, S(8), 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        startButton = new Button { Text = "Старт", Size = new Size(S(150), S(40)) };
        startButton.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        bottom.Controls.Add(statusLabel, 0, 0);
        bottom.Controls.Add(startButton, 1, 0);
        AddRow(bottom);

        // --- Трей ---
        var menu = new ContextMenuStrip();
        menu.Items.Add("Відкрити", null, (_, _) => ShowFromTray());
        trayToggle = new ToolStripMenuItem("Старт", null, (_, _) => ToggleRelay());
        menu.Items.Add(trayToggle);
        trayUpdate = new ToolStripMenuItem("Оновити", null, (_, _) => _ = OnUpdateClicked()) { Visible = false };
        menu.Items.Add(trayUpdate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Вийти", null, (_, _) => ExitApp());
        tray = new NotifyIcon { Icon = iconIdle, Text = "Audio Combiner", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        SetUpdateText();

        // --- Події ---
        sourceBox.SelectedIndexChanged += (_, _) => OnSourceChanged();
        refreshBtn.Click += (_, _) => { RefreshDevices(); PushTargets(); };
        soundBtn.Click += (_, _) => OpenShell("mmsys.cpl");
        startButton.Click += (_, _) => ToggleRelay();
        metronomeBox.CheckedChanged += (_, _) => engine.Metronome = metronomeBox.Checked;
        autoRelayBox.CheckedChanged += (_, _) => { settings.AutoStartRelay = autoRelayBox.Checked; settingsDirty = true; };
        autostartBox.CheckedChanged += (_, _) => SetAutostart(autostartBox.Checked);
        updatesBox.CheckedChanged += (_, _) =>
        {
            settings.CheckUpdates = updatesBox.Checked;
            settingsDirty = true;
            if (updatesBox.Checked) _ = CheckForUpdates(manual: false);
        };

        uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
        uiTimer.Tick += (_, _) => OnUiTick();
        deviceDebounce = new System.Windows.Forms.Timer { Interval = 1200 };
        deviceDebounce.Tick += (_, _) =>
        {
            deviceDebounce.Stop();
            RefreshDevices();
            PushTargets();
        };

        watcher.Changed += () =>
        {
            if (IsHandleCreated)
                BeginInvoke(() => { deviceDebounce.Stop(); deviceDebounce.Start(); });
        };
        try { enumerator.RegisterEndpointNotificationCallback(watcher); } catch { }

        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
        new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                if (reallyClose) return;
                if (IsHandleCreated) BeginInvoke(ShowFromTray);
            }
        }) { IsBackground = true }.Start();

        // Перша перевірка — за кілька секунд після запуску, далі раз на 6 годин
        updateTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
            if (settings.CheckUpdates) _ = CheckForUpdates(manual: false);
        };

        RefreshDevices();
        uiTimer.Start();
        updateTimer.Start();
        UpdateRunningUi();

        if ((startRelay || settings.AutoStartRelay) && sourceBox.SelectedItem is DeviceItem)
            StartRelay();
    }

    // ---------- Пристрої ----------

    void RefreshDevices()
    {
        loadingUi = true;
        try
        {
            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

            sourceBox.BeginUpdate();
            sourceBox.Items.Clear();
            var items = new List<DeviceItem>();
            foreach (var c in captures) items.Add(new DeviceItem { Id = c.ID, Name = c.FriendlyName, IsLoopback = false });
            foreach (var r in renders) items.Add(new DeviceItem { Id = r.ID, Name = r.FriendlyName, IsLoopback = true });
            // VB-Cable — першим у списку
            items = items.OrderByDescending(i => !i.IsLoopback && IsCableOutput(i.Name)).ToList();

            DeviceItem? selected = null;
            if (settings.SourceId != null)
            {
                selected = items.FirstOrDefault(i => i.Id == settings.SourceId && i.IsLoopback == settings.SourceIsLoopback);
                if (selected == null)
                {
                    selected = new DeviceItem { Id = settings.SourceId, Name = settings.SourceName + "  (не підключено)", IsLoopback = settings.SourceIsLoopback };
                    items.Insert(0, selected);
                }
            }
            else
            {
                selected = items.FirstOrDefault(i => !i.IsLoopback && IsCableOutput(i.Name));
                if (selected != null) RememberSource(selected);
            }

            foreach (var i in items) sourceBox.Items.Add(i);
            sourceBox.SelectedItem = selected;
            sourceBox.EndUpdate();

            targetsPanel.SuspendLayout();
            foreach (var r in rows) r.Dispose();
            rows.Clear();
            targetsPanel.Controls.Clear();
            foreach (var d in renders)
            {
                var row = new TargetRow(d.ID, d.FriendlyName, GetWarning(d), settings.GetTarget(d.ID, d.FriendlyName), S);
                row.SettingsChanged += OnRowChanged;
                rows.Add(row);
                targetsPanel.Controls.Add(row);
            }
            if (rows.Count == 0)
                targetsPanel.Controls.Add(new Label { Text = "Немає активних пристроїв виводу. Підключіть навушники і натисніть «Оновити».", AutoSize = true });
            FitRows();
            targetsPanel.ResumeLayout();
        }
        finally
        {
            loadingUi = false;
        }
        UpdateRowAvailability();
    }

    static bool IsCableOutput(string name) => name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase);

    static string? GetWarning(MMDevice d)
    {
        int rate = 0;
        try { using var ac = d.AudioClient; rate = ac.MixFormat.SampleRate; } catch { }
        if (d.FriendlyName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) || (rate > 0 && rate <= 16000))
            return "режим гарнітури (Hands-Free) — якість як у телефоні; краще вибрати стерео-пристрій цієї ж гарнітури";
        return null;
    }

    void FitRows()
    {
        int w = targetsPanel.ClientSize.Width - S(2);
        foreach (var r in rows) r.Width = Math.Max(S(300), w);
    }

    void UpdateRowAvailability()
    {
        var src = sourceBox.SelectedItem as DeviceItem;
        foreach (var r in rows)
        {
            string? reason = null;
            if (src != null && src.IsLoopback && r.DeviceId == src.Id)
                reason = "це джерело — сюди звук і так іде";
            else if (src != null && !src.IsLoopback && IsCableOutput(src.Name) && r.DeviceName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                reason = "це вхід віртуального кабелю — вибір дав би петлю";
            r.SetBlocked(reason);
        }
    }

    void RememberSource(DeviceItem src)
    {
        settings.SourceId = src.Id;
        settings.SourceName = src.Name.Replace("  (не підключено)", "");
        settings.SourceIsLoopback = src.IsLoopback;
        settingsDirty = true;
    }

    void OnSourceChanged()
    {
        if (loadingUi) return;
        if (sourceBox.SelectedItem is not DeviceItem src) return;
        RememberSource(src);
        UpdateRowAvailability();
        if (engine.Running) StartRelay(); // перезапуск з новим джерелом
    }

    void OnRowChanged(TargetRow row)
    {
        var ts = settings.GetTarget(row.DeviceId, row.DeviceName);
        ts.Enabled = row.Checked;
        ts.DelayMs = row.DelayMs;
        ts.VolumePercent = row.VolumePercent;
        settingsDirty = true;
        PushTargets();
    }

    void PushTargets()
    {
        engine.SetTargets(rows
            .Where(r => r.Checked && !r.Blocked)
            .Select(r => new TargetConfig { Id = r.DeviceId, Name = r.DeviceName, DelayMs = r.DelayMs, Volume = r.VolumePercent / 100f }));
    }

    // ---------- Старт / стоп ----------

    void ToggleRelay()
    {
        if (engine.Running) StopRelay();
        else StartRelay();
    }

    void StartRelay()
    {
        if (sourceBox.SelectedItem is not DeviceItem src)
        {
            statusLabel.Text = "Спершу виберіть джерело звуку.";
            ShowFromTray();
            return;
        }
        engine.Start(src.Id, src.IsLoopback);
        engine.Metronome = metronomeBox.Checked;
        PushTargets();
        UpdateRunningUi();
    }

    void StopRelay()
    {
        engine.Stop();
        UpdateRunningUi();
    }

    void UpdateRunningUi()
    {
        bool on = engine.Running;
        startButton.Text = on ? "Стоп" : "Старт";
        trayToggle.Text = on ? "Зупинити ретрансляцію" : "Почати ретрансляцію";
        tray.Icon = on ? iconActive : iconIdle;
        Icon = on ? iconActive : iconIdle;
        UpdateStatuses();
    }

    void OnUiTick()
    {
        levelDisplay = Math.Max(engine.TakePeak(), levelDisplay * 0.8f);
        int lv = (int)Math.Clamp(Math.Sqrt(levelDisplay) * 100, 0, 100);
        if (levelBar.Value != lv) levelBar.Value = lv;

        if (++tick % 5 != 0) return; // раз на пів секунди

        engine.Maintain();
        UpdateStatuses();

        if (settingsDirty)
        {
            settingsDirty = false;
            settings.Save();
        }
    }

    void UpdateStatuses()
    {
        foreach (var r in rows) r.SetStatus(engine.GetTargetStatus(r.DeviceId));

        string src, status;
        if (!engine.Running)
        {
            src = "рівень сигналу джерела";
            status = "Зупинено.";
        }
        else
        {
            src = engine.SourceActive ? "джерело працює" : "джерело: " + (engine.SourceError ?? "підключаю…");
            status = engine.ConfiguredCount == 0
                ? "Позначте хоча б один пристрій нижче."
                : $"Ретранслюю на {engine.ActiveCount} з {engine.ConfiguredCount} пристр.";
        }
        sourceStatus.Text = src;
        statusLabel.Text = status;
        string tip = "Audio Combiner — " + status;
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    // ---------- Оновлення ----------

    void SetUpdateText(string? text = null)
    {
        string version = $"Версія {Updater.CurrentVersion.ToString(3)}";
        string action = text ?? (update != null ? $"Доступна версія {update.Version.ToString(3)} — оновити" : "Перевірити оновлення");
        updateLink.Text = $"{version}  ·  {action}";
        updateLink.LinkArea = text == null ? new LinkArea(version.Length + 5, action.Length) : new LinkArea(0, 0);
        updateLink.LinkColor = update != null ? Color.FromArgb(0, 120, 70) : SystemColors.HotTrack;
        updateLink.Font = new Font(Font, update != null ? FontStyle.Bold : FontStyle.Regular);
        trayUpdate.Text = update != null ? $"Оновити до версії {update.Version.ToString(3)}" : "Оновити";
        trayUpdate.Visible = update != null;
    }

    async Task CheckForUpdates(bool manual)
    {
        if (updateBusy) return;
        updateBusy = true;
        if (manual) SetUpdateText("Перевіряю…");
        try
        {
            var found = await Updater.CheckAsync();
            bool isNew = found != null && found.Version != update?.Version;
            update = found;
            SetUpdateText();
            if (manual && found == null)
                MessageBox.Show(this, $"У вас остання версія ({Updater.CurrentVersion.ToString(3)}).", "Audio Combiner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (!manual && isNew)
                tray.ShowBalloonTip(5000, "Audio Combiner", $"Доступна версія {found!.Version.ToString(3)}. Натисніть «Оновити» у меню іконки або у вікні програми.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            SetUpdateText();
            if (manual)
                MessageBox.Show(this, "Не вдалося перевірити оновлення:\n" + ex.Message, "Audio Combiner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updateBusy = false;
        }
    }

    async Task OnUpdateClicked()
    {
        if (updateBusy) return;
        if (update == null)
        {
            await CheckForUpdates(manual: true);
            if (update == null) return;
        }

        bool wasHidden = !Visible;
        ShowFromTray();

        var info = update;
        string notes = info.Notes.Trim();
        if (notes.Length > 1500) notes = notes[..1500] + "…";
        string question = $"Доступна версія {info.Version.ToString(3)} (у вас {Updater.CurrentVersion.ToString(3)}).\n\n" +
                          (notes.Length > 0 ? notes + "\n\n" : "") +
                          (info.DownloadUrl != null
                              ? "Завантажити й встановити зараз? Програма перезапуститься, ретрансляція продовжиться."
                              : "Відкрити сторінку релізу?");
        if (MessageBox.Show(this, question, "Оновлення Audio Combiner", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (info.DownloadUrl == null)
        {
            OpenShell(info.PageUrl);
            return;
        }

        updateBusy = true;
        try
        {
            SetUpdateText("Завантажую оновлення…");
            await Updater.InstallAsync(info, new Progress<int>(p => SetUpdateText($"Завантажую оновлення… {p}%")));
            Updater.Restart(hidden: wasHidden, relay: engine.Running);
            ExitApp();
        }
        catch (Exception ex)
        {
            updateBusy = false;
            SetUpdateText();
            if (MessageBox.Show(this, "Не вдалося встановити оновлення:\n" + ex.Message + "\n\nВідкрити сторінку релізу, щоб завантажити вручну?",
                    "Audio Combiner", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                OpenShell(info.PageUrl);
        }
    }

    // ---------- Трей, автозапуск, закриття ----------

    protected override void SetVisibleCore(bool value)
    {
        if (!allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    void ShowFromTray()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!balloonShown)
            {
                balloonShown = true;
                tray.ShowBalloonTip(3000, "Audio Combiner", "Програма працює у треї. Вийти — через меню іконки.", ToolTipIcon.Info);
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        reallyClose = true;
        uiTimer.Stop();
        updateTimer.Stop();
        try { enumerator.UnregisterEndpointNotificationCallback(watcher); } catch { }
        engine.Dispose();
        settings.Save();
        tray.Visible = false;
        tray.Dispose();
        showSignal.Set(); // розбудити й завершити потік очікування
        base.OnFormClosed(e);
    }

    void ExitApp()
    {
        reallyClose = true;
        Close();
    }

    static bool IsAutostartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(RunValue, false);
    }

    static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    static Icon MakeIcon(Color bg)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(bg);
            g.FillEllipse(b, 0, 0, 31, 31);
            using var p = new Pen(Color.White, 3f);
            g.DrawArc(p, 7, 7, 18, 18, 180, 180);
            g.FillRectangle(Brushes.White, 6, 15, 5, 9);
            g.FillRectangle(Brushes.White, 21, 15, 5, 9);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
