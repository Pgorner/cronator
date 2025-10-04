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
            var tl = new Rectangle(rect.X, rect.Y, d, d);
            var tr = new Rectangle(rect.Right - d, rect.Y, d, d);
            var br = new Rectangle(rect.Right - d, rect.Bottom - d, d, d);
            var bl = new Rectangle(rect.X, rect.Bottom - d, d, d);

            var path = new GraphicsPath();
            path.StartFigure();
            path.AddArc(tl, 180f, 90f);
            path.AddArc(tr, 270f, 90f);
            path.AddArc(br,   0f, 90f);
            path.AddArc(bl,  90f, 90f);
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

        /// <summary>Raised on every drag/resize step with the new normalized rect.</summary>
        public event Action<string, RectangleF>? WidgetChanged;

        /// <summary>Raised once on mouse-up after a drag/resize completes.</summary>
        public event Action<string, RectangleF>? WidgetChangeCommitted;

        // geometry/drawing
        private Rectangle _face; // scaled monitor face inside the control
        private readonly Dictionary<WidgetBox, Rectangle> _drawRects = new(); // pixel cache
        private readonly List<WidgetBox> _zOrder = new(); // topmost last

        // interaction
        private WidgetBox? _hot;
        private WidgetBox? _active;
        private Point _dragStart;
        private Rectangle _activeStartPx;
        private RectangleF _activeStartNorm;
        private bool _isDragging;
        private bool _resizing;

        private enum HandleKind { None, N, S, E, W, NE, NW, SE, SW }
        private HandleKind _handle = HandleKind.None;

        private const int HandleSize = 11;
        private const float MinNorm = 0.02f;
        private const float Eps = 0.0005f;

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

        // -------- Public API --------

        public void SetMonitor(MonitorSnapshot? snapshot)
        {
            Data = snapshot;
            _hot = _active = null;
            _isDragging = false;
            _resizing = false;

            _drawRects.Clear();
            _zOrder.Clear();

            if (Data?.Widgets != null && Data.Bounds.Width > 0 && Data.Bounds.Height > 0)
            {
                // enabled widgets on top
                _zOrder.AddRange(Data.Widgets.Where(w => w.Enabled));
                _zOrder.AddRange(Data.Widgets.Where(w => !w.Enabled));
                RebuildFaceAndRects();
            }
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!_isDragging) // don’t rebuild while dragging
            {
                RebuildFaceAndRects();
                Invalidate();
            }
        }

        // -------- Layout & Paint --------

        private void RebuildFaceAndRects()
        {
            if (Data == null || Data.Bounds.Width <= 0 || Data.Bounds.Height <= 0)
            {
                _face = Rectangle.Empty;
                _drawRects.Clear();
                return;
            }

            _face = Rectangle.Inflate(GetScaledRect(Data.Bounds, ClientRectangle, 16), -2, -2);

            _drawRects.Clear();
            if (_face.Width <= 0 || _face.Height <= 0 || Data.Widgets == null) return;

            foreach (var w in Data.Widgets)
                _drawRects[w] = NormToFace(w.RectNorm);
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

            if (Data == null || _face.Width <= 0 || _face.Height <= 0)
            {
                DrawEmpty(g);
                return;
            }

            using (var faceBrush = new SolidBrush(Color.FromArgb(245, 245, 248)))
                RoundedRect.Fill(g, faceBrush, _face, 14);
            using (var borderPen = new Pen(Color.FromArgb(210, 214, 220), 2f))
                RoundedRect.Stroke(g, borderPen, _face, 14);

            // Title
            var title = $"{(Data.IsPrimary ? "★ " : "")}Monitor {Data.Index}  {Data.Label}";
            using (var titleFont = new Font(Font, FontStyle.Bold))
            {
                var size = TextRenderer.MeasureText(title, titleFont, Size.Empty, TextFormatFlags.NoPadding);
                var pt = new Point((Width - size.Width) / 2, Math.Max(0, _face.Top - size.Height - 4));
                TextRenderer.DrawText(g, title, titleFont, pt, Color.FromArgb(30, 30, 36), TextFormatFlags.NoPadding);
            }

            // Widgets (from pixel cache)
            foreach (var w in _zOrder)
            {
                if (!_drawRects.TryGetValue(w, out var r)) continue;

                var fill = w.Enabled ? w.Color : ControlPaint.Light(w.Color, 0.6f);
                using (var b = new SolidBrush(Color.FromArgb(220, fill)))
                    RoundedRect.Fill(g, b, r, 8);
                using (var p = new Pen(Color.FromArgb(255, fill), 1.5f))
                    RoundedRect.Stroke(g, p, r, 8);

                var label = string.IsNullOrWhiteSpace(w.Name) ? "Widget" : w.Name;
                var textRect = Rectangle.Inflate(r, -4, -4);
                using var f = new Font(Font.FontFamily, Math.Max(7f, Math.Min(10f, r.Height * 0.28f)));
                TextRenderer.DrawText(
                    g, label, f, textRect, Color.FromArgb(35, 35, 40),
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                if (w == _active || w == _hot)
                {
                    using var selPen = new Pen(Color.FromArgb(60, 60, 75), 1.5f) { DashStyle = DashStyle.Dot };
                    g.DrawRectangle(selPen, r);
                    DrawHandles(g, r);
                }
            }

            DrawLegend(g, _face.Bottom + 10);
        }

        private void DrawLegend(Graphics g, int top)
        {
            var legendRect = new Rectangle(12, top, Width - 24, Math.Max(26, Height - top - 12));
            using var lbg = new SolidBrush(Color.FromArgb(252, 252, 253));
            using var lbd = new Pen(Color.FromArgb(220, 224, 232), 1f);
            RoundedRect.Fill(g, lbg, legendRect, 8);
            RoundedRect.Stroke(g, lbd, legendRect, 8);

            var text = "Drag to move • Use handles to resize • Changes save on drop (or click Save).";
            var rect = Rectangle.Inflate(legendRect, -10, -6);
            TextRenderer.DrawText(g, text, new Font(Font, FontStyle.Regular), rect, Color.FromArgb(55, 60, 70),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // -------- Interaction --------

        private void OnMouseDown(object? sender, MouseEventArgs e)
        {
            if (Data == null || e.Button != MouseButtons.Left) return;
            if (_face.IsEmpty) return;

            var (hit, handle) = HitTest(e.Location);
            _active = hit;
            _handle = handle;
            _resizing = _active != null && handle != HandleKind.None;
            _dragStart = e.Location;

            if (_active != null)
            {
                // bring to front
                var idx = _zOrder.IndexOf(_active);
                if (idx >= 0 && idx != _zOrder.Count - 1)
                {
                    _zOrder.RemoveAt(idx);
                    _zOrder.Add(_active);
                }

                if (!_drawRects.TryGetValue(_active, out _activeStartPx))
                    _activeStartPx = NormToFace(_active.RectNorm);

                _activeStartNorm = _active.RectNorm;
                _isDragging = true;
                Capture = true;
                Invalidate();
            }
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

                // live-normalize to light Save button and keep model in sync
                var newNorm = FaceToNorm(r);
                if (!RectAlmostEqual(_active.RectNorm, newNorm))
                {
                    _active.RectNorm = newNorm;
                    WidgetChanged?.Invoke(_active.Name, newNorm);
                }

                Invalidate();
                return;
            }

            // hover feedback
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

                if (_active != null && _drawRects.TryGetValue(_active, out var r))
                {
                    r = KeepInFace(r);
                    _drawRects[_active] = r;

                    var rn = FaceToNorm(r);
                    if (!RectAlmostEqual(_active.RectNorm, rn))
                    {
                        _active.RectNorm = rn;
                        WidgetChanged?.Invoke(_active.Name, rn); // final live update
                    }
                    WidgetChangeCommitted?.Invoke(_active.Name, _active.RectNorm);
                }
            }
            _resizing = false;
            _handle = HandleKind.None;
        }

        // -------- Hit-testing & helpers --------

        private (WidgetBox? box, HandleKind handle) HitTest(Point p)
        {
            if (Data == null || _face.IsEmpty) return (null, HandleKind.None);

            for (int i = _zOrder.Count - 1; i >= 0; i--)
            {
                var wb = _zOrder[i];
                if (!_drawRects.TryGetValue(wb, out var r)) continue;

                var h = HandleAtPoint(r, p);
                if (h != HandleKind.None) return (wb, h);
                if (r.Contains(p)) return (wb, HandleKind.None);
            }
            return (null, HandleKind.None);
        }

        private HandleKind HandleAtPoint(Rectangle r, Point p)
        {
            int hs = HandleSize;
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

        private Rectangle NormToFace(RectangleF rn)
        {
            int x = _face.Left + (int)Math.Round(Clamp01(rn.X) * _face.Width);
            int y = _face.Top  + (int)Math.Round(Clamp01(rn.Y) * _face.Height);
            int w = (int)Math.Round(Math.Max(MinNorm, rn.Width)  * _face.Width);
            int h = (int)Math.Round(Math.Max(MinNorm, rn.Height) * _face.Height);
            var r = new Rectangle(x, y, w, h);
            return KeepInFace(r);
        }

        private RectangleF FaceToNorm(Rectangle px)
        {
            float nx = Clamp01((px.Left - _face.Left) / Math.Max(1f, _face.Width));
            float ny = Clamp01((px.Top  - _face.Top ) / Math.Max(1f, _face.Height));
            float nw = Clamp01(px.Width  / Math.Max(1f, _face.Width));
            float nh = Clamp01(px.Height / Math.Max(1f, _face.Height));

            if (nx + nw > 1f) nx = 1f - nw;
            if (ny + nh > 1f) ny = 1f - nh;
            nw = Math.Max(MinNorm, nw);
            nh = Math.Max(MinNorm, nh);

            return new RectangleF(nx, ny, nw, nh);
        }

        private Rectangle KeepInFace(Rectangle r)
        {
            int minW = Math.Max(8, (int)Math.Round(MinNorm * _face.Width));
            int minH = Math.Max(8, (int)Math.Round(MinNorm * _face.Height));
            if (r.Width < minW) r.Width = minW;
            if (r.Height < minH) r.Height = minH;

            if (r.Left < _face.Left) r.X = _face.Left;
            if (r.Top < _face.Top) r.Y = _face.Top;
            if (r.Right > _face.Right) r.X = _face.Right - r.Width;
            if (r.Bottom > _face.Bottom) r.Y = _face.Bottom - r.Height;
            return r;
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

        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private static bool RectAlmostEqual(RectangleF a, RectangleF b)
        {
            return Math.Abs(a.X - b.X) < Eps &&
                   Math.Abs(a.Y - b.Y) < Eps &&
                   Math.Abs(a.Width - b.Width) < Eps &&
                   Math.Abs(a.Height - b.Height) < Eps;
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
    }
}
