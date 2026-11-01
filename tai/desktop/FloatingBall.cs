using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TAI.Desktop
{
    internal static class FloatingBallGeometry
    {
        // All coordinates belong to the TIA client area in its DPI context.
        public static Rectangle Clamp(int width, int height, float scale, Point? position)
        {
            if (width <= 0 || height <= 0) return Rectangle.Empty;
            int diameter = Math.Min(Math.Min(width, height), Math.Max(24, (int)Math.Round(52 * scale)));
            int margin = Math.Max(0, (int)Math.Round(16 * scale));
            int x = position.HasValue ? position.Value.X : width - diameter - margin;
            int y = position.HasValue ? position.Value.Y : height - diameter - margin;
            return new Rectangle(Math.Max(0, Math.Min(x, width - diameter)),
                Math.Max(0, Math.Min(y, height - diameter)), diameter, diameter);
        }
    }

    /// <summary>Real circular child window, clipped to its TIA parent. Click toggles; dragging never clicks.</summary>
    internal sealed class FloatingBall : Control
    {
        private bool hover;
        private bool pressed;
        private bool dragging;
        private bool panelOpen;
        private Point pressScreen;
        private Point lastScreen;
        private readonly ToolTip tip = new ToolTip();

        public event EventHandler Toggle;
        public event EventHandler<Point> Dragged;

        public FloatingBall()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleName = "TAI 工程助手悬浮球";
            AccessibleDescription = "单击展开或收起助手，拖动调整位置，空格或回车切换。";
            AccessibleRole = AccessibleRole.PushButton;
            tip.SetToolTip(this, "TAI 工程助手 · 单击打开 · 拖动移动");
            Size = new Size(52, 52);
        }

        public bool PanelOpen
        {
            get { return panelOpen; }
            set
            {
                panelOpen = value;
                tip.SetToolTip(this, value ? "TAI 工程助手 · 单击收起 · 拖动移动" : "TAI 工程助手 · 单击打开 · 拖动移动");
                Invalidate();
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= EmbedNative.WS_CHILD | EmbedNative.WS_CLIPSIBLINGS;
                cp.Style &= ~EmbedNative.WS_POPUP;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var shape = new GraphicsPath())
            {
                shape.AddEllipse(0, 0, Math.Max(1, Width), Math.Max(1, Height));
                Region previous = Region;
                Region = new Region(shape);
                if (previous != null) previous.Dispose();
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == EmbedNative.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(EmbedNative.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = pressed ? Color.FromArgb(0, 94, 103) : hover ? Color.FromArgb(0, 150, 158) : Color.FromArgb(0, 119, 128);
            e.Graphics.Clear(fill);
            using (var pen = new Pen(Color.FromArgb(170, 255, 255, 255), Math.Max(1, Width / 26f)))
                e.Graphics.DrawEllipse(pen, 2, 2, Math.Max(1, Width - 5), Math.Max(1, Height - 5));
            using (var font = new Font("Segoe UI", Math.Max(8, Height * 0.29f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                e.Graphics.DrawString(panelOpen ? "×" : "AI", font, Brushes.White, ClientRectangle, format);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -8, -8), Color.White, fill);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressScreen = lastScreen = Cursor.Position;
            dragging = false;
            pressed = true;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!pressed) return;
            Point now = Cursor.Position;
            if (!dragging)
            {
                Size threshold = SystemInformation.DragSize;
                dragging = Math.Abs(now.X - pressScreen.X) > threshold.Width / 2 ||
                    Math.Abs(now.Y - pressScreen.Y) > threshold.Height / 2;
            }
            if (!dragging) return;
            Point delta = new Point(now.X - lastScreen.X, now.Y - lastScreen.Y);
            lastScreen = now;
            if (delta.X != 0 || delta.Y != 0) Dragged?.Invoke(this, delta);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool clicked = pressed && !dragging && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            pressed = false;
            dragging = false;
            Capture = false;
            Invalidate();
            base.OnMouseUp(e);
            if (clicked) Toggle?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) { pressed = false; dragging = false; Invalidate(); }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Toggle?.Invoke(this, EventArgs.Empty);
                e.Handled = e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
