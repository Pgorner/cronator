using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace ClockWidgetNS
{
    internal static class WidgetLog
    {
        public static Action<string> Info  = s => Console.WriteLine(s);
        public static Action<string> Warn  = s => Console.WriteLine("WARN: " + s);
        public static Action<string> Error = s => Console.Error.WriteLine("ERR: " + s);
        public static void I(string msg) => Info(msg);
        public static void W(string msg) => Warn(msg);
        public static void E(string msg) => Error(msg);
    }

    // ---- read-only proxy to Cronator.PlacementBus (via AppDomain bag) ----
    internal static class BusProxy
    {
        private const string MapKey = "Cronator.PlacementBus.Map";

        private static ConcurrentDictionary<string, RectangleF>? Map
            => AppDomain.CurrentDomain.GetData(MapKey) as ConcurrentDictionary<string, RectangleF>;

        private static string K(int? mon, string name)
            => (mon.HasValue ? $"m{mon.Value}:" : "*:") + (name ?? "").Trim().ToLowerInvariant();

        public static bool TryGet(int? mon, string name, out RectangleF rn)
        {
            rn = default;
            var map = Map; if (map is null) return false;
            if (mon.HasValue && map.TryGetValue(K(mon, name), out rn)) return true;
            if (map.TryGetValue(K(null, name), out rn)) return true;
            return false;
        }
    }
    // ---------------------------------------------------------------------

    public sealed class ClockWidget
    {
        // visuals
        private string _format   = "HH:mm:ss";
        private float  _fontPx   = 120f;
        private float  _scale    = 1.0f;
        private Color  _color    = Color.White;
        private Color  _shadow   = Color.FromArgb(160, 0, 0, 0);
        private bool   _drawBg   = false;
        private Color  _bgColor  = Color.FromArgb(128, 0, 0, 0);
        private Color  _bgBorder = Color.FromArgb(180, 255, 255, 255);
        private string _fontFamily = "Segoe UI";
        private FontStyle _fontStyle = FontStyle.Bold;

        // normalized placement (authoritative at render time)
        private float _nx = 0.72f, _ny = 0.03f, _nw = 0.22f, _nh = 0.13f;

        // context (so we can read the right bus entry)
        private int?  _ctxMonitor = null;
        private string _ctxName    = "clock";

        private static DateTime _lastDrawLog = DateTime.MinValue;

        public void ApplySettings(Dictionary<string, object?> s)
        {
            if (s is null) { WidgetLog.W("ApplySettings(null)"); return; }

            // visuals
            if (TryGet(s, "format", out string? fmt) && !string.IsNullOrWhiteSpace(fmt)) _format = fmt;
            if (TryGetF(s, "fontPx", out float fpx)) _fontPx = Math.Max(8f, fpx);
            if (TryGetF(s, "scale",  out float sc))  _scale  = Math.Max(0.1f, sc);
            if (TryGet(s, "fontFamily", out string? ff) && !string.IsNullOrWhiteSpace(ff)) _fontFamily = ff.Trim();
            if (TryGet(s, "fontStyle", out string? fs) && !string.IsNullOrWhiteSpace(fs)) _fontStyle = ParseFontStyle(fs);
            if (TryGetColor(s, "color", out var col))   _color = Color.FromArgb(255, col);
            if (TryGetColor(s, "shadow", out var shd))  _shadow = shd;
            if (TryGetB(s, "bg", out bool bg)) _drawBg = bg;
            if (TryGetColor(s, "bgColor", out var bcol)) _bgColor = bcol;
            if (TryGetColor(s, "bgBorder", out var bb))  _bgBorder = bb;

            // placement from settings (optional)
            bool sawNorm = false;
            if (TryGetF(s, "nx", out var nx)) { _nx = Clamp01(nx); sawNorm = true; }
            if (TryGetF(s, "ny", out var ny)) { _ny = Clamp01(ny); sawNorm = true; }
            if (TryGetF(s, "nw", out var nw)) { _nw = Math.Max(0.01f, Clamp01(nw)); sawNorm = true; }
            if (TryGetF(s, "nh", out var nh)) { _nh = Math.Max(0.01f, Clamp01(nh)); sawNorm = true; }

            // context injected by host (Program) — monitor + name
            if (TryGetF(s, "monitor", out var mAsFloat)) _ctxMonitor = (int)mAsFloat;
            else if (TryGet<int>(s, "monitor", out var mAsInt))      _ctxMonitor = mAsInt;
            if (TryGet(s, "name", out string? nm) && !string.IsNullOrWhiteSpace(nm)) _ctxName = nm.Trim();

            WidgetLog.I(
                $"ApplySettings: format='{_format}', fontPx={_fontPx}, scale={_scale}, " +
                $"color={_color}, bg={_drawBg}; font='{_fontFamily}', style={_fontStyle}; " +
                $"rectN=({_nx:0.###},{_ny:0.###},{_nw:0.###},{_nh:0.###}) (defaultsUsed={!sawNorm}) " +
                $"ctx: monitor={(_ctxMonitor?.ToString() ?? "null")}, name='{(_ctxName ?? "<null>")}'");
        }

        public void Draw(Graphics g, Rectangle monitorRect, Rectangle virt, DateTime now)
        {
            // pick up live placement every frame
            UpdateRectFromBus();

            // monitor-local origin
            g.TranslateTransform(monitorRect.Left - virt.Left, monitorRect.Top - virt.Top);

            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
            g.CompositingMode    = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit;

            string text = now.ToString(string.IsNullOrWhiteSpace(_format) ? "HH:mm:ss" : _format);

            float dpiScale = g.DpiY / 96f;
            float sizePx = Math.Max(8f * dpiScale, _fontPx * _scale * dpiScale);

            // create font, measure, shrink if needed, then recreate font actually used for draw
            SizeF measured;
            Font font = new Font(_fontFamily, sizePx, _fontStyle, GraphicsUnit.Pixel);
            try
            {
                measured = g.MeasureString(text, font);

                float maxW = monitorRect.Width  * _nw;
                float maxH = monitorRect.Height * _nh;

                if (measured.Width > 0.1f && measured.Height > 0.1f)
                {
                    float ratio = Math.Min(maxW / measured.Width, maxH / measured.Height);
                    if (ratio < 1f)
                    {
                        float newPx = Math.Max(8f * dpiScale, sizePx * ratio);
                        if (Math.Abs(newPx - sizePx) > 0.1f)
                        {
                            font.Dispose();
                            font = new Font(_fontFamily, newPx, _fontStyle, GraphicsUnit.Pixel);
                            measured = g.MeasureString(text, font);
                            sizePx = newPx;
                        }
                    }
                }

                float x = monitorRect.Width  * _nx;
                float y = monitorRect.Height * _ny;

                using var brush = new SolidBrush(_color);
                using var shBr  = new SolidBrush(_shadow);

                if (_drawBg)
                {
                    float padX = Math.Max(10f, sizePx * 0.25f);
                    float padY = Math.Max(6f,  sizePx * 0.15f);
                    var panel = new RectangleF(x - padX, y - padY, measured.Width + padX * 2, measured.Height + padY * 2);
                    using var bg  = new SolidBrush(_bgColor);
                    using var pen = new Pen(_bgBorder, Math.Max(1f, sizePx / 28f));
                    g.FillRectangle(bg, panel);
                    g.DrawRectangle(pen, panel.X, panel.Y, panel.Width, panel.Height);
                }

                g.DrawString(text, font, shBr, x + 2, y + 2);
                g.DrawString(text, font, brush, x, y);

                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - _lastDrawLog).TotalSeconds >= 2)
                {
                    _lastDrawLog = nowUtc;
                    var finalRect = new RectangleF(x, y, measured.Width, measured.Height);
                    WidgetLog.I(
                        $"Draw: virt={virt} monitor={monitorRect} dpiScale={dpiScale:0.###} " +
                        $"text='{text}' measured=({measured.Width:0.#}x{measured.Height:0.#}) " +
                        $"fontPx={sizePx:0.#} rectN=({_nx:0.###},{_ny:0.###},{_nw:0.###},{_nh:0.###}) " +
                        $"finalRectPx={finalRect}");
                }
            }
            finally
            {
                font.Dispose();
            }
        }

        private void UpdateRectFromBus()
        {
            // Try monitor-scoped first, fallback to name-only entry
            if (BusProxy.TryGet(_ctxMonitor, _ctxName ?? "clock", out var rn))
            {
                // Keep width/height from bus too (so we can enable resizing later)
                if (rn.X != _nx || rn.Y != _ny || rn.Width != _nw || rn.Height != _nh)
                {
                    _nx = Clamp01(rn.X);
                    _ny = Clamp01(rn.Y);
                    _nw = Math.Max(0.01f, Clamp01(rn.Width));
                    _nh = Math.Max(0.01f, Clamp01(rn.Height));
                }
            }
        }

        // ---------- helpers ----------
        private static bool TryGet<T>(Dictionary<string, object?> s, string key, out T? value)
        {
            value = default;
            if (!s.TryGetValue(key, out var v) || v is null) return false;

            try
            {
                if (v is T t) { value = t; return true; }

                if (v is JsonElement je)
                {
                    object? boxed = null;
                    switch (je.ValueKind)
                    {
                        case JsonValueKind.String:
                            var str = je.GetString();
                            if (typeof(T) == typeof(string)) { value = (T?)(object?)str; return true; }
                            boxed = str;
                            break;

                        case JsonValueKind.Number:
                            if (typeof(T) == typeof(int))     { value = (T?)(object)je.GetInt32();  return true; }
                            if (typeof(T) == typeof(long))    { value = (T?)(object)je.GetInt64();  return true; }
                            if (typeof(T) == typeof(float))   { value = (T?)(object)(float)je.GetDouble(); return true; }
                            if (typeof(T) == typeof(double))  { value = (T?)(object)je.GetDouble(); return true; }
                            if (typeof(T) == typeof(decimal)) { value = (T?)(object)je.GetDecimal(); return true; }
                            boxed = je.GetDouble();
                            break;

                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            if (typeof(T) == typeof(bool)) { value = (T?)(object)je.GetBoolean(); return true; }
                            boxed = je.GetBoolean();
                            break;

                        case JsonValueKind.Null:
                        case JsonValueKind.Undefined:
                            return false;

                        default:
                            return false;
                    }

                    if (boxed is not null)
                    {
                        value = (T?)Convert.ChangeType(boxed, typeof(T));
                        return true;
                    }
                }

                value = (T?)Convert.ChangeType(v, typeof(T));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetF(Dictionary<string, object?> s, string key, out float f)
        {
            f = 0;
            if (!s.TryGetValue(key, out var v) || v is null) return false;

            try
            {
                if (v is float ff) { f = ff; return true; }
                if (v is double dd) { f = (float)dd; return true; }
                if (v is int ii) { f = ii; return true; }
                if (v is long ll) { f = ll; return true; }
                if (v is string str && float.TryParse(str, System.Globalization.NumberStyles.Float,
                                                      System.Globalization.CultureInfo.InvariantCulture, out var pf))
                { f = pf; return true; }

                if (v is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number) { f = (float)je.GetDouble(); return true; }
                    if (je.ValueKind == JsonValueKind.String &&
                        float.TryParse(je.GetString(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var pf2))
                    { f = pf2; return true; }
                }
            }
            catch { }
            return false;
        }

        private static bool TryGetB(Dictionary<string, object?> s, string key, out bool b)
        {
            b = false;
            if (!s.TryGetValue(key, out var v) || v is null) return false;

            try
            {
                if (v is bool bb) { b = bb; return true; }
                if (v is string str && bool.TryParse(str, out var pb)) { b = pb; return true; }

                if (v is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.True)  { b = true;  return true; }
                    if (je.ValueKind == JsonValueKind.False) { b = false; return true; }
                    if (je.ValueKind == JsonValueKind.String &&
                        bool.TryParse(je.GetString(), out var pb2)) { b = pb2; return true; }
                }
            }
            catch { }
            return false;
        }

        private static bool TryGetColor(Dictionary<string, object?> s, string key, out Color c)
        {
            c = Color.White;
            if (!s.TryGetValue(key, out var v) || v is null) return false;

            if (v is string str) return TryParseColor(str, out c);

            if (v is JsonElement je && je.ValueKind == JsonValueKind.String)
                return TryParseColor(je.GetString() ?? "", out c);

            return false;
        }

        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private static FontStyle ParseFontStyle(string s)
        {
            FontStyle fs = 0;
            foreach (var part in s.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse<FontStyle>(part.Trim(), true, out var one)) fs |= one;
            return fs == 0 ? FontStyle.Regular : fs;
        }

        private static bool TryParseColor(string s, out Color c)
        {
            try
            {
                s = s.Trim();
                if (s.StartsWith("#", StringComparison.Ordinal))
                {
                    if (s.Length == 7)
                        c = Color.FromArgb(255,
                            Convert.ToInt32(s.Substring(1, 2), 16),
                            Convert.ToInt32(s.Substring(3, 2), 16),
                            Convert.ToInt32(s.Substring(5, 2), 16));
                    else if (s.Length == 9)
                        c = Color.FromArgb(
                            Convert.ToInt32(s.Substring(1, 2), 16),
                            Convert.ToInt32(s.Substring(3, 2), 16),
                            Convert.ToInt32(s.Substring(5, 2), 16),
                            Convert.ToInt32(s.Substring(7, 2), 16));
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
