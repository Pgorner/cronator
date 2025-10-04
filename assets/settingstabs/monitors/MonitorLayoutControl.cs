using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Cronator.Tabs.Monitors
{
    public sealed class MonitorLayoutControl : UserControl
    {
        private readonly ListBox _monList = new() { Dock = DockStyle.Left, Width = 220 };
        private readonly Panel _preview = new() { Dock = DockStyle.Fill, BackColor = Color.WhiteSmoke };
        private readonly Label _help = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new(8) };

        public MonitorLayoutControl()
        {
            BackColor = SystemColors.Window;

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Text = "Monitor Layout",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Padding = new Padding(12, 10, 12, 0)
            };

            Controls.Add(_preview);
            Controls.Add(_monList);
            Controls.Add(header);
            Controls.Add(_help);

            _monList.SelectedIndexChanged += (_, __) => _preview.Invalidate();
            _preview.Paint += Preview_Paint;

            LoadMonitors();
        }

        private void LoadMonitors()
        {
            _monList.Items.Clear();
            var screens = Screen.AllScreens.ToList();

            foreach (var s in screens)
            {
                var b = s.Bounds;
                _monList.Items.Add($"{b.Width}x{b.Height} {(s.Primary ? "(primary)" : "")}".Trim());
            }

            if (_monList.Items.Count > 0) _monList.SelectedIndex = 0;

            _help.Text = "Tip: future versions will let you assign different widgets/positions per monitor here.";
        }

        private void Preview_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.WhiteSmoke);

            var vs = SystemInformation.VirtualScreen;
            var rect = new Rectangle(10, 10, _preview.ClientSize.Width - 20, _preview.ClientSize.Height - 20);

            // draw virtual area
            using (var pen = new Pen(Color.DimGray, 2))
                g.DrawRectangle(pen, rect);

            var screens = Screen.AllScreens.ToList();
            if (screens.Count == 0) return;

            // scale screens to preview
            float sx = rect.Width / (float)vs.Width;
            float sy = rect.Height / (float)vs.Height;

            int sel = _monList.SelectedIndex;
            for (int i = 0; i < screens.Count; i++)
            {
                var b = screens[i].Bounds;
                var x = rect.Left + (int)((b.Left - vs.Left) * sx);
                var y = rect.Top + (int)((b.Top - vs.Top) * sy);
                var w = (int)(b.Width * sx);
                var h = (int)(b.Height * sy);

                var r = new Rectangle(x, y, w, h);
                var bg = (i == sel) ? Color.SteelBlue : Color.LightSteelBlue;
                using var brush = new SolidBrush(bg);
                using var pen = new Pen(Color.Black, 1);

                g.FillRectangle(brush, r);
                g.DrawRectangle(pen, r);

                var label = $"{i} {(screens[i].Primary ? "P" : "")}".Trim();
                var size = g.MeasureString(label, Font);
                g.DrawString(label, Font, Brushes.White, r.Left + (r.Width - size.Width) / 2, r.Top + (r.Height - size.Height) / 2);
            }
        }
    }
}
