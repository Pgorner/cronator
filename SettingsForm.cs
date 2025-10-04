using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace Cronator
{
    // Public so tab DLLs can implement it.
    public interface ISettingsTab
    {
        string Id { get; }
        string Title { get; }
        Control? CreateControl();
    }

    public sealed class SettingsForm : Form
    {
        private readonly SplitContainer _split;
        private readonly TreeView _nav;
        private readonly Panel _contentHost;

        private readonly List<WidgetInfo> _widgets; // available for any tabs that want it
        private Control? _currentPage;

        // dynamic tabs
        private readonly TabManager _tabs = new();

        public SettingsForm() : this(new List<WidgetInfo>()) { }

        public SettingsForm(List<WidgetInfo> widgets)
        {
            _widgets = widgets ?? new List<WidgetInfo>();

            Text = "Cronator Settings";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 600);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterDistance = 220,
                Panel1MinSize = 180
            };
            Controls.Add(_split);

            _nav = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                FullRowSelect = true,
                ShowLines = false,
                BorderStyle = BorderStyle.None
            };
            _nav.AfterSelect += Nav_AfterSelect;
            _split.Panel1.Controls.Add(_nav);

            _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
            _split.Panel2.Controls.Add(_contentHost);

            BuildNavFromTabs();
        }

        private void BuildNavFromTabs()
        {
            _nav.Nodes.Clear();

            try
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "settingstabs");
                _tabs.LoadFromFolder(root);

                // inject provider for monitors tab types (if present)
                TryInjectMonitorsProviderIfAvailable();

                foreach (var t in _tabs.Tabs)
                {
                    var title = t.Instance?.Title
                                ?? t.Manifest.displayName
                                ?? t.Manifest.name
                                ?? t.FolderName
                                ?? "Tab";

                    var node = new TreeNode(title) { Tag = t };
                    _nav.Nodes.Add(node);
                }

                _nav.ExpandAll();

                if (_nav.Nodes.Count > 0)
                {
                    _nav.SelectedNode = _nav.Nodes[0];
                }
                else
                {
                    ShowPage(new PlaceholderPage("No settings tabs found in assets/settingstabs."));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Tabs] load error: " + ex.Message);
                ShowPage(new PlaceholderPage("Failed to load tabs."));
            }
        }

        private void Nav_AfterSelect(object? sender, TreeViewEventArgs e)
        {
            // try again here in case assembly was loaded after BuildNavFromTabs
            TryInjectMonitorsProviderIfAvailable();

            var node = e.Node;
            if (node?.Tag is not TabManager.TabHandle handle)
            {
                ShowPage(new PlaceholderPage("Select a tab on the left."));
                return;
            }

            try
            {
                var ctrl = handle.Instance.CreateControl() ?? new PlaceholderPage("Tab has no content.");
                ShowPage(ctrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tabs] create control error for '{handle.Manifest.name}': {ex.Message}");
                ShowPage(new PlaceholderPage("Failed to create tab UI."));
            }
        }

        private void ShowPage(Control page)
        {
            if (_currentPage != null)
            {
                _contentHost.Controls.Remove(_currentPage);
                try { _currentPage.Dispose(); } catch { }
                _currentPage = null;
            }
            _currentPage = page;
            page.Dock = DockStyle.Fill;
            _contentHost.Controls.Add(page);
        }

        // ======================== Monitors snapshot injection ========================

        /// <summary>
        /// If the Monitors tab assembly is loaded, set its static SnapshotProvider (via reflection)
        /// to a delegate that returns the actual, current widget placements normalized per monitor.
        /// </summary>
        private void TryInjectMonitorsProviderIfAvailable()
        {
            try
            {
                foreach (var h in _tabs.Tabs)
                {
                    var asm = h.Assembly;
                    if (asm == null) continue;

                    // Prefer known types first (faster), then exported
                    var knownTypes = new[]
                    {
                        asm.GetType("Cronator.SettingsTabs.Monitors.MonitorsTab"),
                        asm.GetType("Cronator.SettingsTabs.Monitors.MonitorsInterop")
                    }.Where(t => t != null)!.Cast<Type>().ToArray();

                    IEnumerable<Type> exportedPlusKnown =
                        knownTypes.Any()
                            ? knownTypes.Concat(asm.GetExportedTypes().Where(t => !knownTypes.Contains(t)))
                            : asm.GetExportedTypes();

                    var candidateProps = exportedPlusKnown
                        .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                        .Where(p => string.Equals(p.Name, "SnapshotProvider", StringComparison.Ordinal))
                        .ToList();

                    Console.WriteLine($"[Tabs] monitors: found {candidateProps.Count} candidate SnapshotProvider propert{(candidateProps.Count==1?"y":"ies")} in {asm.GetName().Name}.");

                    foreach (var prop in candidateProps)
                    {
                        try
                        {
                            Console.WriteLine($"[Tabs] monitors: inspecting {prop.DeclaringType?.FullName}.{prop.Name} : {prop.PropertyType.FullName}");

                            var propType = prop.PropertyType;
                            if (!propType.IsGenericType || propType.GetGenericTypeDefinition() != typeof(Func<>))
                            {
                                Console.WriteLine("  - reject: property type is not Func<>");
                                continue;
                            }

                            var retType = propType.GetGenericArguments()[0];
                            if (!retType.IsGenericType || retType.GetGenericTypeDefinition() != typeof(List<>))
                            {
                                Console.WriteLine("  - reject: return is not List<TSnapshot>");
                                continue;
                            }

                            var snapType = retType.GetGenericArguments()[0];
                            if (snapType == null) { Console.WriteLine("  - reject: no TSnapshot"); continue; }

                            var wbType = asm.GetExportedTypes().FirstOrDefault(t => t.Namespace == snapType.Namespace && t.Name == "WidgetBox")
                                    ?? asm.GetExportedTypes().FirstOrDefault(t => t.Name == "WidgetBox");
                            if (wbType == null)
                            {
                                Console.WriteLine("  - reject: WidgetBox type not found");
                                continue;
                            }

                            var buildMethod = typeof(SettingsForm).GetMethod(nameof(BuildMonitorSnapshotsObjects), BindingFlags.NonPublic | BindingFlags.Static);
                            if (buildMethod == null)
                            {
                                Console.WriteLine("  - reject: builder method missing");
                                continue;
                            }

                            var closedBuilder = buildMethod.MakeGenericMethod(snapType, wbType);
                            var del = Delegate.CreateDelegate(propType, closedBuilder);

                            prop.SetValue(null, del);
                            Console.WriteLine($"[Tabs] monitors: SnapshotProvider injected on {prop.DeclaringType?.FullName}.");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  - reject: exception while wiring: {ex.Message}");
                        }
                    }
                }

                Console.WriteLine("[Tabs] monitors: SnapshotProvider property not found to inject.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Tabs] monitors provider inject error: " + ex);
            }
        }

        /// <summary>
        /// Builds List&lt;TMonitorSnapshot&gt; where TMonitorSnapshot, TWidgetBox are the types from the MonitorsTab assembly.
        /// Uses current Screen.AllScreens and widget configs under assets/widgets.
        /// </summary>
        private static List<TMonitorSnapshot> BuildMonitorSnapshotsObjects<TMonitorSnapshot, TWidgetBox>()
            where TMonitorSnapshot : class
            where TWidgetBox : class
        {
            var list = new List<TMonitorSnapshot>();
            var screens = Screen.AllScreens;

            // Prepare reflection handles
            var snType = typeof(TMonitorSnapshot);
            var wbType = typeof(TWidgetBox);

            var snBoundsProp   = snType.GetProperty("Bounds")!;
            var snLabelProp    = snType.GetProperty("Label")!;
            var snIsPrimaryProp= snType.GetProperty("IsPrimary")!;
            var snIndexProp    = snType.GetProperty("Index")!;
            var snWidgetsProp  = snType.GetProperty("Widgets")!; // List<WidgetBox>

            var wbRectProp   = wbType.GetProperty("RectNorm")!;
            var wbNameProp   = wbType.GetProperty("Name")!;
            var wbColorProp  = wbType.GetProperty("Color")!;
            var wbEnabledProp= wbType.GetProperty("Enabled")!;

            // Build placements from disk
            var placements = ReadWidgetPlacements(screens);

            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                var snap = Activator.CreateInstance<TMonitorSnapshot>();

                snBoundsProp.SetValue(snap, s.Bounds);
                snLabelProp.SetValue(snap, $"{s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (Primary)" : ""));
                snIsPrimaryProp.SetValue(snap, s.Primary);
                snIndexProp.SetValue(snap, i);

                // Create strongly-typed List<WidgetBox>
                var listWb = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(wbType))!;

                if (placements.TryGetValue(i, out var ws))
                {
                    foreach (var p in ws)
                    {
                        var wb = Activator.CreateInstance<TWidgetBox>();
                        wbRectProp.SetValue(wb, p.RectNorm);
                        wbNameProp.SetValue(wb, p.Name);
                        wbColorProp.SetValue(wb, p.Color);
                        wbEnabledProp.SetValue(wb, p.Enabled);
                        listWb.Add(wb);
                    }
                }

                snWidgetsProp.SetValue(snap, listWb);
                list.Add(snap);
            }

            return list;
        }

        // Deserialized, engine-agnostic widget placement
        private sealed class Placement
        {
            public RectangleF RectNorm;
            public string Name = "Widget";
            public bool Enabled = true;
            public Color Color = Color.SteelBlue;
        }

        /// <summary>
        /// Read widgets under assets/widgets/*, combine manifest + config.user.json,
        /// decide monitor index and normalized rectangle (nx,ny,nw,nh or pixel fallback).
        /// </summary>
        // ===================== NORMALIZED-ONLY PLACEMENTS =====================
        private static Dictionary<int, List<Placement>> ReadWidgetPlacements(Screen[] screens)
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "widgets");
            var map = new Dictionary<int, List<Placement>>();
            if (!Directory.Exists(root)) return map;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var manifestPath = Path.Combine(dir, "manifest.json");
                    var userPath     = Path.Combine(dir, "config.user.json");

                    string displayName = Path.GetFileName(dir);
                    bool enabled = false;
                    JsonNode? settingsNode = null;

                    // --- Read manifest (displayName, enabled, defaultRect) ---
                    float? dnx = null, dny = null, dnw = null, dnh = null;
                    if (File.Exists(manifestPath))
                    {
                        if (JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject mn)
                        {
                            if (mn["displayName"] is JsonValue dv && dv.TryGetValue<string>(out var dn) && !string.IsNullOrWhiteSpace(dn))
                                displayName = dn;

                            if (mn["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en))
                                enabled = en;

                            if (mn["defaultRect"] is JsonObject dr)
                            {
                                dnx = GetFloat(dr, "nx");
                                dny = GetFloat(dr, "ny");
                                dnw = GetFloat(dr, "nw");
                                dnh = GetFloat(dr, "nh");
                            }
                        }
                    }

                    // --- User overrides (enabled, settings{ monitor, nx..nh }) ---
                    int? monFromUser = null;
                    if (File.Exists(userPath))
                    {
                        if (JsonNode.Parse(File.ReadAllText(userPath)) is JsonObject un)
                        {
                            if (un["enabled"] is JsonValue e2 && e2.TryGetValue<bool>(out var en2))
                                enabled = en2;

                            if (un["settings"] is JsonObject so)
                            {
                                settingsNode = so;

                                // monitor / screen / display
                                if (TryGetInt(so, out var mi, "monitor", "screen", "display"))
                                    monFromUser = mi;
                            }
                        }
                    }

                    if (!enabled) continue; // honor enabled flags

                    // Decide target monitor index
                    int mon = monFromUser ?? 0;
                    if (mon < 0 || mon >= screens.Length) mon = 0;

                    // Preferred rect order: user nx..nh → manifest defaultRect → hardcoded fallback
                    float? nx = GetFloat(settingsNode, "nx");
                    float? ny = GetFloat(settingsNode, "ny");
                    float? nw = GetFloat(settingsNode, "nw");
                    float? nh = GetFloat(settingsNode, "nh");

                    RectangleF rectN;
                    if (nx.HasValue || ny.HasValue || nw.HasValue || nh.HasValue)
                    {
                        rectN = new RectangleF(
                            Clamp01(nx ?? 0.72f),
                            Clamp01(ny ?? 0.03f),
                            Clamp01(nw ?? 0.22f),
                            Clamp01(nh ?? 0.13f));
                    }
                    else if (dnx.HasValue || dny.HasValue || dnw.HasValue || dnh.HasValue)
                    {
                        rectN = new RectangleF(
                            Clamp01(dnx ?? 0.72f),
                            Clamp01(dny ?? 0.03f),
                            Clamp01(dnw ?? 0.22f),
                            Clamp01(dnh ?? 0.13f));
                    }
                    else
                    {
                        rectN = new RectangleF(0.72f, 0.03f, 0.22f, 0.13f); // final fallback
                    }

                    rectN = NormalizeRect(rectN);

                    var p = new Placement
                    {
                        RectNorm = rectN,
                        Name = displayName,
                        Enabled = true,
                        Color = ColorFromName(displayName)
                    };

                    if (!map.TryGetValue(mon, out var lst))
                        map[mon] = lst = new List<Placement>();
                    lst.Add(p);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[MonitorsTab Placement] " + ex.Message);
                }
            }

            return map;
        }

        // ---------- JsonNode helpers ----------
        private static string? GetString(JsonNode? settings, string key)
        {
            if (settings is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return null;
        }

        private static float? GetFirstFloat(JsonNode? settings, params string[] keys)
        {
            foreach (var k in keys)
            {
                var f = GetFloat(settings, k);
                if (f.HasValue) return f.Value;
            }
            return null;
        }

        private static bool TryGetInt(JsonNode? settings, out int value, params string[] keys)
        {
            value = 0;
            if (settings is not JsonObject obj) return false;

            foreach (var k in keys)
            {
                if (!obj.TryGetPropertyValue(k, out var node) || node is null) continue;

                if (node is JsonValue v)
                {
                    if (v.TryGetValue<int>(out var i)) { value = i; return true; }
                    if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var j)) { value = j; return true; }
                }
            }
            return false;
        }

        private static float? GetFloat(JsonNode? settings, string key)
        {
            if (settings is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;

            if (node is JsonValue v)
            {
                if (v.TryGetValue<float>(out var f)) return f;
                if (v.TryGetValue<double>(out var d)) return (float)d;
                if (v.TryGetValue<string>(out var s) && float.TryParse(s, out var g)) return g;
            }
            return null;
        }

        private static float GetFirstFloat(JsonNode? settings, float fallback, params string[] keys)
        {
            foreach (var k in keys)
            {
                var f = GetFloat(settings, k);
                if (f.HasValue) return f.Value;
            }
            return fallback;
        }

        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static RectangleF NormalizeRect(RectangleF r)
        {
            float x = Clamp01(r.X);
            float y = Clamp01(r.Y);
            float w = Clamp01(r.Width);
            float h = Clamp01(r.Height);

            if (x + w > 1f) w = 1f - x;
            if (y + h > 1f) h = 1f - y;

            w = Math.Max(0.01f, w);
            h = Math.Max(0.01f, h);
            return new RectangleF(x, y, w, h);
        }

        // Anchor → normalized rect (JsonNode version)
        private static RectangleF RectFromAnchor(Screen s, JsonNode? settings)
        {
            string fmt       = GetString(settings, "format") ?? "HH:mm:ss";
            float  fontPx    = GetFirstFloat(settings, 120f, "fontPx", "fontSize");
            float  scale     = GetFirstFloat(settings, 1.0f, "scale");
            string anchor    = (GetString(settings, "anchor") ?? AnchorFromPosition(GetString(settings, "position")) ?? "top-right").ToLowerInvariant();
            int    offX      = (int)(GetFirstFloat(settings, 0f, "offsetX", "margin")); // margin handled below
            int    offY      = (int)(GetFirstFloat(settings, 0f, "offsetY", "margin"));

            // font family/style like the widget uses
            string fontFamily = GetString(settings, "fontFamily") ?? "Segoe UI";
            string fontStyle  = GetString(settings, "fontStyle")  ?? "Bold";
            var    style      = ParseFontStyle(fontStyle);

            // If legacy only had "margin", translate it to offsets *inward* for the anchor
            if (!(settings is JsonObject so && (so.ContainsKey("offsetX") || so.ContainsKey("offsetY"))))
            {
                if (settings is JsonObject o && o.TryGetPropertyValue("margin", out var marginNode) && marginNode is JsonValue marginVal)
                {
                    if (marginVal.TryGetValue<float>(out var marginF) || (marginVal.TryGetValue<string>(out var marginS) && float.TryParse(marginS, out marginF)))
                    {
                        var (dx, dy) = OffsetsFromMarginForAnchor((int)marginF, anchor);
                        offX = dx; offY = dy;
                    }
                }
            }

            // Measure using same family/style as the widget (device px)
            var text = DateTime.Now.ToString(fmt);
            SizeF textSize;
            using (var bmp = new Bitmap(1,1))
            using (var g = Graphics.FromImage(bmp))
            using (var f = new Font(fontFamily, Math.Max(8f, fontPx * scale), style, GraphicsUnit.Pixel))
                textSize = g.MeasureString(text, f);

            var bounds = s.Bounds;
            float x, y;
            switch (anchor)
            {
                case "top-left":      x = 0;                      y = 0;                        break;
                case "top-right":     x = bounds.Width - textSize.Width;  y = 0;                 break;
                case "bottom-left":   x = 0;                      y = bounds.Height - textSize.Height; break;
                case "bottom-right":  x = bounds.Width - textSize.Width;  y = bounds.Height - textSize.Height; break;
                case "center":        x = (bounds.Width - textSize.Width)/2f; y = (bounds.Height - textSize.Height)/2f; break;
                default:              x = bounds.Width - textSize.Width;    y = 0;               break;
            }
            x += offX; y += offY;

            float nx = Clamp01((float)x / Math.Max(1, bounds.Width));
            float ny = Clamp01((float)y / Math.Max(1, bounds.Height));
            float nw = Clamp01((float)textSize.Width  / Math.Max(1, bounds.Width));
            float nh = Clamp01((float)textSize.Height / Math.Max(1, bounds.Height));

            nw = Math.Max(0.01f, nw);
            nh = Math.Max(0.01f, nh);
            if (nx + nw > 1f) nx = 1f - nw;
            if (ny + nh > 1f) ny = 1f - nh;

            return new RectangleF(nx, ny, nw, nh);

            // local helpers:
            static string AnchorFromPosition(string? pos) => pos?.Trim().ToLowerInvariant() switch
            {
                "upperleft" or "topleft"     => "top-left",
                "upperright" or "topright"   => "top-right",
                "lowerleft" or "bottomleft"  => "bottom-left",
                "lowerright" or "bottomright"=> "bottom-right",
                "center"                     => "center",
                _                            => "top-right"
            };
            static FontStyle ParseFontStyle(string s)
            {
                FontStyle fs = 0;
                foreach (var part in s.Split(new[]{',','|',';'}, StringSplitOptions.RemoveEmptyEntries))
                    if (Enum.TryParse<FontStyle>(part.Trim(), true, out var one)) fs |= one;
                return fs == 0 ? FontStyle.Regular : fs;
            }
            static (int, int) OffsetsFromMarginForAnchor(int margin, string anch) => anch switch
            {
                "top-left"      => ( margin,  margin),
                "top-right"     => (-margin,  margin),
                "bottom-left"   => ( margin, -margin),
                "bottom-right"  => (-margin, -margin),
                "center"        => (0,0),
                _               => (-margin,  margin)
            };
        }


        private static Color ColorFromName(string name)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in name) h = h * 31 + c;
                // Convert hash → pleasant color
                int r = 160 + (Math.Abs(h) % 96);
                int g = 120 + (Math.Abs(h >> 3) % 96);
                int b = 140 + (Math.Abs(h >> 5) % 96);
                return Color.FromArgb(r, g, b);
            }
        }
    }

    // ---------------- Placeholder page ----------------
    internal sealed class PlaceholderPage : UserControl
    {
        public PlaceholderPage(string text)
        {
            Controls.Add(new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11, FontStyle.Regular)
            });
        }
    }

    // ---------------- Tabs loader ----------------
    internal sealed class TabManager
    {
        public readonly List<TabHandle> Tabs = new();

        public sealed class TabManifest
        {
            public string? name { get; set; }          // e.g., "monitors"
            public string? displayName { get; set; }   // e.g., "Monitors"
            public string? kind { get; set; }          // "dll"
            public string? assembly { get; set; }      // "MonitorsTab.dll"
            public string? type { get; set; }          // "Cronator.SettingsTabs.Monitors.MonitorsTab"
            public bool enabled { get; set; } = true;
        }

        public sealed class TabHandle
        {
            public string FolderPath { get; init; } = "";
            public string FolderName { get; init; } = "";
            public TabManifest Manifest { get; init; } = new();
            public ISettingsTab Instance { get; init; } = default!;
            public System.Reflection.Assembly Assembly { get; init; } = default!;
        }

        private sealed class ReflectionTabAdapter : ISettingsTab
        {
            private readonly object _impl;
            private readonly PropertyInfo _id;
            private readonly PropertyInfo _title;
            private readonly MethodInfo _create;

            public ReflectionTabAdapter(object impl)
            {
                _impl = impl;
                var t = impl.GetType();
                _id = t.GetProperty("Id") ?? throw new InvalidOperationException("Tab must expose Id property");
                _title = t.GetProperty("Title") ?? throw new InvalidOperationException("Tab must expose Title property");
                _create = t.GetMethod("CreateControl", BindingFlags.Public | BindingFlags.Instance)
                          ?? throw new InvalidOperationException("Tab must expose CreateControl()");
            }

            public string Id => (string)(_id.GetValue(_impl) ?? "");
            public string Title => (string)(_title.GetValue(_impl) ?? "");
            public Control? CreateControl() => (Control?)_create.Invoke(_impl, null);
        }

        public void LoadFromFolder(string root)
        {
            Tabs.Clear();

            if (!Directory.Exists(root))
            {
                Console.WriteLine($"[Tabs] root folder not found: {root}");
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var fname = Path.GetFileName(folder);
                try
                {
                    var manifestPath = Path.Combine(folder, "manifest.json");
                    if (!File.Exists(manifestPath))
                    {
                        Console.WriteLine($"[Tabs] {fname}: missing manifest.json → skipped.");
                        continue;
                    }

                    var manifest = JsonSerializer.Deserialize<TabManifest>(File.ReadAllText(manifestPath)) ?? new TabManifest();
                    if (!manifest.enabled)
                    {
                        Console.WriteLine($"[Tabs] {fname}: disabled.");
                        continue;
                    }

                    if (!"dll".Equals(manifest.kind, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[Tabs] {fname}: unsupported kind '{manifest.kind}'.");
                        continue;
                    }

                    var asmDecl = manifest.assembly;
                    if (string.IsNullOrWhiteSpace(asmDecl))
                    {
                        Console.WriteLine($"[Tabs] {fname}: 'assembly' missing → skipped.");
                        continue;
                    }

                    if (!TryResolveAssemblyPath(folder, asmDecl!, out var asmPath))
                    {
                        Console.WriteLine($"[Tabs] {fname}: assembly not found.");
                        continue;
                    }

                    var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(asmPath);

                    Type? t = null;
                    if (!string.IsNullOrWhiteSpace(manifest.type))
                    {
                        t = asm.GetType(manifest.type!, throwOnError: false, ignoreCase: false);
                        if (t == null)
                        {
                            Console.WriteLine($"[Tabs] {fname}: type not found in '{Path.GetFileName(asmPath)}': {manifest.type}");
                            // fallthrough to auto-detect
                        }
                    }

                    if (t == null)
                    {
                        t = AutoDetectTabType(asm);
                        if (t != null)
                        {
                            Console.WriteLine($"[Tabs] {fname}: auto-detected tab type '{t.FullName}'.");
                        }
                        else
                        {
                            Console.WriteLine($"[Tabs] {fname}: no ISettingsTab found → skipped.");
                            continue;
                        }
                    }

                    var raw = Activator.CreateInstance(t);
                    ISettingsTab? inst = raw as ISettingsTab;
                    if (inst == null) inst = new ReflectionTabAdapter(raw!);

                    Tabs.Add(new TabHandle
                    {
                        FolderPath = folder,
                        FolderName = fname,
                        Manifest = manifest,
                        Instance = inst,
                        Assembly = asm,
                    });

                    var title = inst.Title ?? manifest.displayName ?? manifest.name ?? fname;
                    Console.WriteLine($"[Tabs] loaded: {title} ({Path.GetFileName(asmPath)})");
                }
                catch (BadImageFormatException bif)
                {
                    Console.WriteLine($"[Tabs] {fname} load error (BadImageFormat): {bif.Message}");
                }
                catch (FileLoadException fle)
                {
                    Console.WriteLine($"[Tabs] {fname} load error (FileLoad): {fle.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Tabs] {fname} load error: {ex.Message}");
                }
            }

            if (Tabs.Count == 0) Console.WriteLine("[Tabs] none loaded.");
        }

        private static Type? AutoDetectTabType(Assembly asm)
        {
            try
            {
                foreach (var t in asm.GetExportedTypes())
                {
                    if (t.IsAbstract || !t.IsClass) continue;
                    if (typeof(ISettingsTab).IsAssignableFrom(t)) return t;

                    // Duck-type fallback: Id (string), Title (string), CreateControl(): Control
                    var idProp    = t.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
                    var titleProp = t.GetProperty("Title", BindingFlags.Instance | BindingFlags.Public);
                    var cc        = t.GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.Public, new Type[0]);

                    if (idProp?.PropertyType == typeof(string) &&
                        titleProp?.PropertyType == typeof(string) &&
                        cc is not null && typeof(Control).IsAssignableFrom(cc.ReturnType))
                    {
                        return t;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool TryResolveAssemblyPath(string folder, string asmRelativeOrName, out string fullPath)
        {
            var candidate = Path.Combine(folder, asmRelativeOrName);
            if (File.Exists(candidate)) { fullPath = candidate; return true; }

            var fileName = Path.GetFileName(asmRelativeOrName);
            var found = Directory.GetFiles(folder, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(found)) { fullPath = found; return true; }

            var anyDll = Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories)
                                  .OrderByDescending(File.GetLastWriteTimeUtc)
                                  .FirstOrDefault();
            if (!string.IsNullOrEmpty(anyDll))
            {
                Console.WriteLine($"[Tabs] {Path.GetFileName(folder)}: using discovered DLL '{Path.GetFileName(anyDll)}'.");
                fullPath = anyDll;
                return true;
            }

            fullPath = string.Empty;
            return false;
        }
    }

    // ---------- WidgetInfo (unchanged; kept so tabs can use it if needed) ----------
    public sealed class WidgetInfo
    {
        public string Id = "";
        public string DisplayName = "";
        public string Version = "";
        public string FolderPath = "";
        public string AssemblyPath = "";
        public string? TypeName;
        public bool Enabled;
        public Dictionary<string, object?> EffectiveSettings = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object?> UserSettings = new(StringComparer.OrdinalIgnoreCase);
        public Image? Icon;
    }
}
