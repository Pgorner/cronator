using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace Cronator.SettingsTabs.Monitors
{
    // (MonitorSnapshot/WidgetBox are in MonitorLayoutControl.cs)

    /// <summary> Duck-typed entry point that the host reflects over. </summary>
    public sealed class MonitorsTab
    {
        // 🔑 The host injects this via reflection:
        // SettingsForm looks for: Cronator.SettingsTabs.Monitors.MonitorsTab.SnapshotProvider (public static)
        public static Func<List<MonitorSnapshot>>? SnapshotProvider { get; set; }

        public string Id => "monitors";
        public string Title => "Monitors";
        public Control? CreateControl() => new MonitorsTabControl();
    }

    internal sealed class MonitorsTabControl : UserControl
    {
        private readonly FlowLayoutPanel _tabs;
        private readonly Label _status;
        private readonly Button _refresh;
        private readonly Button _save;
        private readonly Panel _canvasHost;
        private readonly MonitorLayoutControl _canvas;

        private List<MonitorSnapshot> _monitors = new();
        private int _selectedIndex = -1;

        // Track edits (name -> updated RectNorm)
        private readonly Dictionary<(int monitor, string name), RectangleF> _dirty = new();

        public MonitorsTabControl()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.White;

            // Header
            var header = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 8, 12, 8) };

            _tabs = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            _refresh = new Button { AutoSize = true, Text = "Refresh", Margin = new Padding(8, 0, 0, 0) };
            _refresh.Click += (_, __) => RefreshMonitors();

            _save = new Button { AutoSize = true, Text = "Save", Margin = new Padding(8, 0, 0, 0), Enabled = false };
            _save.Click += (_, __) => SaveDirty();

            _status = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Left,
                ForeColor = SystemColors.GrayText,
                Padding = new Padding(8, 12, 0, 0)
            };

            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true
            };
            right.Controls.Add(_refresh);
            right.Controls.Add(_save);

            header.Controls.Add(_tabs);
            header.Controls.Add(right);
            header.Controls.Add(_status);
            Controls.Add(header);

            // Canvas
            _canvas = new MonitorLayoutControl { Dock = DockStyle.Fill, BackColor = Color.White };
            _canvas.WidgetChanged += OnWidgetChanged;
            _canvasHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20), BackColor = Color.White };
            _canvasHost.Controls.Add(_canvas);
            Controls.Add(_canvasHost);

            RefreshMonitors();
        }

        private void RefreshMonitors()
        {
            try
            {
                // ✅ read from MonitorsTab.SnapshotProvider (this is what the host sets)
                var provider = MonitorsTab.SnapshotProvider;
                _monitors = provider?.Invoke() ?? BuildFallbackFromScreen();

                _status.Text = _monitors.Count == 0
                    ? "No monitors found"
                    : $"{_monitors.Count} monitor(s) found";

                _dirty.Clear();
                _save.Enabled = false;

                RebuildTabs();

                if (_monitors.Count > 0)
                {
                    if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count) _selectedIndex = 0;
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

        private void OnWidgetChanged(string name, RectangleF rn)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count) return;

            _dirty[(_selectedIndex, name)] = rn;
            _save.Enabled = _dirty.Count > 0;

            // update in-memory snapshot so tab switch keeps changes visible
            var snap = _monitors[_selectedIndex];
            var w = snap.Widgets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (w != null) w.RectNorm = rn;
        }

        private void SaveDirty()
        {
            if (_dirty.Count == 0) return;

            string widgetsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "widgets");
            int saved = 0, missed = 0;

            foreach (var kv in _dirty.ToList())
            {
                int mon = kv.Key.monitor;
                string widgetName = kv.Key.name;
                var rn = kv.Value;

                // heuristics: match folder where manifest.displayName == widgetName (fallback: folder name)
                var dir = FindWidgetFolderByDisplayName(widgetsRoot, widgetName);
                if (dir == null) { missed++; continue; }

                var userPath = Path.Combine(dir, "config.user.json");
                JsonNode root = File.Exists(userPath)
                    ? (JsonNode.Parse(File.ReadAllText(userPath)) ?? new JsonObject())
                    : new JsonObject();

                if (root is not JsonObject obj) obj = new JsonObject();

                // ensure enabled + settings object exists
                obj["enabled"] = true;
                var settings = (obj["settings"] as JsonObject) ?? new JsonObject();
                obj["settings"] = settings;

                // write normalized rect + monitor index
                settings["monitor"] = mon;
                settings["nx"] = Math.Round(rn.X, 4);
                settings["ny"] = Math.Round(rn.Y, 4);
                settings["nw"] = Math.Round(rn.Width, 4);
                settings["nh"] = Math.Round(rn.Height, 4);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
                    File.WriteAllText(userPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    saved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MonitorsTab] Save failed for '{widgetName}': {ex.Message}");
                    missed++;
                }
            }

            _dirty.Clear();
            _save.Enabled = false;

            _status.Text = missed > 0
                ? $"Saved {saved} change(s), {missed} not matched"
                : $"Saved {saved} change(s)";
        }

        private static string? FindWidgetFolderByDisplayName(string root, string displayName)
        {
            if (!Directory.Exists(root)) return null;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    string folderName = Path.GetFileName(dir);
                    string manifestPath = Path.Combine(dir, "manifest.json");

                    if (File.Exists(manifestPath))
                    {
                        using var j = JsonDocument.Parse(File.ReadAllText(manifestPath));
                        var ro = j.RootElement;
                        if (ro.TryGetProperty("displayName", out var dn) &&
                            dn.ValueKind == JsonValueKind.String &&
                            string.Equals(dn.GetString(), displayName, StringComparison.OrdinalIgnoreCase))
                        {
                            return dir;
                        }
                    }

                    // fallback: folder name equals display name
                    if (string.Equals(folderName, displayName, StringComparison.OrdinalIgnoreCase))
                        return dir;
                }
                catch { }
            }
            return null;
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
