using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Cronator.SettingsTabs.Monitors
{
    /// <summary>
    /// Host (SettingsForm) will set this at runtime via reflection:
    /// MonitorsInterop.SnapshotProvider = Func&lt;List&lt;MonitorSnapshot&gt;&gt;
    /// </summary>
    public static class MonitorsInterop
    {
        public static Func<List<MonitorSnapshot>>? SnapshotProvider { get; set; }
    }

    // NOTE: Do NOT redefine MonitorSnapshot/WidgetBox here.
    // They already exist in MonitorLayoutControl.cs in this same namespace.
    // We just use them.

    /// <summary>
    /// Duck-typed tab entry point: the host looks for Id, Title, and CreateControl().
    /// No reference to Cronator.ISettingsTab needed.
    /// </summary>
    public sealed class MonitorsTab
    {
        public string Id => "monitors";
        public string Title => "Monitors";
        public Control? CreateControl() => new MonitorsTabControl();
    }

    internal sealed class MonitorsTabControl : UserControl
    {
        private readonly FlowLayoutPanel _tabs;
        private readonly Button _refresh;
        private readonly Label _status;
        private readonly Panel _canvasHost;
        private readonly MonitorLayoutControl _canvas;

        private List<MonitorSnapshot> _monitors = new();
        private int _selectedIndex = -1;

        public MonitorsTabControl()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.White;

            // Header
            var header = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 8, 12, 8) };

            _tabs = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            _refresh = new Button
            {
                AutoSize = true,
                Text = "Refresh",
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Margin = new Padding(8, 0, 0, 0)
            };
            _refresh.Click += (_, __) => RefreshMonitors();

            _status = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Left,
                ForeColor = SystemColors.GrayText,
                Padding = new Padding(8, 10, 0, 0)
            };

            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true
            };
            right.Controls.Add(_refresh);

            header.Controls.Add(_tabs);
            header.Controls.Add(right);
            header.Controls.Add(_status);
            Controls.Add(header);

            // Canvas host
            _canvas = new MonitorLayoutControl { Dock = DockStyle.Fill, BackColor = Color.White };
            _canvasHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20), BackColor = Color.White };
            _canvasHost.Controls.Add(_canvas);
            Controls.Add(_canvasHost);

            RefreshMonitors();
        }

        private void RefreshMonitors()
        {
            try
            {
                var provider = MonitorsInterop.SnapshotProvider;
                _monitors = provider?.Invoke() ?? BuildFallbackFromScreen();

                _status.Text = _monitors.Count == 0
                    ? "No monitors found"
                    : $"{_monitors.Count} monitor(s) found";

                RebuildTabs();

                if (_monitors.Count > 0)
                {
                    if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count)
                        _selectedIndex = 0;
                    SelectMonitor(_selectedIndex, updateButtonsOnly: false);
                }
                else
                {
                    _selectedIndex = -1;
                    _canvas.SetMonitor(null);
                }
            }
            catch (Exception ex)
            {
                _status.Text = "Error reading monitors";
                Console.WriteLine("[MonitorsTab] RefreshMonitors error: " + ex);
                _monitors = new();
                RebuildTabs();
                _canvas.SetMonitor(null);
            }
        }

        private void RebuildTabs()
        {
            _tabs.SuspendLayout();
            try
            {
                _tabs.Controls.Clear();

                for (int i = 0; i < _monitors.Count; i++)
                {
                    var m = _monitors[i];
                    var btn = new Button
                    {
                        Text = MakeTabTitle(m, i),
                        AutoSize = true,
                        FlatStyle = FlatStyle.System,
                        Tag = i,
                        Margin = new Padding(0, 0, 8, 0)
                    };
                    btn.Click += (_, __) => SelectMonitor((int)btn.Tag!, updateButtonsOnly: false);
                    _tabs.Controls.Add(btn);
                }
            }
            finally { _tabs.ResumeLayout(); }

            UpdateTabButtonStates();
        }

        private void SelectMonitor(int index, bool updateButtonsOnly)
        {
            _selectedIndex = index;
            UpdateTabButtonStates();

            if (!updateButtonsOnly)
            {
                var snap = (index >= 0 && index < _monitors.Count) ? _monitors[index] : null;
                _canvas.SetMonitor(snap);
            }
        }

        private void UpdateTabButtonStates()
        {
            foreach (Control c in _tabs.Controls)
            {
                if (c is Button b && b.Tag is int idx)
                {
                    var sel = idx == _selectedIndex;
                    b.Font = new Font(b.Font, sel ? FontStyle.Bold : FontStyle.Regular);
                }
            }
        }

        private static string MakeTabTitle(MonitorSnapshot m, int idx)
        {
            var sz = $"{m.Bounds.Width}×{m.Bounds.Height}";
            var primary = m.IsPrimary ? " • Primary" : "";
            return $"[{idx}] {sz}{primary}";
        }

        private static List<MonitorSnapshot> BuildFallbackFromScreen()
        {
            var list = new List<MonitorSnapshot>();
            var screens = Screen.AllScreens;

            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                list.Add(new MonitorSnapshot
                {
                    Index = i,
                    Bounds = s.Bounds,
                    Label = $"{s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (Primary)" : ""),
                    IsPrimary = s.Primary,
                    Widgets = new List<WidgetBox>() // empty in fallback
                });
            }

            return list;
        }
    }
}
