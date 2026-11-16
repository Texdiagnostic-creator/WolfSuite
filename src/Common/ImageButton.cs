using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Wolf
{
    // Кнопка с картинкой-фоном (например, капли крови). Наследуется от Button, поэтому
    // остаются Enabled, шрифт, Click и поведение по Enter — переопределяется только отрисовка.
    public sealed class ImageButton : Button
    {
        private Image face;
        private bool hover;
        private bool pressed;

        public ImageButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            ForeColor = Color.White;
            Radius = 8;
            PanelColor = Theme.BgPanel;
            BorderColor = Color.FromArgb(92, 12, 12);
            FallbackFill = Color.FromArgb(96, 22, 18);
        }

        public Image Face { get { return face; } set { face = value; Invalidate(); } }
        public Color PanelColor { get; set; }
        public Color BorderColor { get; set; }
        public Color FallbackFill { get; set; }
        public int Radius { get; set; }

        // Неоновая подсветка вокруг кнопки: GlowInset — сколько пикселей по краям отведено
        // под ореол (сама кнопка занимает ClientRectangle, уменьшенный на GlowInset).
        public Color GlowColor { get; set; }
        public int GlowInset { get; set; }

        private const int GlowSteps = 6;
        private const int GlowAlpha = 120;

        private const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                              TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(PanelColor);

            Rectangle body = ClientRectangle;
            body.Inflate(-(GlowInset + 1), -(GlowInset + 1));
            if (body.Width <= 2 || body.Height <= 2) { return; }

            if (GlowInset > 0 && GlowColor.A > 0)
            {
                // Те же кольца, что у кнопки ВК: от внешнего края к кнопке ярче и плотнее.
                for (int i = GlowSteps; i >= 1; i--)
                {
                    Rectangle ring = body;
                    ring.Inflate(i, i);
                    int alpha = (int)(GlowAlpha * (1.0 - (double)i / (GlowSteps + 1)));
                    if (hover && Enabled) { alpha += 25; }
                    if (pressed) { alpha -= 20; }
                    if (alpha < 0) { alpha = 0; }
                    if (alpha > 255) { alpha = 255; }
                    using (GraphicsPath path = Round(ring, Radius + i))
                    using (Pen pen = new Pen(Color.FromArgb(alpha, GlowColor), 2f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            using (GraphicsPath path = Round(body, Radius))
            {
                if (face != null)
                {
                    Region old = g.Clip;
                    g.SetClip(path);
                    g.DrawImage(face, body);
                    g.Clip = old;
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(FallbackFill)) { g.FillPath(brush, path); }
                }

                if (hover && Enabled)
                {
                    using (SolidBrush light = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) { g.FillPath(light, path); }
                }
                if (pressed)
                {
                    using (SolidBrush dark = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) { g.FillPath(dark, path); }
                }
                if (!Enabled)
                {
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 24, 26, 31))) { g.FillPath(dim, path); }
                }
                using (Pen pen = new Pen(BorderColor, 1.4f)) { g.DrawPath(pen, path); }
            }

            // Тень под текстом: на текстуре крови белое читается только так.
            Rectangle shadow = body;
            shadow.Offset(1, 1);
            TextRenderer.DrawText(g, Text, Font, shadow, Color.FromArgb(170, 0, 0, 0), Flags);
            TextRenderer.DrawText(g, Text, Font, body, Enabled ? ForeColor : Color.FromArgb(150, 200, 200, 200), Flags);
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d > r.Width) { d = r.Width; }
            if (d > r.Height) { d = r.Height; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }
    }
}
