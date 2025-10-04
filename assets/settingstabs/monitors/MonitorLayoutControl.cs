using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Cronator.SettingsTabs.Monitors
{
    public sealed class WidgetBox
    {
        public RectangleF RectNorm { get; set; }
        public string Name { get; set; } = "";
        public Color Color { get; set; } = Color.SteelBlue;
        public bool Enabled { get; set; } = true;
    }

    public sealed class MonitorSnapshot
    {
        public Rectangle Bounds { get; set; }
        public string Label { get; set; } = "";
        public bool IsPrimary { get; set; }
        public int Index { get; set; }
        public List<WidgetBox> Widgets { get; set; } = new();
    }

    internal static class RoundedRect
    {
        internal static GraphicsPath Create(Rectangle rect, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
        internal static void Fill(Graphics g, Brush brush, Rectangle rect, int radius)
        {
            using var gp = Create(rect, radius);
            g.FillPath(brush, gp);
        }
        internal static void Stroke(Graphics g, Pen pen, Rectangle rect, int radius)
        {
            using var gp = Create(rect, radius);
            g.DrawPath(pen, gp);
        }
    }

    public sealed class MonitorLayoutControl : Control
    {
        public MonitorSnapshot? Data { get; private set; }

        // Raised on every live change (drag/resize)
        public event Action<string /*widgetName*/, RectangleF /*newRectNorm*/>? WidgetChanged;

        private Rectangle _face; // drawable monitor face
        private readonly Dictionary<WidgetBox, Rectangle> _drawRects = new();

        private WidgetBox? _hot;
        private WidgetBox? _active;

        private Point _dragStart;
        private Rectangle _activeStartPx;

        private bool _isDragging;
        private bool _resizing;
        private bool _layoutBuilt;

        private enum HandleKind { None, N, S, E, W, NE, NW, SE, SW }
        private HandleKind _handle = HandleKind.None;

        private const int HandleSize = 11; // a little larger for easier grabbing

        public MonitorLayoutControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            Cursor = Cursors.Arrow;
            MouseMove  += OnMouseMove;
            MouseDown  += OnMouseDown;
            MouseUp    += OnMouseUp;
            MouseLeave += (_, __) => { if (!_isDragging) { _hot = null; _handle = HandleKind.None; Invalidate(); } };
        }

        public void SetMonitor(MonitorSnapshot? snapshot)
        {
            Data = snapshot;
            _hot = _active = null;
            _isDragging = false;
            _resizing = false;
            _layoutBuilt = false;
            _drawRects.Clear();
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            _layoutBuilt = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var bg = new SolidBrush(BackColor);
            g.FillRectangle(bg, ClientRectangle);

            EnsureLayoutBuilt();
            if (!_layoutBuilt || Data == null || _face.Width <= 0 || _face.Height <= 0)
            {
                DrawEmpty(g);
                return;
            }

            using (var faceBrush = new SolidBrush(Color.FromArgb(245, 245, 248)))
                RoundedRect.Fill(g, faceBrush, _face, 14);
            using (var borderPen = new Pen(Color.FromArgb(210, 214, 220), 2f))
                RoundedRect.Stroke(g, borderPen, _face, 14);

            // Title
            var title = $"{(Data!.IsPrimary ? "★ " : "")}Monitor {Data.Index}  {Data.Label}";
            using (var titleFont = new Font(Font, FontStyle.Bold))
            {
                var size = TextRenderer.MeasureText(title, titleFont, Size.Empty, TextFormatFlags.NoPadding);
                var pt = new Point((Width - size.Width) / 2, Math.Max(0, _face.Top - size.Height - 4));
                TextRenderer.DrawText(g, title, titleFont, pt, Color.FromArgb(30, 30, 36), TextFormatFlags.NoPadding);
            }

            // Widgets
            foreach (var (w, r) in _drawRects.ToArray())
            {
                if (!w.Enabled) continue;

                using (var b = new SolidBrush(Color.FromArgb(220, w.Color)))
                    RoundedRect.Fill(g, b, r, 8);
                using (var p = new Pen(Color.FromArgb(255, w.Color), 1.5f))
                    RoundedRect.Stroke(g, p, r, 8);

                // Label
                var label = string.IsNullOrWhiteSpace(w.Name) ? "Widget" : w.Name;
                var textRect = Rectangle.Inflate(r, -4, -4);
                using var f = new Font(Font.FontFamily, Math.Max(7f, Math.Min(10f, r.Height * 0.28f)), FontStyle.Regular, GraphicsUnit.Point);
                TextRenderer.DrawText(
                    g, label, f, textRect, Color.FromArgb(35, 35, 40),
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                // Selection adorners
                if (w == _active || w == _hot)
                {
                    using var selPen = new Pen(Color.FromArgb(60, 60, 75), 1.5f) { DashStyle = DashStyle.Dot };
                    g.DrawRectangle(selPen, r);
                    DrawHandles(g, r);
                }
            }

            DrawLegend(g, _face.Bottom + 10);
        }

        // ---------- Layout ----------

        private void EnsureLayoutBuilt()
        {
            if (_isDragging) return; // don't rebuild while dragging

            if (Data == null || Data.Bounds.Width <= 0 || Data.Bounds.Height <= 0)
            {
                _layoutBuilt = false;
                _face = Rectangle.Empty;
                _drawRects.Clear();
                return;
            }

            var monRect = GetScaledRect(Data.Bounds, ClientRectangle, 16);
            _face = Rectangle.Inflate(monRect, -2, -2);

            // (re)build rects if empty or count differs
            if (_drawRects.Count == 0 || _drawRects.Count != (Data.Widgets?.Count ?? 0))
            {
                _drawRects.Clear();
                if (Data.Widgets != null)
                {
                    foreach (var w in Data.Widgets)
                    {
                        if (!w.Enabled) continue;
                        _drawRects[w] = NormToFace(w.RectNorm);
                    }
                }
            }

            _layoutBuilt = true;
        }

        private Rectangle NormToFace(RectangleF rn)
        {
            int x = _face.Left + (int)Math.Round(rn.X * _face.Width);
            int y = _face.Top  + (int)Math.Round(rn.Y * _face.Height);
            int w = (int)Math.Round(Math.Max(0.01f, rn.Width)  * _face.Width);
            int h = (int)Math.Round(Math.Max(0.01f, rn.Height) * _face.Height);
            return new Rectangle(x, y, w, h);
        }

        private RectangleF FaceToNorm(Rectangle px)
        {
            float nx = Clamp01((px.Left - _face.Left) / Math.Max(1f, _face.Width));
            float ny = Clamp01((px.Top  - _face.Top ) / Math.Max(1f, _face.Height));
            float nw = Clamp01(px.Width  / Math.Max(1f, _face.Width));
            float nh = Clamp01(px.Height / Math.Max(1f, _face.Height));

            if (nx + nw > 1f) nx = 1f - nw;
            if (ny + nh > 1f) ny = 1f - nh;
            nw = Math.Max(0.01f, nw);
            nh = Math.Max(0.01f, nh);

            return new RectangleF(nx, ny, nw, nh);
        }

        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private void DrawEmpty(Graphics g)
        {
            using var dashed = new Pen(Color.Silver, 1.5f) { DashStyle = DashStyle.Dash };
            var r = Rectangle.Inflate(ClientRectangle, -20, -20);
            RoundedRect.Stroke(g, dashed, r, 12);

            const string txt = "No monitor selected.";
            var size = TextRenderer.MeasureText(txt, Font, Size.Empty, TextFormatFlags.NoPadding);
            var pt = new Point((Width - size.Width) / 2, (Height - size.Height) / 2);
            TextRenderer.DrawText(g, txt, Font, pt, Color.Gray, TextFormatFlags.NoPadding);
        }

        private static Rectangle GetScaledRect(Rectangle src, Rectangle dst, int padding)
        {
            var inner = Rectangle.Inflate(dst, -padding, -padding);
            if (inner.Width <= 0 || inner.Height <= 0 || src.Width <= 0 || src.Height <= 0)
                return Rectangle.Empty;

            float sx = (float)inner.Width / src.Width;
            float sy = (float)inner.Height / src.Height;
            float scale = Math.Min(sx, sy);

            int w = (int)Math.Round(src.Width * scale);
            int h = (int)Math.Round(src.Height * scale);
            int x = inner.Left + (inner.Width - w) / 2;
            int y = inner.Top + (inner.Height - h) / 2;

            return new Rectangle(x, y, w, h);
        }

        private void DrawLegend(Graphics g, int top)
        {
            var legendRect = new Rectangle(12, top, Width - 24, Math.Max(26, Height - top - 12));
            using var bg = new SolidBrush(Color.FromArgb(252, 252, 253));
            using var border = new Pen(Color.FromArgb(220, 224, 232), 1f);
            RoundedRect.Fill(g, bg, legendRect, 8);
            RoundedRect.Stroke(g, border, legendRect, 8);

            var text = "Legend: colored rounded boxes = widgets (drag to move, handles to resize).";
            var rect = Rectangle.Inflate(legendRect, -10, -6);
            TextRenderer.DrawText(g, text, new Font(Font, FontStyle.Regular), rect, Color.FromArgb(55, 60, 70),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // ---------- Interactions ----------

        private void OnMouseDown(object? sender, MouseEventArgs e)
        {
            if (Data == null || e.Button != MouseButtons.Left) return;

            EnsureLayoutBuilt(); // make sure rects exist for hit-test
            if (!_layoutBuilt) return;

            var (hit, handle) = HitTest(e.Location);
            _active = hit;
            _handle = handle;
            _resizing = _active != null && handle != HandleKind.None;
            _dragStart = e.Location;

            if (_active != null)
            {
                _activeStartPx = _drawRects[_active];
                _isDragging = true;
                Capture = true; // keep mouse even if cursor leaves control
            }

            Invalidate();
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            if (Data == null) return;

            if (_active != null && _isDragging && Capture)
            {
                int dx = e.X - _dragStart.X;
                int dy = e.Y - _dragStart.Y;
                var r = _activeStartPx;

                if (_resizing)
                {
                    switch (_handle)
                    {
                        case HandleKind.N:  r.Y = _activeStartPx.Y + dy; r.Height = _activeStartPx.Height - dy; break;
                        case HandleKind.S:  r.Height = _activeStartPx.Height + dy; break;
                        case HandleKind.W:  r.X = _activeStartPx.X + dx; r.Width  = _activeStartPx.Width  - dx; break;
                        case HandleKind.E:  r.Width  = _activeStartPx.Width  + dx; break;
                        case HandleKind.NE: r.Y = _activeStartPx.Y + dy; r.Height = _activeStartPx.Height - dy; r.Width = _activeStartPx.Width + dx; break;
                        case HandleKind.NW: r.Y = _activeStartPx.Y + dy; r.Height = _activeStartPx.Height - dy; r.X = _activeStartPx.X + dx; r.Width = _activeStartPx.Width - dx; break;
                        case HandleKind.SE: r.Width = _activeStartPx.Width + dx; r.Height = _activeStartPx.Height + dy; break;
                        case HandleKind.SW: r.X = _activeStartPx.X + dx; r.Width = _activeStartPx.Width - dx; r.Height = _activeStartPx.Height + dy; break;
                    }
                }
                else
                {
                    r.X += dx; r.Y += dy;
                }

                r = KeepInFace(r);
                _drawRects[_active] = r;

                // keep model in sync + notify
                _active.RectNorm = FaceToNorm(r);
                WidgetChanged?.Invoke(_active.Name, _active.RectNorm);

                Invalidate();
                return;
            }

            // hover feedback
            EnsureLayoutBuilt();
            var (hit2, handle2) = HitTest(e.Location);
            _hot = hit2;
            _handle = handle2;
            Cursor = CursorForHandle(handle2, hit2 != null);
            Invalidate();
        }

        private void OnMouseUp(object? sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                Capture = false;
            }
            _resizing = false;
            _handle = HandleKind.None;
        }

        private (WidgetBox? box, HandleKind handle) HitTest(Point p)
        {
            if (Data == null || !_layoutBuilt) return (null, HandleKind.None);

            // handles first
            foreach (var kv in _drawRects)
            {
                var h = HandleAtPoint(kv.Value, p);
                if (h != HandleKind.None) return (kv.Key, h);
            }
            // then body
            foreach (var kv in _drawRects)
            {
                if (kv.Value.Contains(p)) return (kv.Key, HandleKind.None);
            }
            return (null, HandleKind.None);
        }

        private HandleKind HandleAtPoint(Rectangle r, Point p)
        {
            var hs = HandleSize;
            var rects = new Dictionary<HandleKind, Rectangle>
            {
                { HandleKind.NW, new Rectangle(r.Left - hs, r.Top - hs, hs*2, hs*2) },
                { HandleKind.NE, new Rectangle(r.Right - hs, r.Top - hs, hs*2, hs*2) },
                { HandleKind.SW, new Rectangle(r.Left - hs, r.Bottom - hs, hs*2, hs*2) },
                { HandleKind.SE, new Rectangle(r.Right - hs, r.Bottom - hs, hs*2, hs*2) },
                { HandleKind.N,  new Rectangle(r.Left + r.Width/2 - hs, r.Top - hs, hs*2, hs*2) },
                { HandleKind.S,  new Rectangle(r.Left + r.Width/2 - hs, r.Bottom - hs, hs*2, hs*2) },
                { HandleKind.W,  new Rectangle(r.Left - hs, r.Top + r.Height/2 - hs, hs*2, hs*2) },
                { HandleKind.E,  new Rectangle(r.Right - hs, r.Top + r.Height/2 - hs, hs*2, hs*2) },
            };
            foreach (var h in rects)
                if (h.Value.Contains(p)) return h.Key;
            return HandleKind.None;
        }

        private void DrawHandles(Graphics g, Rectangle r)
        {
            var pts = new[]
            {
                new Point(r.Left, r.Top), new Point(r.Right, r.Top),
                new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom),
                new Point(r.Left + r.Width/2, r.Top),
                new Point(r.Left + r.Width/2, r.Bottom),
                new Point(r.Left, r.Top + r.Height/2),
                new Point(r.Right, r.Top + r.Height/2),
            };
            foreach (var p in pts)
            {
                var rect = new Rectangle(p.X-HandleSize, p.Y-HandleSize, HandleSize*2, HandleSize*2);
                using var b = new SolidBrush(Color.White);
                using var pen = new Pen(Color.FromArgb(60,60,75), 1f);
                g.FillEllipse(b, rect);
                g.DrawEllipse(pen, rect);
            }
        }

        private static Cursor CursorForHandle(HandleKind h, bool valid)
        {
            if (!valid) return Cursors.Arrow;
            return h switch
            {
                HandleKind.N => Cursors.SizeNS,
                HandleKind.S => Cursors.SizeNS,
                HandleKind.W => Cursors.SizeWE,
                HandleKind.E => Cursors.SizeWE,
                HandleKind.NE => Cursors.SizeNESW,
                HandleKind.SW => Cursors.SizeNESW,
                HandleKind.NW => Cursors.SizeNWSE,
                HandleKind.SE => Cursors.SizeNWSE,
                _ => Cursors.SizeAll
            };
        }

        private Rectangle KeepInFace(Rectangle r)
        {
            if (r.Width < 8) r.Width = 8;
            if (r.Height < 8) r.Height = 8;

            if (r.Left < _face.Left) r.X = _face.Left;
            if (r.Top < _face.Top) r.Y = _face.Top;
            if (r.Right > _face.Right) r.X = _face.Right - r.Width;
            if (r.Bottom > _face.Bottom) r.Y = _face.Bottom - r.Height;
            return r;
        }
    }
}
