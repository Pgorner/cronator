using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace Cronator
{
    internal class SettingsForm : Form
    {
        private ComboBox cboMonitor = new();
        private NumericUpDown numInterval = new();

        private CheckBox chkClock = new();
        private ComboBox cboClockPos = new();
        private TextBox txtClockFormat = new();
        private NumericUpDown numClockFont = new();

        private Button btnApply = new();
        private Button btnClose = new();

        // Paths / model
        private readonly string _clockFolder;
        private readonly string _clockConfigPath;

        private ClockConfig _clock = new();

        public SettingsForm()
        {
            Text = "Cronator Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 420;
            Height = 360;

            // Resolve config path
            var baseDir = AppContext.BaseDirectory;
            _clockFolder = Path.Combine(baseDir, "assets", "widgets", "clock");
            _clockConfigPath = Path.Combine(_clockFolder, "config.json");

            // --- Monitor ---
            var lblMon = new Label { Text = "Monitor:", Left = 16, Top = 20, Width = 120 };
            cboMonitor.Left = 140; cboMonitor.Top = 16; cboMonitor.Width = 240;

            // --- Timer ---
            var lblInt = new Label { Text = "Timer (s):", Left = 16, Top = 60, Width = 120 };
            numInterval.Left = 140; numInterval.Top = 56; numInterval.Width = 80;
            numInterval.Minimum = 0; // 0 = off
            numInterval.Maximum = 3600;
            numInterval.Value = 0;

            // --- Clock toggle ---
            var grpClock = new GroupBox { Text = "Clock widget", Left = 12, Top = 96, Width = 380, Height = 180 };

            var lblClock = new Label { Text = "Show clock:", Left = 12, Top = 28, Width = 120, Parent = grpClock };
            chkClock.Left = 140; chkClock.Top = 26; chkClock.Width = 20; chkClock.Parent = grpClock;

            var lblClockPos = new Label { Text = "Position:", Left = 12, Top = 64, Width = 120, Parent = grpClock };
            cboClockPos.Left = 140; cboClockPos.Top = 60; cboClockPos.Width = 200; cboClockPos.Parent = grpClock;
            cboClockPos.DropDownStyle = ComboBoxStyle.DropDownList;
            cboClockPos.Items.AddRange(new object[] { "UpperLeft", "UpperRight", "LowerLeft", "LowerRight", "Center" });

            var lblFormat = new Label { Text = "Format:", Left = 12, Top = 100, Width = 120, Parent = grpClock };
            txtClockFormat.Left = 140; txtClockFormat.Top = 96; txtClockFormat.Width = 200; txtClockFormat.Parent = grpClock;
            txtClockFormat.PlaceholderText = "e.g., HH:mm:ss";

            var lblFont = new Label { Text = "Font size (px):", Left = 12, Top = 136, Width = 120, Parent = grpClock };
            numClockFont.Left = 140; numClockFont.Top = 132; numClockFont.Width = 100; numClockFont.Parent = grpClock;
            numClockFont.Minimum = 0;     // 0 = auto
            numClockFont.Maximum = 256;
            numClockFont.Value = 0;

            // --- Buttons ---
            btnApply.Text = "Apply";
            btnApply.Left = 232; btnApply.Top = 290; btnApply.Width = 75;
            btnApply.Click += (_, __) => Apply();

            btnClose.Text = "Close";
            btnClose.Left = 317; btnClose.Top = 290; btnClose.Width = 75;
            btnClose.Click += (_, __) => Close();

            Controls.AddRange(new Control[]
            {
                lblMon, cboMonitor,
                lblInt, numInterval,
                grpClock,
                btnApply, btnClose
            });

            // populate monitors
            var mons = Program.TrayGetMonitorList();
            foreach (var (index, label) in mons.Select((l, i) => (i, l)))
                cboMonitor.Items.Add($"{index}: {label}");
            if (cboMonitor.Items.Count > 0)
                cboMonitor.SelectedIndex = Math.Max(0, Math.Min(Program.TrayGetSelectedMonitorIndex(), cboMonitor.Items.Count - 1));

            // load clock config
            LoadClockConfigIntoUI();
        }

        private void LoadClockConfigIntoUI()
        {
            try
            {
                Directory.CreateDirectory(_clockFolder);

                if (File.Exists(_clockConfigPath))
                {
                    var json = File.ReadAllText(_clockConfigPath);
                    _clock = JsonSerializer.Deserialize<ClockConfig>(json) ?? new ClockConfig();
                }
                else
                {
                    _clock = new ClockConfig(); // defaults, first run
                    File.WriteAllText(_clockConfigPath, JsonSerializer.Serialize(_clock, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to load clock config: " + ex.Message, "Cronator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _clock = new ClockConfig();
            }

            chkClock.Checked = _clock.Enabled;
            var posName = _clock.Position?.ToString() ?? "UpperRight";
            var posIdx = cboClockPos.Items.IndexOf(posName);
            cboClockPos.SelectedIndex = posIdx >= 0 ? posIdx : cboClockPos.Items.IndexOf("UpperRight");

            txtClockFormat.Text = string.IsNullOrWhiteSpace(_clock.Format) ? "HH:mm:ss" : _clock.Format;
            numClockFont.Value = (decimal)Math.Max(0, _clock.FontSize);
        }

        private void Apply()
        {
            // monitor
            if (cboMonitor.SelectedIndex >= 0)
                Program.TraySelectMonitor(cboMonitor.SelectedIndex);

            // user auto-timer
            int sec = (int)numInterval.Value;
            Program.TraySetTimer(sec > 0 ? sec : (int?)null);

            // widgets -> save clock config
            try
            {
                var pos = (string)(cboClockPos.SelectedItem ?? "UpperRight");
                _clock.Enabled = chkClock.Checked;
                _clock.Position = Enum.TryParse<WidgetPosition>(pos, out var wp) ? wp : WidgetPosition.UpperRight;
                _clock.Format = string.IsNullOrWhiteSpace(txtClockFormat.Text) ? "HH:mm:ss" : txtClockFormat.Text.Trim();
                _clock.FontSize = (float)numClockFont.Value; // 0 = auto

                Directory.CreateDirectory(_clockFolder);
                var json = JsonSerializer.Serialize(_clock, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_clockConfigPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save clock config: " + ex.Message, "Cronator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // force a visible update
            Program.TrayUpdateOnce();

            MessageBox.Show(this, "Applied.", "Cronator", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ----- local DTOs (mirror Program.cs) -----
        private enum WidgetPosition { UpperLeft, UpperRight, LowerLeft, LowerRight, Center }

        private sealed class ClockConfig
        {
            public bool Enabled { get; set; } = true;
            public WidgetPosition? Position { get; set; } = WidgetPosition.UpperRight;
            public float FontSize { get; set; } = 0; // 0 = auto
            public string Format { get; set; } = "HH:mm:ss";
        }
    }
}
