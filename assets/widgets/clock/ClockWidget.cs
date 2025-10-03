using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Collections.Generic;

namespace ClockWidgetNS
{
    public sealed class ClockWidget
    {
        // ---- settings (with safe defaults) ----
        private string _format   = "HH:mm:ss";
        private float  _fontPx   = 120f;      // base size in pixels (96-DPI logical)
        private float  _scale    = 1.0f;      // multiplier applied on top of fontPx
        private string _anchor   = "top-right"; // top-left|top-right|bottom-left|bottom-right|center
        private int    _offsetX  = -40;       // px from anchor (positive to the right)
        private int    _offsetY  = 40;        // px from anchor (positive downward)
        private bool   _autoFit  = false;     // shrink to fit below max box if true
        private float  _maxBoxPctW = 0.40f;   // 40% of monitor width
        private float  _maxBoxPctH = 0.20f;   // 20% of monitor height
        private Color  _color    = Color.White;
        private Color  _shadow   = Color.FromArgb(160, 0, 0, 0);
        private bool   _drawBg   = false;     // optional soft background panel
        private Color  _bgColor  = Color.FromArgb(128, 0, 0, 0);
        private Color  _bgBorder = Color.FromArgb(180, 255, 255, 255);

        // ---- settings loader ----
        public void ApplySettings(Dictionary<string, object?> s)
        {
            if (s is null) return;

            if (TryGet(s, "format", out string? fmt) && !string.IsNullOrWhiteSpace(fmt)) _format = fmt;

            if (TryGetF(s, "fontPx", out float fpx)) _fontPx = Math.Max(8f, fpx);
            if (TryGetF(s, "scale",  out float sc))  _scale  = Math.Max(0.1f, sc);

            if (TryGet(s, "anchor", out string? anc) && !string.IsNullOrWhiteSpace(anc))
                _anchor = anc.Trim().ToLowerInvariant();

            if (TryGetI(s, "offsetX", out int ox)) _offsetX = ox;
            if (TryGetI(s, "offsetY", out int oy)) _offsetY = oy;

            if (TryGetB(s, "autoFit", out bool af)) _autoFit = af;
            if (TryGetF(s, "maxBoxPctW", out float pw)) _maxBoxPctW = Clamp01(pw);
            if (TryGetF(s, "maxBoxPctH", out float ph)) _maxBoxPctH = Clamp01(ph);

            if (TryGet(s, "color", out string? c1) && TryParseColor(c1!, out var col)) _color = Color.FromArgb(255, col);
            if (TryGet(s, "shadow", out string? c2) && TryParseColor(c2!, out var shd)) _shadow = shd;

            if (TryGetB(s, "bg", out bool bg)) _drawBg = bg;
            if (TryGet(s, "bgColor", out string? bc) && TryParseColor(bc!, out var bcol)) _bgColor = bcol;
            if (TryGet(s, "bgBorder", out string? bbo) && TryParseColor(bbo!, out var bb)) _bgBorder = bb;
        }

