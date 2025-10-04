using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Collections.Generic;

namespace ClockWidgetNS
{
    /// <summary>
    /// Host can (optionally) inject these delegates via reflection:
    ///   - set ClockWidgetNS.WidgetLog.Info/Warn/Error = (msg) => Cronator.Log.*/Console.WriteLine...
    /// If not set, calls are no-ops. This keeps the widget decoupled from the host.
    /// </summary>
    internal static class WidgetLog
    {
        public static Action<string> Info  = s => Console.WriteLine(s);
        public static Action<string> Warn  = s => Console.WriteLine("WARN: " + s);
        public static Action<string> Error = s => Console.Error.WriteLine("ERR: " + s);

        public static void I(string msg) => Info(msg);
        public static void W(string msg) => Warn(msg);
        public static void E(string msg) => Error(msg);
    }

    public sealed class ClockWidget
    {
        // ---- settings (with safe defaults) ----
        private string _format   = "HH:mm:ss";
        private float  _fontPx   = 120f;         // base size in pixels (96-DPI logical)
        private float  _scale    = 1.0f;         // multiplier applied on top of fontPx
        private string _anchor   = "top-right";  // top-left|top-right|bottom-left|bottom-right|center
        private int    _offsetX  = -40;          // px from anchor (positive to the right)
        private int    _offsetY  = 40;           // px from anchor (positive downward)
        private bool   _autoFit  = false;        // shrink to fit below max box if true
        private float  _maxBoxPctW = 0.40f;      // 40% of monitor width
        private float  _maxBoxPctH = 0.20f;      // 20% of monitor height
        private Color  _color    = Color.White;
        private Color  _shadow   = Color.FromArgb(160, 0, 0, 0);
        private bool   _drawBg   = false;        // optional soft background panel
        private Color  _bgColor  = Color.FromArgb(128, 0, 0, 0);
        private Color  _bgBorder = Color.FromArgb(180, 255, 255, 255);

        // ---- normalized placement (from Settings Monitors tab) ----
        private bool  _useNorm = false;    // true when any of nx/ny/nw/nh is provided
        private float _nx, _ny, _nw, _nh;  // normalized in [0..1] relative to monitor

        // throttle logging so we don't spam once per tick
        private static DateTime _lastDrawLog = DateTime.MinValue;

        // ---- settings loader ----
        public void ApplySettings(Dictionary<string, object?> s)
        {
            if (s is null)
            {
                WidgetLog.W("ApplySettings called with null dictionary.");
                return;
            }

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

            // ---- normalized rectangle (if present, overrides anchor/offset/autofit box) ----
            bool anyNorm = false;
            if (TryGetF(s, "nx", out float nx)) { _nx = Clamp01(nx); anyNorm = true; }
            if (TryGetF(s, "ny", out float ny)) { _ny = Clamp01(ny); anyNorm = true; }
            if (TryGetF(s, "nw", out float nw)) { _nw = Math.Max(0.01f, Clamp01(nw)); anyNorm = true; }
            if (TryGetF(s, "nh", out float nh)) { _nh = Math.Max(0.01f, Clamp01(nh)); anyNorm = true; }
            _useNorm = anyNorm;

            WidgetLog.I(
                $"ApplySettings: format='{_format}', fontPx={_fontPx}, scale={_scale}, " +
                $"anchor='{_anchor}', off=({_offsetX},{_offsetY}), autoFit={_autoFit}, " +
                $"maxBoxPct=({_maxBoxPctW:P0},{_maxBoxPctH:P0}), color={_color}, bg={_drawBg}; " +
                $"useNorm={_useNorm} nx={_nx:0.###} ny={_ny:0.###} nw={_nw:0.###} nh={_nh:0.###}");
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

            // If we have a normalized rect, we auto-fit inside that rect by default.
            float maxW = 0, maxH = 0;
            if (_useNorm)
            {
                maxW = monitorRect.Width  * _nw;
                maxH = monitorRect.Height * _nh;

                if (textSize.Width > 0.1f && textSize.Height > 0.1f)
                {
                    float ratioW = maxW / textSize.Width;
                    float ratioH = maxH / textSize.Height;
                    float shrink = Math.Min(ratioW, ratioH);
                    if (shrink < 1f) sizePx = Math.Max(8f * dpiScale, sizePx * shrink);
                }
            }
            else if (_autoFit)
            {
                maxW = monitorRect.Width  * _maxBoxPctW;
                maxH = monitorRect.Height * _maxBoxPctH;

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

            // Position
            float x, y;
            string posMode;
            if (_useNorm)
            {
                // Top-left of the normalized rect relative to monitor
                x = monitorRect.Width  * _nx;
                y = monitorRect.Height * _ny;
                posMode = $"norm({_nx:0.###},{_ny:0.###},{_nw:0.###},{_nh:0.###}) max=({maxW:0.#},{maxH:0.#})";
            }
            else
            {
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

                // offsets only apply for anchor-mode
                x += _offsetX;
                y += _offsetY;
                posMode = $"anchor='{_anchor}' off=({_offsetX},{_offsetY})";
            }

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

            // Throttled draw log (every ~2 seconds)
            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastDrawLog).TotalSeconds >= 2)
            {
                _lastDrawLog = nowUtc;
                var finalRect = new RectangleF(x, y, size.Width, size.Height);
                WidgetLog.I(
                    $"Draw: virt={virt} monitor={monitorRect} dpiScale={dpiScale:0.###} " +
                    $"text='{text}' measured=({textSize.Width:0.#}x{textSize.Height:0.#}) " +
                    $"fontPx={sizePx:0.#} mode={posMode} finalRectPx={finalRect}");
            }

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
