using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace CronatorWidgets.Clock
{
    // MUST be public so the host can see it via reflection
    public sealed class ClockWidget
    {
        // settings with defaults
        private string _format = "HH:mm:ss";
        private string _position = "UpperRight";
        private float  _fontSize = 0f; // 0 = auto
        private Color  _color    = Color.White;
        private bool   _shadow   = true;

        // Host calls this once right after loading (best-effort)
        public void ApplySettings(Dictionary<string, object?> settings)
        {
            if (settings == null) return;

            if (TryGet(settings, "format", out string? fmt) && !string.IsNullOrWhiteSpace(fmt))
                _format = fmt;

            if (TryGet(settings, "position", out string? pos) && !string.IsNullOrWhiteSpace(pos))
                _position = pos;

            if (TryGet(settings, "fontSize", out double dsz))
                _fontSize = (float)dsz;
            else if (TryGet(settings, "fontSize", out float fsz))
                _fontSize = fsz;

            if (TryGet(settings, "color", out string? hex) && !string.IsNullOrWhiteSpace(hex))
                _color = ParseColor(hex) ?? _color;

            if (TryGet(settings, "shadow", out bool sh))
                _shadow = sh;
        }

        // Host calls this every frame
        public void Draw(Graphics g, Rectangle monitorRect, Rectangle virt, DateTime now)
        {
            string text = now.ToString(_format);

            // Translate so (0,0) == top-left of the selected monitor region
            int ox = monitorRect.Left - virt.Left;
            int oy = monitorRect.Top  - virt.Top;

            var state = g.Save();
            g.TranslateTransform(ox, oy);

            float size = _fontSize > 0 ? _fontSize : Math.Max(24, monitorRect.Width / 16f);
            using var font   = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush  = new SolidBrush(_color);
            using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));

            // measure
            var sz = g.MeasureString(text, font);
            int margin = Math.Max(16, monitorRect.Width / 80);

            float x = monitorRect.Width - margin - sz.Width; // default UpperRight in monitor space
            float y = margin;

            switch ((_position ?? "UpperRight").Trim())
            {
                case "UpperLeft":  x = margin; y = margin; break;
                case "UpperRight": x = monitorRect.Width  - margin - sz.Width;  y = margin; break;
                case "LowerLeft":  x = margin; y = monitorRect.Height - margin - sz.Height; break;
                case "LowerRight": x = monitorRect.Width  - margin - sz.Width;  y = monitorRect.Height - margin - sz.Height; break;
                case "Center":     x = (monitorRect.Width  - sz.Width)  / 2f;
                                y = (monitorRect.Height - sz.Height) / 2f; break;
            }

            // Optional backing panel for visibility (enable via settings: "bg": true)
            bool showBg = false;
            // if you want this configurable, capture it in ApplySettings like _bgPanel
            // and set showBg = _bgPanel;
            if (showBg)
            {
                var panel = new RectangleF(x - margin/2f, y - margin/3f, sz.Width + margin, sz.Height + margin/2f);
                using var panelBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                using var panelPen   = new Pen(Color.FromArgb(160, 255, 255, 255), Math.Max(1f, size/32f));
                g.FillRectangle(panelBrush, panel);
                g.DrawRectangle(panelPen, panel.X, panel.Y, panel.Width, panel.Height);
            }

            // Shadow + text
            if (_shadow) g.DrawString(text, font, shadow, x + 1, y + 1);
            g.DrawString(text, font, brush, x, y);

            g.Restore(state);
        }


        // ---- helpers --------------------------------------------------------

        private static bool TryGet<T>(Dictionary<string, object?> dict, string key, out T value)
        {
            if (dict.TryGetValue(key, out var raw) && raw is not null)
            {
                try
                {
                    // Fast paths
                    if (raw is T t) { value = t; return true; }

                    // Convert numerics / bool / string as needed
                    var target = typeof(T);
                    if (target == typeof(string))        { value = (T)(object)raw.ToString()!; return true; }
                    if (target == typeof(bool) && bool.TryParse(raw.ToString(), out var b))
                    { value = (T)(object)b; return true; }
                    if ((target == typeof(float) || target == typeof(double)) &&
                        double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        value = target == typeof(float) ? (T)(object)(float)d : (T)(object)d;
                        return true;
                    }
                }
                catch { /* ignore */ }
            }
            value = default!;
            return false;
        }

        private static Color? ParseColor(string hex)
        {
            // Accept #RRGGBB or #AARRGGBB
            string s = hex.Trim().TrimStart('#');
            if (s.Length == 6)
            {
                int r = int.Parse(s.Substring(0, 2), NumberStyles.HexNumber);
                int g = int.Parse(s.Substring(2, 2), NumberStyles.HexNumber);
                int b = int.Parse(s.Substring(4, 2), NumberStyles.HexNumber);
                return Color.FromArgb(255, r, g, b);
            }
            if (s.Length == 8)
            {
                int a = int.Parse(s.Substring(0, 2), NumberStyles.HexNumber);
                int r = int.Parse(s.Substring(2, 2), NumberStyles.HexNumber);
                int g = int.Parse(s.Substring(4, 2), NumberStyles.HexNumber);
                int b = int.Parse(s.Substring(6, 2), NumberStyles.HexNumber);
                return Color.FromArgb(a, r, g, b);
            }
            return null;
        }
    }
}