        // ---- draw ----
        public void Draw(Graphics g, Rectangle monitorRect, Rectangle virt, DateTime now)
        {
            // Translate so (0,0) is the monitor's top-left in the big span
            int ox = monitorRect.Left - virt.Left;
            int oy = monitorRect.Top  - virt.Top;

            var state = g.Save();
            g.TranslateTransform(ox, oy);

            // high quality text
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
            g.CompositingMode    = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit;

            // time text
            string text = now.ToString(string.IsNullOrWhiteSpace(_format) ? "HH:mm:ss" : _format);

            // compute target font size in device px (respect DPI)
            float dpiScale = g.DpiY / 96f;
            float sizePx = Math.Max(8f * dpiScale, _fontPx * _scale * dpiScale);

            // first measure with a probe font
            SizeF textSize;
            using (var fProbe = new Font("Segoe UI", sizePx, FontStyle.Bold, GraphicsUnit.Pixel))
                textSize = g.MeasureString(text, fProbe, int.MaxValue);

            // optional auto-fit inside a percentage box of the monitor area
            if (_autoFit)
            {
                float maxW = monitorRect.Width  * _maxBoxPctW;
                float maxH = monitorRect.Height * _maxBoxPctH;
                if (textSize.Width > 0.1f && textSize.Height > 0.1f)
                {
                    float ratioW = maxW / textSize.Width;
                    float ratioH = maxH / textSize.Height;
                    float shrink = Math.Min(1f, Math.Min(ratioW, ratioH)); // <= 1
                    sizePx = Math.Max(8f * dpiScale, sizePx * shrink);
                }
            }

            // final font + size
            using var font   = new Font("Segoe UI", sizePx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush  = new SolidBrush(_color);
            using var shBr   = new SolidBrush(_shadow);

            var size = g.MeasureString(text, font);

            // anchor
            float x, y;
            switch (_anchor)
            {
                case "top-left":
                case "upperleft":
                    x = 0; y = 0; break;

                case "top-right":
                case "upperright":
                    x = monitorRect.Width - size.Width; y = 0; break;

                case "bottom-left":
                case "lowerleft":
                    x = 0; y = monitorRect.Height - size.Height; break;

                case "bottom-right":
                case "lowerright":
                    x = monitorRect.Width - size.Width; y = monitorRect.Height - size.Height; break;

                case "center":
                    x = (monitorRect.Width  - size.Width)  / 2f;
                    y = (monitorRect.Height - size.Height) / 2f; break;

                default: // fallback
                    x = monitorRect.Width - size.Width; y = 0; break;
            }

            // offsets
            x += _offsetX;
            y += _offsetY;

            // optional background panel
            if (_drawBg)
            {
                float padX = Math.Max(10f, sizePx * 0.25f);
                float padY = Math.Max(6f,  sizePx * 0.15f);
                var panel = new RectangleF(x - padX, y - padY, size.Width + padX * 2, size.Height + padY * 2);
                using var bg  = new SolidBrush(_bgColor);
                using var pen = new Pen(_bgBorder, Math.Max(1f, sizePx / 28f));
                g.FillRectangle(bg, panel);
                g.DrawRectangle(pen, panel.X, panel.Y, panel.Width, panel.Height);
            }

            // shadow + text
            g.DrawString(text, font, shBr, x + 2, y + 2);
            g.DrawString(text, font, brush, x, y);

            g.Restore(state);
        }

        // ---- helpers ----
        private static bool TryGet<T>(Dictionary<string, object?> s, string key, out T? value)
        {
            if (s.TryGetValue(key, out var v) && v is T t) { value = t; return true; }
            value = default; return false;
        }
        private static bool TryGetF(Dictionary<string, object?> s, string key, out float f)
        {
            f = 0;
            if (!s.TryGetValue(key, out var v) || v is null) return false;
            try { f = Convert.ToSingle(v); return true; } catch { return false; }
        }
        private static bool TryGetI(Dictionary<string, object?> s, string key, out int i)
        {
            i = 0;
            if (!s.TryGetValue(key, out var v) || v is null) return false;
            try { i = Convert.ToInt32(v); return true; } catch { return false; }
        }
        private static bool TryGetB(Dictionary<string, object?> s, string key, out bool b)
        {
            b = false;
            if (!s.TryGetValue(key, out var v) || v is null) return false;
            try { b = Convert.ToBoolean(v); return true; } catch { return false; }
        }

        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private static bool TryParseColor(string s, out Color c)
        {
            try
            {
                s = s.Trim();
                if (s.StartsWith("#", StringComparison.Ordinal))
                {
                    if (s.Length == 7)       // #RRGGBB
                        c = Color.FromArgb(255,
                            Convert.ToInt32(s.Substring(1,2), 16),
                            Convert.ToInt32(s.Substring(3,2), 16),
                            Convert.ToInt32(s.Substring(5,2), 16));
                    else if (s.Length == 9)  // #AARRGGBB
                        c = Color.FromArgb(
                            Convert.ToInt32(s.Substring(1,2), 16),
                            Convert.ToInt32(s.Substring(3,2), 16),
                            Convert.ToInt32(s.Substring(5,2), 16),
                            Convert.ToInt32(s.Substring(7,2), 16));
                    else { c = Color.White; return false; }
                    return true;
                }
                c = Color.FromName(s);
                if (c.A == 0 && !s.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
                { c = Color.White; return false; }
                return true;
            }
            catch { c = Color.White; return false; }
        }
    }
}
