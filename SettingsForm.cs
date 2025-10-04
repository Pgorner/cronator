using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Cronator
{
    public sealed class SettingsForm : Form
    {
        private readonly SplitContainer _split;
        private readonly TreeView _nav;
        private readonly Panel _contentHost;

        private readonly List<WidgetInfo> _widgets; // supply from your loader
        private Control? _currentPage;

        // Replace the existing ctor with this:
        public SettingsForm(List<WidgetInfo>? widgets = null)
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

            BuildNav();

            // default selection: Widgets → else first node
            var widgetsNode = _nav.Nodes.Cast<TreeNode?>()
                .FirstOrDefault(n => string.Equals(n?.Tag as string, "widgets", StringComparison.OrdinalIgnoreCase));
            if (widgetsNode is not null)
            {
                _nav.SelectedNode = widgetsNode;
            }
            else if (_nav.Nodes.Count > 0)
            {
                _nav.SelectedNode = _nav.Nodes[0];
            }
        }


        private void BuildNav()
        {
            _nav.Nodes.Clear();
            _nav.Nodes.Add(new TreeNode("General")  { Tag = "general"  });
            _nav.Nodes.Add(new TreeNode("Monitors") { Tag = "monitors" });
            _nav.Nodes.Add(new TreeNode("Widgets")  { Tag = "widgets"  });
            _nav.ExpandAll();
        }

        private void Nav_AfterSelect(object? sender, TreeViewEventArgs e)
        {
            var node = e.Node;
            if (node is null) { ShowPage(new PlaceholderPage("Select an item on the left.")); return; }

            var tag = node.Tag as string;
            if (string.IsNullOrWhiteSpace(tag)) { ShowPage(new PlaceholderPage("Select an item on the left.")); return; }

            switch (tag)
            {
                case "widgets":
                    ShowPage(new WidgetsGridPage(_widgets, OpenWidgetEditor));
                    break;
                case "general":
                    ShowPage(new PlaceholderPage("General settings coming soon."));
                    break;
                case "monitors":
                    ShowPage(new PlaceholderPage("Monitor layout & selection."));
                    break;
                default:
                    ShowPage(new PlaceholderPage("Select an item on the left."));
                    break;
            }
        }

        private void ShowPage(Control page)
        {
            if (_currentPage != null)
            {
                _contentHost.Controls.Remove(_currentPage);
                _currentPage.Dispose();
                _currentPage = null;
            }
            _currentPage = page;
            page.Dock = DockStyle.Fill;
            _contentHost.Controls.Add(page);
        }

        private void OpenWidgetEditor(WidgetInfo info)
        {
            ShowPage(new WidgetEditorPage(
                info ?? new WidgetInfo(),
                onApply: () =>
                {
                    WidgetPersistence.SaveUserConfig(info ?? new WidgetInfo());
                    try { Program.TrayUpdateOnce(); } catch { /* non-fatal */ }
                },
                onBack: () =>
                {
                    var widgetsNode = _nav.Nodes.Cast<TreeNode?>()
                        .FirstOrDefault(n => string.Equals(n?.Tag as string, "widgets", StringComparison.OrdinalIgnoreCase));
                    if (widgetsNode is not null)
                        _nav.SelectedNode = widgetsNode;
                    else
                        ShowPage(new WidgetsGridPage(_widgets, OpenWidgetEditor));
                }));
        }
    }

    // ---------- “Widgets” grid page ----------
    internal sealed class WidgetsGridPage : UserControl
    {
        private readonly ListView _lv;
        private readonly ImageList _images;
        private readonly List<WidgetInfo> _widgets;
        private readonly Action<WidgetInfo> _open;

        public WidgetsGridPage(List<WidgetInfo> widgets, Action<WidgetInfo> openEditor)
        {
            _widgets = widgets ?? new List<WidgetInfo>();
            _open = openEditor ?? (_ => { });

            var header = new Label
            {
                Text = "Widgets",
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Dock = DockStyle.Top,
                Padding = new Padding(12),
                Height = 44
            };
            Controls.Add(header);

            _images = new ImageList { ImageSize = new Size(48, 48), ColorDepth = ColorDepth.Depth32Bit };
            _lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.LargeIcon,
                LargeImageList = _images,
                BorderStyle = BorderStyle.None
            };

            _lv.ItemActivate += (s, e) =>
            {
                if (_lv.SelectedItems.Count == 0) return;
                var item = _lv.SelectedItems[0];
                if (item is null) return;
                if (item.Tag is not WidgetInfo info) return;
                _open(info);
            };

            Controls.Add(_lv);

            Populate();
        }

        private void Populate()
        {
            _lv.BeginUpdate();
            try
            {
                _lv.Items.Clear();
                _images.Images.Clear();

                foreach (var w in _widgets.OrderBy(x => x.DisplayName ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    var idx = _images.Images.Count;
                    _images.Images.Add(w.Icon ?? SystemIcons.Information.ToBitmap());

                    var it = new ListViewItem
                    {
                        Text = $"{(string.IsNullOrWhiteSpace(w.DisplayName) ? "(unnamed)" : w.DisplayName)} {(w.Enabled ? "✓" : "✕")}",
                        ImageIndex = idx,
                        Tag = w
                    };
                    _lv.Items.Add(it);
                }
            }
            finally
            {
                _lv.EndUpdate();
            }
        }
    }

    // ---------- Widget editor page ----------
    internal sealed class WidgetEditorPage : UserControl
    {
        private readonly WidgetInfo _info;
        private readonly PropertyGrid _grid;
        private readonly CheckBox _enabled;
        private readonly Button _apply;
        private readonly Button _back;

        private readonly DynamicSettingsObject _dynamicSettings;

        public WidgetEditorPage(WidgetInfo info, Action onApply, Action onBack)
        {
            _info = info ?? new WidgetInfo();
            onApply ??= static () => { };
            onBack  ??= static () => { };

            var header = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(12) };
            var icon = new PictureBox
            {
                Size = new Size(40, 40),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = _info.Icon ?? SystemIcons.Information.ToBitmap()
            };
            var title = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Text = $"{(_info.DisplayName ?? "(unnamed)")}  {_info.Version}"
            };
            _enabled = new CheckBox { AutoSize = true, Text = "Enabled", Checked = _info.Enabled, Left = 0, Top = 34 };

            var headLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = false };
            headLayout.Controls.Add(icon);
            headLayout.Controls.Add(new Panel { Width = 8 });
            headLayout.Controls.Add(new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Controls = { title, _enabled }
            });
            header.Controls.Add(headLayout);
            Controls.Add(header);

            _grid = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = false };
            Controls.Add(_grid);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12) };
            _apply = new Button { Text = "Apply", AutoSize = true };
            _back  = new Button { Text = "Back",  AutoSize = true, Left = 90 };

            // >>> IMPORTANT: create _dynamicSettings BEFORE wiring handlers that use it <<<
            _dynamicSettings = new DynamicSettingsObject(_info);
            _grid.SelectedObject = _dynamicSettings;

            _apply.Click += (s, e) =>
            {
                _info.Enabled = _enabled.Checked;

                _info.UserSettings ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                _dynamicSettings.CopyToDictionary(_info.UserSettings);

                _info.EffectiveSettings ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                _info.EffectiveSettings = WidgetPersistence.MergeSettings(_info);

                onApply();
            };
            _back.Click += (s, e) => onBack();

            footer.Controls.Add(_apply);
            footer.Controls.Add(_back);
            Controls.Add(footer);
        }
    }

    // ---------- PropertyGrid dynamic backing object ----------
    internal sealed class DynamicSettingsObject : ICustomTypeDescriptor
    {
        private readonly WidgetInfo _info;
        private readonly Dictionary<string, object?> _working = new(StringComparer.OrdinalIgnoreCase);

        public DynamicSettingsObject(WidgetInfo info)
        {
            _info = info ?? new WidgetInfo();

            _info.UserSettings      ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _info.EffectiveSettings ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in _info.UserSettings)
                _working[kv.Key ?? string.Empty] = kv.Value;

            foreach (var kv in _info.EffectiveSettings)
            {
                var key = kv.Key ?? string.Empty;
                if (!_working.ContainsKey(key))
                    _working[key] = kv.Value;
            }
        }

        public void CopyToDictionary(Dictionary<string, object?> target)
        {
            if (target is null) return;
            target.Clear();
            foreach (var kv in _working) target[kv.Key] = kv.Value;
        }

        public AttributeCollection GetAttributes() => AttributeCollection.Empty;
        public string GetClassName() => (_info.DisplayName ?? "Widget") + " Settings";
        public string GetComponentName() => _info.DisplayName ?? "Widget";
        public TypeConverter GetConverter() => new TypeConverter();
        public EventDescriptor? GetDefaultEvent() => null;
        public PropertyDescriptor? GetDefaultProperty() => null;
        public object? GetEditor(Type editorBaseType) => null;
        public EventDescriptorCollection GetEvents(Attribute[]? attributes) => EventDescriptorCollection.Empty;
        public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
        public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();

        public PropertyDescriptorCollection GetProperties()
        {
            var keys = _working.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Array.Sort(keys, StringComparer.OrdinalIgnoreCase);

            var props = new PropertyDescriptor[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                props[i] = new DictPropertyDescriptor(key, _working);
            }
            return new PropertyDescriptorCollection(props, readOnly: true);
        }

        public object? GetPropertyOwner(PropertyDescriptor? pd) => this;

        private sealed class DictPropertyDescriptor : PropertyDescriptor
        {
            private readonly string _key;
            private readonly Dictionary<string, object?> _dict;

            public DictPropertyDescriptor(string key, Dictionary<string, object?> dict)
                : base(key ?? string.Empty, BuildAttributes(key ?? string.Empty, dict))
            {
                _key = key ?? string.Empty;
                _dict = dict ?? throw new ArgumentNullException(nameof(dict));
            }

            public override bool CanResetValue(object? component) => false;
            public override Type ComponentType => typeof(DynamicSettingsObject);
            public override object? GetValue(object? component) => _dict.TryGetValue(_key, out var v) ? v : null;
            public override bool IsReadOnly => false;
            public override Type PropertyType => InferType(GetValue(null));
            public override void ResetValue(object? component) { }
            public override void SetValue(object? component, object? value)
            {
                _dict[_key] = value;
                OnValueChanged(component, EventArgs.Empty);
            }
            public override bool ShouldSerializeValue(object? component) => true;

            private static Attribute[] BuildAttributes(string key, Dictionary<string, object?> dict)
            {
                dict.TryGetValue(key, out var val);
                if (val is Color)
                    return new Attribute[] { new TypeConverterAttribute(typeof(ColorConverter)) };
                return Array.Empty<Attribute>();
            }

            private static Type InferType(object? v)
            {
                if (v is null) return typeof(string);
                return v.GetType();
            }
        }
    }

    // ---------- Placeholder simple page ----------
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

    // ---------- Widget persistence helpers ----------
    public static class WidgetPersistence
    {
        public static void SaveUserConfig(WidgetInfo info)
        {
            if (info is null) return;

            info.FolderPath ??= string.Empty;
            if (string.IsNullOrWhiteSpace(info.FolderPath) || !Directory.Exists(info.FolderPath)) return;

            info.UserSettings ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            var path = Path.Combine(info.FolderPath, "config.user.json");
            var payload = new
            {
                enabled = info.Enabled,
                settings = info.UserSettings
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static Dictionary<string, object?> MergeSettings(WidgetInfo info)
        {
            var effective = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (info.EffectiveSettings != null)
                foreach (var kv in info.EffectiveSettings) effective[kv.Key] = kv.Value;

            if (info.UserSettings != null)
                foreach (var kv in info.UserSettings) effective[kv.Key] = kv.Value;

            return effective;
        }
    }

    // ---------- WidgetInfo ----------
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
