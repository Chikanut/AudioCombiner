namespace AudioCombiner;

/// Картка одного пристрою-цілі: галочка, затримка, гучність, стан
sealed class TargetRow : TableLayoutPanel
{
    static readonly Color WarnColor = Color.FromArgb(190, 90, 0);
    static readonly Color OkColor = Color.FromArgb(0, 120, 70);

    readonly CheckBox check;
    readonly Label info;
    readonly TrackBar delayBar, volBar;
    readonly NumericUpDown delayNum;
    readonly Label volLabel;
    readonly string? warning;
    string? blockReason;
    string status = "";
    bool syncing;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool Checked => check.Checked;
    public bool Blocked => blockReason != null;
    public int DelayMs => (int)delayNum.Value;
    public int VolumePercent => volBar.Value;

    public event Action<TargetRow>? SettingsChanged;

    public TargetRow(string id, string name, string? warning, TargetSettings ts, Func<int, int> S)
    {
        DeviceId = id;
        DeviceName = name;
        this.warning = warning;

        ColumnCount = 3;
        RowCount = 4;
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(110)));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(80)));
        RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));
        RowStyles.Add(new RowStyle(SizeType.Absolute, S(22)));
        RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
        RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
        Padding = new Padding(S(8), S(4), S(8), S(4));
        Margin = new Padding(0, 0, 0, S(8));
        Height = S(28 + 22 + 34 + 34 + 8);
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;

        check = new CheckBox
        {
            Text = name,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Checked = ts.Enabled,
        };
        check.Font = new Font(check.Font, FontStyle.Bold);
        Controls.Add(check, 0, 0);
        SetColumnSpan(check, 3);

        info = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        Controls.Add(info, 0, 1);
        SetColumnSpan(info, 3);

        Controls.Add(new Label { Text = "Затримка, мс", Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true }, 0, 2);
        delayBar = new TrackBar
        {
            Minimum = 0, Maximum = 1000, SmallChange = 5, LargeChange = 50,
            TickStyle = TickStyle.None, AutoSize = false, Dock = DockStyle.Fill,
            Value = Math.Clamp(ts.DelayMs, 0, 1000),
        };
        delayNum = new NumericUpDown
        {
            Minimum = 0, Maximum = 1000, Increment = 5,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Value = delayBar.Value,
        };
        Controls.Add(delayBar, 1, 2);
        Controls.Add(delayNum, 2, 2);

        Controls.Add(new Label { Text = "Гучність", Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true }, 0, 3);
        volBar = new TrackBar
        {
            Minimum = 0, Maximum = 150, SmallChange = 5, LargeChange = 10,
            TickStyle = TickStyle.None, AutoSize = false, Dock = DockStyle.Fill,
            Value = Math.Clamp(ts.VolumePercent, 0, 150),
        };
        volLabel = new Label { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, Text = $"{volBar.Value} %" };
        Controls.Add(volBar, 1, 3);
        Controls.Add(volLabel, 2, 3);

        check.CheckedChanged += (_, _) => { UpdateInfo(); SettingsChanged?.Invoke(this); };
        delayBar.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            delayNum.Value = delayBar.Value;
            syncing = false;
            SettingsChanged?.Invoke(this);
        };
        delayNum.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            delayBar.Value = (int)delayNum.Value;
            syncing = false;
            SettingsChanged?.Invoke(this);
        };
        volBar.ValueChanged += (_, _) =>
        {
            volLabel.Text = $"{volBar.Value} %";
            SettingsChanged?.Invoke(this);
        };

        UpdateInfo();
    }

    public void SetBlocked(string? reason)
    {
        blockReason = reason;
        check.Enabled = reason == null;
        UpdateInfo();
    }

    public void SetStatus(string s)
    {
        if (s == status) return;
        status = s;
        UpdateInfo();
    }

    void UpdateInfo()
    {
        var parts = new List<string>();
        if (blockReason != null) parts.Add(blockReason);
        if (warning != null) parts.Add(warning);
        if (blockReason == null && check.Checked && status != "") parts.Add(status);
        if (parts.Count == 0) parts.Add(check.Checked ? "увімкнено" : "вимкнено");

        info.Text = string.Join(" · ", parts);
        info.ForeColor = blockReason != null || warning != null ? WarnColor
            : status.StartsWith("працює") ? OkColor
            : SystemColors.GrayText;
    }
}
