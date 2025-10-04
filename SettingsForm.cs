using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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

                // Try to inject live monitor snapshots into Monitors tab types (if loaded)
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
            // Try again here in case assembly was loaded after BuildNavFromTabs
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


        /// <summary>
        /// Sets Cronator.SettingsTabs.Monitors.MonitorsTab.SnapshotProvider (if present)
        /// on the assembly that defines the selected tab.
        /// </summary>
        private void EnsureMonitorsSnapshotProvider(TabManager.TabHandle handle)
        {
            try
            {
                var asm = handle.Instance?.GetType().Assembly;
                if (asm == null) return;

                var tabType     = asm.GetType("Cronator.SettingsTabs.Monitors.MonitorsTab");
                var snapType    = asm.GetType("Cronator.SettingsTabs.Monitors.MonitorSnapshot");
                var widgetBoxTy = asm.GetType("Cronator.SettingsTabs.Monitors.WidgetBox");
                if (tabType == null || snapType == null || widgetBoxTy == null) return;

                var listSnapType = typeof(List<>).MakeGenericType(snapType);
                var funcType     = typeof(Func<>).MakeGenericType(listSnapType);

                var method = typeof(SettingsForm).GetMethod(
                    nameof(BuildMonitorSnapshotsObjects),
                    BindingFlags.NonPublic | BindingFlags.Static
                )!;
                var generic = method.MakeGenericMethod(snapType, widgetBoxTy);

                // Build a compatible Func<List<MonitorSnapshot>> for the tab’s types.
                var del = generic.CreateDelegate(funcType);

                var prop = tabType.GetProperty("SnapshotProvider",
                    BindingFlags.Public | BindingFlags.Static);
                prop?.SetValue(null, del);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Tabs] EnsureMonitorsSnapshotProvider error: " + ex.Message);
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
                // Scan every loaded tab assembly for a public static property named "SnapshotProvider"
                // whose type is Func<List<SomeMonitorSnapshotType>>. When found, we build a delegate
                // that returns List<ThatType> using our BuildMonitorSnapshotsObjects<TSnap,TBox>().
                foreach (var h in _tabs.Tabs)
                {
                    var asm = h.Instance?.GetType()?.Assembly;
                    if (asm == null) continue;

                    // Prefer known types (MonitorsInterop, MonitorsTab) first for clearer logs
                    var knownTypes = new[]
                    {
                        asm.GetType("Cronator.SettingsTabs.Monitors.MonitorsInterop"),
                        asm.GetType("Cronator.SettingsTabs.Monitors.MonitorsTab")
                    }.Where(t => t != null).ToArray();

                    IEnumerable<Type> exportedPlusKnown = knownTypes.Length > 0
                        ? knownTypes.Concat(asm.GetExportedTypes().Where(t => !knownTypes.Contains(t)))
                        : asm.GetExportedTypes();

                    var candidateProps = exportedPlusKnown
                        .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                        .Where(p => string.Equals(p.Name, "SnapshotProvider", StringComparison.Ordinal))
                        .ToList();

                    foreach (var prop in candidateProps)
                    {
                        var propType = prop.PropertyType;
                        if (!propType.IsGenericType) continue;

                        // Must be Func<...>
                        if (propType.GetGenericTypeDefinition() != typeof(Func<>)) continue;

                        // Return type must be List<TSnapshot>
                        var retType = propType.GetGenericArguments()[0];
                        if (!retType.IsGenericType || retType.GetGenericTypeDefinition() != typeof(List<>)) continue;

                        var snapType = retType.GetGenericArguments()[0]; // TSnapshot
                        if (snapType == null) continue;

                        // Find the WidgetBox type in the same namespace/assembly as the snapshot
                        // (the Monitors tab project defines both)
                        var wbType = asm.GetExportedTypes().FirstOrDefault(t =>
                            t.Namespace == snapType.Namespace && t.Name == "WidgetBox");

                        if (wbType == null)
                        {
                            // Try a looser search just in case
                            wbType = asm.GetExportedTypes().FirstOrDefault(t => t.Name == "WidgetBox");
                        }

                        if (wbType == null) continue; // can't build without WidgetBox

                        // Build a closed generic method: BuildMonitorSnapshotsObjects<TSnapshot, WidgetBox>()
                        var buildMethod = typeof(SettingsForm).GetMethod(
                            nameof(BuildMonitorSnapshotsObjects),
                            BindingFlags.NonPublic | BindingFlags.Static);

                        if (buildMethod == null) continue;

                        var closedBuilder = buildMethod.MakeGenericMethod(snapType, wbType);

                        // The closedBuilder signature is: static List<TSnapshot> Method()
                        // We need a delegate of type: Func<List<TSnapshot>>
                        var del = Delegate.CreateDelegate(propType, closedBuilder);

                        // Set the static property
                        prop.SetValue(null, del);
                        Console.WriteLine($"[Tabs] monitors: SnapshotProvider injected on {prop.DeclaringType?.FullName}.");
                        return; // injected once is enough
                    }
                }

                // If we get here, we didn’t find a property to set.
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

            var snBoundsProp = snType.GetProperty("Bounds")!;
            var snLabelProp = snType.GetProperty("Label")!;
            var snIsPrimaryProp = snType.GetProperty("IsPrimary")!;
            var snIndexProp = snType.GetProperty("Index")!;
            var snWidgetsProp = snType.GetProperty("Widgets")!; // List<WidgetBox>

            var wbRectProp = wbType.GetProperty("RectNorm")!;
            var wbNameProp = wbType.GetProperty("Name")!;
            var wbColorProp = wbType.GetProperty("Color")!;
            var wbEnabledProp = wbType.GetProperty("Enabled")!;

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
                    var userPath = Path.Combine(dir, "config.user.json");

                    string displayName = Path.GetFileName(dir);
                    bool enabled = false;

                    JsonElement? settings = null;

                    // Manifest (for name + default enabled?)
                    if (File.Exists(manifestPath))
                    {
                        using var j = JsonDocument.Parse(File.ReadAllText(manifestPath));
                        var ro = j.RootElement;

                        if (ro.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                            displayName = dn.GetString() ?? displayName;

                        if (ro.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                            enabled = en.GetBoolean();
                    }

                    // User config overrides enable + settings
                    if (File.Exists(userPath))
                    {
                        using var j = JsonDocument.Parse(File.ReadAllText(userPath));
                        var ro = j.RootElement;

                        if (ro.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                            enabled = en.GetBoolean();

                        if (ro.TryGetProperty("settings", out var set))
                            settings = set;
                    }

                    if (!enabled) continue; // only show enabled widgets

                    // Determine monitor index (monitor|screen|display)
                    int mon = 0;
                    if (TryGetInt(settings, "monitor", out var m0)) mon = m0;
                    else if (TryGetInt(settings, "screen", out var m1)) mon = m1;
                    else if (TryGetInt(settings, "display", out var m2)) mon = m2;

                    if (mon < 0 || mon >= screens.Length) mon = 0;

                    // Determine rectangle
                    RectangleF rectN = new RectangleF(0.05f, 0.05f, 0.20f, 0.12f); // sensible default

                    // Prefer normalized if present (nx,ny,nw,nh)
                    bool hasNorm =
                        TryGetFloat(settings, "nx", out var nx) |
                        TryGetFloat(settings, "ny", out var ny) |
                        TryGetFloat(settings, "nw", out var nw) |
                        TryGetFloat(settings, "nh", out var nh);

                    if (hasNorm)
                    {
                        rectN = new RectangleF(
                            Clamp01(nx.GetValueOrDefault(0.05f)),
                            Clamp01(ny.GetValueOrDefault(0.05f)),
                            Clamp01(nw.GetValueOrDefault(0.20f)),
                            Clamp01(nh.GetValueOrDefault(0.12f)));
                    }
                    else
                    {
                        // Try pixel coords (x,y,w,h) or (left,top,width,height)
                        var s = screens[mon].Bounds;
                        float xPx = GetFirstFloat(settings, "x", "left").GetValueOrDefault(s.X + s.Width * 0.05f);
                        float yPx = GetFirstFloat(settings, "y", "top").GetValueOrDefault(s.Y + s.Height * 0.05f);
                        float wPx = GetFirstFloat(settings, "w", "width").GetValueOrDefault(s.Width * 0.20f);
                        float hPx = GetFirstFloat(settings, "h", "height").GetValueOrDefault(s.Height * 0.12f);

                        rectN = new RectangleF(
                            Clamp01((xPx - s.Left) / Math.Max(1, s.Width)),
                            Clamp01((yPx - s.Top) / Math.Max(1, s.Height)),
                            Clamp01(wPx / Math.Max(1, s.Width)),
                            Clamp01(hPx / Math.Max(1, s.Height)));
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

        private static bool TryGetInt(JsonElement? settings, string key, out int value)
        {
            value = 0;
            if (settings is null || settings.Value.ValueKind != JsonValueKind.Object) return false;
            if (!settings.Value.TryGetProperty(key, out var el)) return false;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) { value = i; return true; }
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var j)) { value = j; return true; }
            return false;
        }

        private static bool TryGetFloat(JsonElement? settings, string key, out float? value)
        {
            value = null;
            if (settings is null || settings.Value.ValueKind != JsonValueKind.Object) return false;
            if (!settings.Value.TryGetProperty(key, out var el)) return false;
            if (el.ValueKind == JsonValueKind.Number) { value = el.GetSingle(); return true; }
            if (el.ValueKind == JsonValueKind.String && float.TryParse(el.GetString(), out var f)) { value = f; return true; }
            return false;
        }

        private static float? GetFirstFloat(JsonElement? settings, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (TryGetFloat(settings, k, out var v) && v.HasValue) return v.Value;
            }
            return null;
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
                        Instance = inst
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
                    var idProp = t.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
                    var titleProp = t.GetProperty("Title", BindingFlags.Instance | BindingFlags.Public);
                    var cc = t.GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.Public, new Type[0]);

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
