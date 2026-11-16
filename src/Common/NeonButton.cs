using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Wolf
{
    // Кнопка с неоновой подсветкой по кругу. Свечение рисуется обводками скруглённого
    // прямоугольника с падающей прозрачностью — без картинок и без внешних библиотек.
    // Фон заливки совпадает с панелью, на которой кнопка лежит (шапка лаунчера).
    public sealed class NeonButton : Control
    {
        private const int Inset = 10;      // место под свечение вокруг самой кнопки
        private const int GlowSteps = 6;
        private const int GlowAlpha = 110;

        private readonly Color glow;
        private bool hover;
        private bool pressed;

        public NeonButton(string text, Color glow, Color panel)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            this.glow = glow;
            BackColor = panel;
            ForeColor = Color.FromArgb(214, 236, 255);
            Font = Theme.Font(9.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            TabStop = true;
            Radius = 10;
        }

        public int Radius { get; set; }

        private Color Fill { get { return Color.FromArgb(22, 34, 52); } }
        private Color Border { get { return Color.FromArgb(120, 200, 255); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            Rectangle body = new Rectangle(Inset, Inset, Width - Inset * 2, Height - Inset * 2);
            if (body.Width <= 0 || body.Height <= 0) { return; }

            // Каждое кольцо чуть шире предыдущего и заметно прозрачнее — вместе дают мягкий ореол.
            for (int i = GlowSteps; i >= 1; i--)
            {
                Rectangle ring = body;
                ring.Inflate(i, i);
                int alpha = (int)(GlowAlpha * (1.0 - (double)i / (GlowSteps + 1)));
                if (hover) { alpha += 25; }
                if (pressed) { alpha -= 20; }
                if (alpha < 0) { alpha = 0; }
                if (alpha > 255) { alpha = 255; }
                using (GraphicsPath path = Round(ring, Radius + i))
                using (Pen pen = new Pen(Color.FromArgb(alpha, glow), 2f))
                {
                    g.DrawPath(pen, path);
                }
            }

            using (GraphicsPath path = Round(body, Radius))
            {
                using (SolidBrush brush = new SolidBrush(pressed ? Color.FromArgb(16, 26, 40) : Fill))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(hover ? Color.FromArgb(190, 230, 255) : Border, 1.6f))
                {
                    g.DrawPath(pen, path);
                }
            }

            TextRenderer.DrawText(g, Text, Font, body, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
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

        // Enter/пробел работают как у обычной кнопки.
        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter) { return true; }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                pressed = true;
                Invalidate();
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                pressed = false;
                Invalidate();
                OnClick(EventArgs.Empty);
            }
            base.OnKeyUp(e);
        }
    }
}
