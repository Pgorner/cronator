using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        public void SetMonitor(MonitorSnapshot? snapshot)
        {
            Data = snapshot;
            Invalidate();
        }

        public MonitorLayoutControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point); // crisp with TextRenderer
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

            if (Data == null || Data.Bounds.Width <= 0 || Data.Bounds.Height <= 0)
            {
                DrawEmpty(g);
                return;
            }

            var monRect = GetScaledRect(Data.Bounds, ClientRectangle, 16);
            var face = Rectangle.Inflate(monRect, -2, -2);

            using (var faceBrush = new SolidBrush(Color.FromArgb(245, 245, 248)))
                RoundedRect.Fill(g, faceBrush, face, 14);
            using (var borderPen = new Pen(Color.FromArgb(210, 214, 220), 2f))
                RoundedRect.Stroke(g, borderPen, face, 14);

            // Title (TextRenderer = sharper)
            var title = $"{(Data.IsPrimary ? "★ " : "")}Monitor {Data.Index}  {Data.Label}";
            using (var titleFont = new Font(Font, FontStyle.Bold))
            {
                var size = TextRenderer.MeasureText(title, titleFont, Size.Empty, TextFormatFlags.NoPadding);
                var pt = new Point((Width - size.Width) / 2, Math.Max(0, face.Top - size.Height - 4));
                TextRenderer.DrawText(g, title, titleFont, pt, Color.FromArgb(30, 30, 36),
                    TextFormatFlags.NoPadding);
            }

            if (Data.Widgets is { Count: > 0 })
            {
                foreach (var w in Data.Widgets)
                {
                    if (!w.Enabled) continue;

                    var r = new Rectangle(
                        face.Left + (int)Math.Round(w.RectNorm.X * face.Width),
                        face.Top + (int)Math.Round(w.RectNorm.Y * face.Height),
                        (int)Math.Round(Math.Max(0.01f, w.RectNorm.Width) * face.Width),
                        (int)Math.Round(Math.Max(0.01f, w.RectNorm.Height) * face.Height)
                    );

                    using (var b = new SolidBrush(Color.FromArgb(220, w.Color)))
                        RoundedRect.Fill(g, b, r, 8);
                    using (var p = new Pen(Color.FromArgb(255, w.Color), 1.5f))
                        RoundedRect.Stroke(g, p, r, 8);

                    // Label with TextRenderer (centered, ellipsis)
                    var label = string.IsNullOrWhiteSpace(w.Name) ? "Widget" : w.Name;
                    var textRect = Rectangle.Inflate(r, -4, -4);
                    using var f = new Font(Font.FontFamily, Math.Max(7f, Math.Min(10f, r.Height * 0.28f)), FontStyle.Regular, GraphicsUnit.Point);
                    TextRenderer.DrawText(
                        g, label, f, textRect, Color.FromArgb(35, 35, 40),
                        TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
                        | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                    );
                }
            }

            DrawLegend(g, face.Bottom + 10);
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

            var text = "Legend: colored rounded boxes indicate widgets; label = widget name";
            var rect = Rectangle.Inflate(legendRect, -10, -6);
            TextRenderer.DrawText(g, text, new Font(Font, FontStyle.Regular), rect, Color.FromArgb(55, 60, 70),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
