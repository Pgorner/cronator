using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace Cronator.SettingsTabs.Monitors
{
    // (MonitorSnapshot / WidgetBox are defined in MonitorLayoutControl.cs)

    /// <summary>Duck-typed entry point that the host reflects over.</summary>
    public sealed class MonitorsTab
    {
        // Injected by host (SettingsForm) via reflection:
        public static Func<List<MonitorSnapshot>>? SnapshotProvider { get; set; }

        public string Id => "monitors";
        public string Title => "Monitors";
        public Control? CreateControl() => new MonitorsTabControl();
    }

    internal sealed class MonitorsTabControl : UserControl
    {
        // ---- Local proxy to PlacementBus via AppDomain (no project reference needed) ----
        private static class BusProxy
        {
            private const string MapKey = "Cronator.PlacementBus.Map";

            private static ConcurrentDictionary<string, RectangleF> Map
            {
                get
                {
                    var o = AppDomain.CurrentDomain.GetData(MapKey) as ConcurrentDictionary<string, RectangleF>;
                    if (o == null)
                    {
                        o = new ConcurrentDictionary<string, RectangleF>();
                        AppDomain.CurrentDomain.SetData(MapKey, o);
                    }
                    return o;
                }
            }

            private static string K(int? mon, string name)
                => (mon.HasValue ? $"m{mon.Value}:" : "*:") + (name ?? "").Trim().ToLowerInvariant();

            private static RectangleF San(RectangleF r)
            {
                static float C(float x) => x < 0 ? 0 : (x > 1 ? 1 : x);
                return new RectangleF(C(r.X), C(r.Y), Math.Max(0.02f, C(r.Width)), Math.Max(0.02f, C(r.Height)));
            }

            public static void Seed(int monitorIndex, string name, RectangleF rn)
            {
                rn = San(rn);
                Map.TryAdd(K(monitorIndex, name), rn);
                Map.TryAdd(K(null, name), rn);
                Console.WriteLine($"[MonitorsTab] BusProxy.Seed m={monitorIndex}, name='{name}', rectN=({rn.X:0.###},{rn.Y:0.###},{rn.Width:0.###},{rn.Height:0.###})");
            }

            public static void Set(int monitorIndex, string name, RectangleF rn)
            {
                rn = San(rn);
                Map[K(monitorIndex, name)] = rn;
                Map[K(null, name)] = rn;
                Console.WriteLine($"[MonitorsTab] BusProxy.Set  m={monitorIndex}, name='{name}', rectN=({rn.X:0.###},{rn.Y:0.###},{rn.Width:0.###},{rn.Height:0.###})");
            }
        }
        // -------------------------------------------------------------------------------

        private readonly FlowLayoutPanel _tabs;
        private readonly Label _status;
        private readonly Button _refresh;
        private readonly Button _save;
        private readonly Panel _canvasHost;
        private readonly MonitorLayoutControl _canvas;

        private List<MonitorSnapshot> _monitors = new();
        private int _selectedIndex = -1;

        // (monitor index, widget displayName) -> new RectNorm
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
            _canvas.WidgetChangeCommitted += OnWidgetCommitted; // auto-save on drop (optional)

            _canvasHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20), BackColor = Color.White };
            _canvasHost.Controls.Add(_canvas);
            Controls.Add(_canvasHost);

            RefreshMonitors();
        }

        // ---------- Refresh / Load ----------

        private void RefreshMonitors()
        {
            try
            {
                // 1) Host snapshot (preferred)
                var provider = MonitorsTab.SnapshotProvider;
                _monitors = provider?.Invoke() ?? BuildFallbackFromScreen();

                // 2) Merge/hydrate from disk (normalized only)
                HydrateWidgetsFromDisk(_monitors);

                // 3) Seed bus so widgets immediately reflect current positions
                for (int i = 0; i < _monitors.Count; i++)
                {
                    var snap = _monitors[i];
                    foreach (var w in snap.Widgets ?? Enumerable.Empty<WidgetBox>())
                        BusProxy.Seed(i, w.Name, w.RectNorm);
                }

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

        /// <summary>
        /// Merge widgets into current snapshots using only normalized rects.
        /// Honors enabled flags from manifest and user config.
        /// Falls back to manifest.defaultRect if user rect is absent.
        /// </summary>
        private static void HydrateWidgetsFromDisk(List<MonitorSnapshot> monitors)
        {
            if (monitors.Count == 0) return;

            string widgetsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "widgets");
            if (!Directory.Exists(widgetsRoot)) return;

            foreach (var dir in Directory.EnumerateDirectories(widgetsRoot))
            {
                string displayName = Path.GetFileName(dir);
                bool enabled = false;

                // manifest
                float? dnx = null, dny = null, dnw = null, dnh = null;
                try
                {
                    var manifestPath = Path.Combine(dir, "manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                        var rootEl = doc.RootElement;
                        if (rootEl.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                            displayName = dn.GetString() ?? displayName;

                        if (rootEl.TryGetProperty("enabled", out var en) &&
                            (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                            enabled = rootEl.GetProperty("enabled").GetBoolean();

                        if (rootEl.TryGetProperty("defaultRect", out var dr) && dr.ValueKind == JsonValueKind.Object)
                        {
                            if (dr.TryGetProperty("nx", out var v))  dnx = (float?)v.GetDouble();
                            if (dr.TryGetProperty("ny", out var v2)) dny = (float?)v2.GetDouble();
                            if (dr.TryGetProperty("nw", out var v3)) dnw = (float?)v3.GetDouble();
                            if (dr.TryGetProperty("nh", out var v4)) dnh = (float?)v4.GetDouble();
                        }
                    }
                }
                catch { /* ignore malformed manifest */ }

                // user config
                int? monIndex = null;
                float? nx = null, ny = null, nw = null, nh = null;
                try
                {
                    var cfgPath = Path.Combine(dir, "config.user.json");
                    if (File.Exists(cfgPath))
                    {
                        if (JsonNode.Parse(File.ReadAllText(cfgPath)) is JsonObject node)
                        {
                            if (node["enabled"] is JsonValue e2 && e2.TryGetValue<bool>(out var en2))
                                enabled = en2;

                            if (node["settings"] is JsonObject s)
                            {
                                if (s["monitor"] is JsonValue mv && mv.TryGetValue<int>(out var mInt)) monIndex = mInt;

                                nx = TryReadFloat(s, "nx");
                                ny = TryReadFloat(s, "ny");
                                nw = TryReadFloat(s, "nw");
                                nh = TryReadFloat(s, "nh");
                            }
                        }
                    }
                }
                catch { /* ignore malformed user config */ }

                if (!enabled) continue;
                if (!monIndex.HasValue) continue; // don't auto-attach if no monitor set yet

                int mon = monIndex.Value;
                if (mon < 0 || mon >= monitors.Count) mon = 0;

                var snap = monitors[mon];
                snap.Widgets ??= new List<WidgetBox>();

                var existing = snap.Widgets.FirstOrDefault(w => string.Equals(w.Name, displayName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new WidgetBox
                    {
                        Name = displayName,
                        Enabled = true,
                        Color = ColorForName(displayName),
                        RectNorm = new RectangleF(0.72f, 0.03f, 0.22f, 0.13f) // default if neither user nor manifest has it
                    };
                    snap.Widgets.Add(existing);
                }

                // resolve rect: user wins -> manifest defaultRect -> keep whatever existing had
                if (nx.HasValue || ny.HasValue || nw.HasValue || nh.HasValue)
                {
                    existing.RectNorm = new RectangleF(
                        Clamp01(nx ?? existing.RectNorm.X),
                        Clamp01(ny ?? existing.RectNorm.Y),
                        Math.Max(0.02f, Clamp01(nw ?? existing.RectNorm.Width)),
                        Math.Max(0.02f, Clamp01(nh ?? existing.RectNorm.Height)));
                }
                else if (dnx.HasValue || dny.HasValue || dnw.HasValue || dnh.HasValue)
                {
                    existing.RectNorm = new RectangleF(
                        Clamp01(dnx ?? existing.RectNorm.X),
                        Clamp01(dny ?? existing.RectNorm.Y),
                        Math.Max(0.02f, Clamp01(dnw ?? existing.RectNorm.Width)),
                        Math.Max(0.02f, Clamp01(dnh ?? existing.RectNorm.Height)));
                }

                // ensure color is set
                if (existing.Color.A == 0) existing.Color = ColorForName(existing.Name);
            }
        }

        private static float? TryReadFloat(JsonObject settings, string key)
        {
            try
            {
                if (settings[key] is JsonValue v && v.TryGetValue<double>(out var d)) return (float)d;
                if (settings[key] is JsonValue v2 && v2.TryGetValue<float>(out var f)) return f;
            }
            catch { }
            return null;
        }

        // ---------- Tabs / Selection ----------

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

                // re-seed bus for this monitor (useful after switching tabs)
                if (snap?.Widgets != null)
                    foreach (var w in snap.Widgets)
                        BusProxy.Seed(index, w.Name, w.RectNorm);
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

        // ---------- Change tracking / Save ----------

        private void OnWidgetChanged(string name, RectangleF rn)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count) return;

            _dirty[(_selectedIndex, name)] = rn;
            _save.Enabled = _dirty.Count > 0;

            // keep live snapshot in sync for tab switching
            var snap = _monitors[_selectedIndex];
            var w = snap.Widgets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (w != null) w.RectNorm = rn;

            // push live change to bus so widgets move immediately
            BusProxy.Set(_selectedIndex, name, rn);
        }

        // Optional: auto-save on mouse up (commit). Comment out if you prefer manual "Save".
        private void OnWidgetCommitted(string name, RectangleF rn)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count) return;

            // ensure bus is updated one more time on commit
            BusProxy.Set(_selectedIndex, name, rn);

            _dirty[(_selectedIndex, name)] = rn;
            SaveDirty();
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

                var dir = FindWidgetFolderByDisplayName(widgetsRoot, widgetName);
                if (dir == null) { missed++; continue; }

                var userPath = Path.Combine(dir, "config.user.json");
                JsonObject root;
                try
                {
                    root = (JsonNode.Parse(File.Exists(userPath) ? File.ReadAllText(userPath) : "{}") as JsonObject)
                           ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }

                // ensure enabled + settings object exists
                root["enabled"] = true;
                var settings = (root["settings"] as JsonObject) ?? new JsonObject();
                root["settings"] = settings;

                // write normalized rect + monitor index
                settings["monitor"] = mon;
                settings["nx"] = Math.Round(rn.X, 4);
                settings["ny"] = Math.Round(rn.Y, 4);
                settings["nw"] = Math.Round(rn.Width, 4);
                settings["nh"] = Math.Round(rn.Height, 4);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
                    File.WriteAllText(userPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

            // exact match on manifest.displayName
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
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
                }
                catch { /* ignore malformed manifests */ }
            }

            // fallback: folder name equals displayName
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (string.Equals(Path.GetFileName(dir), displayName, StringComparison.OrdinalIgnoreCase))
                    return dir;

            return null;
        }

        // ---------- Helpers ----------

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
                    Widgets = new List<WidgetBox>()
                });
            }
            return list;
        }

        private static Color ColorForName(string name)
        {
            using var sha1 = SHA1.Create();
            var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(name));
            int hue = bytes[0] * 360 / 255;
            int sat = 160 + (bytes[1] % 70);
            int val = 180 + (bytes[2] % 60);
            return FromHsv(hue, sat / 255.0, val / 255.0);
        }

        private static Color FromHsv(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value * 255;
            int v = (int)Math.Round(value);
            int p = (int)Math.Round(value * (1 - saturation));
            int q = (int)Math.Round(value * (1 - f * saturation));
            int t = (int)Math.Round(value * (1 - (1 - f) * saturation));

            return hi switch
            {
                0 => Color.FromArgb(255, v, t, p),
                1 => Color.FromArgb(255, q, v, p),
                2 => Color.FromArgb(255, p, v, t),
                3 => Color.FromArgb(255, p, q, v),
                4 => Color.FromArgb(255, t, p, v),
                _ => Color.FromArgb(255, v, p, q),
            };
        }

        private static float Clamp01(float x) => x < 0 ? 0 : (x > 1 ? 1 : x);
    }
}
