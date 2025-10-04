using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Text.Json;

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

        private readonly List<WidgetInfo> _widgets; // still available for tabs that want it
        private Control? _currentPage;

        // NEW: dynamic tabs
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

            // Load tabs from disk
            BuildNavFromTabs();
        }

        // add near the top of SettingsForm.cs
        private static string? FindTabAssembly(string tabFolder, string assemblyFileName)
        {
            var candidates = new[]
            {
                Path.Combine(tabFolder, assemblyFileName),                         // assets/settingstabs/<tab>/MonitorsTab.dll
                Path.Combine(tabFolder, "net8.0-windows", assemblyFileName),       // assets/settingstabs/<tab>/net8.0-windows/MonitorsTab.dll
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assemblyFileName) // bin\<cfg>\net8.0-windows\MonitorsTab.dll (fallback)
            };

            foreach (var p in candidates)
                if (File.Exists(p)) return p;

            return null;
        }


        private void BuildNavFromTabs()
        {
            _nav.Nodes.Clear();

            try
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "settingstabs");
                _tabs.LoadFromFolder(root);

                foreach (var t in _tabs.Tabs)
                {
                    // Only show tabs that actually loaded
                    var node = new TreeNode(t.Instance.Title ?? t.Manifest.displayName ?? t.Manifest.name ?? t.FolderName)
                    {
                        Tag = t
                    };
                    _nav.Nodes.Add(node);
                }

                _nav.ExpandAll();

                if (_nav.Nodes.Count > 0)
                {
                    _nav.SelectedNode = _nav.Nodes[0];
                }
                else
                {
                    // No tabs found: show a friendly placeholder
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
            var node = e.Node;
            if (node?.Tag is not TabManager.TabHandle handle)
            {
                ShowPage(new PlaceholderPage("Select a tab on the left."));
                return;
            }

            try
            {
                var ctrl = handle.Instance.CreateControl() ?? new PlaceholderPage("Tab has no content.");
                // Optional: Provide shared context to tabs via Tag or service locator:
                // ctrl.Tag = new { Widgets = _widgets };

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

    // ---------------- Tabs loader (mirrors your widget loader robustness) ----------------
    internal sealed class TabManager
    {
        public readonly List<TabHandle> Tabs = new();

        public sealed class TabManifest
        {
            public string? name { get; set; }          // e.g., "monitors"
            public string? displayName { get; set; }   // e.g., "Monitors"
            public string? kind { get; set; }          // "dll"
            public string? assembly { get; set; }      // "MonitorsTab.dll"
            public string? type { get; set; }          // "Cronator.Tabs.Monitors.MonitorsTab"
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
                _id = t.GetProperty("Id")!;
                _title = t.GetProperty("Title")!;
                _create = t.GetMethod("CreateControl")!;
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
                            // fall through to auto-detect
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
                    if (inst == null)
                    {
                        // Wrap with adapter if duck-typed
                        inst = new ReflectionTabAdapter(raw!);
                    }

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

// inside TabManager
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
            // 1) exact relative
            var candidate = Path.Combine(folder, asmRelativeOrName);
            if (File.Exists(candidate)) { fullPath = candidate; return true; }

            // 2) search exact file recursively (handles net8.0-windows subfolder etc.)
            var fileName = Path.GetFileName(asmRelativeOrName);
            var found = Directory.GetFiles(folder, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(found)) { fullPath = found; return true; }

            // 3) fallback: newest *.dll in folder tree
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

        public sealed class TabHandleComparer : IComparer<TabHandle>
        {
            public int Compare(TabHandle? x, TabHandle? y)
            {
                var sx = x?.Instance?.Title ?? x?.Manifest?.displayName ?? x?.Manifest?.name ?? x?.FolderName ?? "";
                var sy = y?.Instance?.Title ?? y?.Manifest?.displayName ?? y?.Manifest?.name ?? y?.FolderName ?? "";
                return StringComparer.OrdinalIgnoreCase.Compare(sx, sy);
            }
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
