using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GyeolheePinball
{
    // Keep the frame and controls on one opaque surface; DWM owns the anti-aliased outer corners.
    internal class UpdateDialog : Form
    {
        // 메인 화면("오락실 도트")과 같은 색을 쓴다.
        protected static readonly Color SurfaceColor = ArcadeColors.Header;
        protected static readonly Color TextColor = ArcadeColors.Text;
        protected static readonly Color MutedColor = ArcadeColors.Soft;
        protected static readonly Color AccentColor = ArcadeColors.Accent;
        protected static readonly Color BorderColor = ArcadeColors.Accent;
        private bool _nativeFrame;

        protected UpdateDialog(string caption, bool canClose)
        {
            Text = caption;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            BackColor = SurfaceColor;
            Font = UiFont.Make(9.0f, FontStyle.Regular);
            DoubleBuffered = true;

            if (canClose)
            {
                var close = new UpdateActionButton();
                close.Name = "closeButton";
                close.Text = "×";
                close.AccessibleName = "닫기";
                close.Font = UiFont.Make(11f, FontStyle.Regular);
                close.FillColor = SurfaceColor;
                close.LineColor = SurfaceColor;
                close.ForeColor = MutedColor;
                close.SetBounds(374, 21, 28, 28);
                close.DialogResult = DialogResult.Cancel;
                close.TabIndex = 2;
                Controls.Add(close);
            }
        }

        protected void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
            }
        }

        protected Label AddText(string name, string text, float size, FontStyle style, Color color,
            int x, int y, int width, int height)
        {
            var label = new UpdateTextLabel();
            label.Name = name;
            label.Text = text;
            label.Font = UiFont.Make(size, style);
            label.ForeColor = color;
            label.BackColor = SurfaceColor;
            label.SetBounds(x, y, width, height);
            Controls.Add(label);
            return label;
        }

        protected UpdateActionButton AddAction(string name, string text, bool primary, int x, int y)
        {
            var button = new UpdateActionButton();
            button.Name = name;
            button.Text = text;
            button.Font = ArcadeFonts.Get(ArcadeFonts.Pixel, 12f * DeviceDpi / 96f, false);
            // FillColor는 단추 면, LineColor는 아래로 떨어지는 그림자 색이다.
            button.FillColor = primary ? ArcadeColors.Rose : Color.FromArgb(0x5d, 0x4b, 0xb0);
            button.LineColor = primary ? Color.FromArgb(0x5d, 0x4b, 0xb0) : ArcadeColors.Ink;
            button.ForeColor = TextColor;
            button.SetBounds(x, y, 92, 38);
            Controls.Add(button);
            return button;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.Style |= 0x00C00000 | 0x00040000; // DWM frame, without a native caption strip.
                return value;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0083 && message.WParam != IntPtr.Zero)
            {
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
            if (message.Msg == 0x0084 && message.Result.ToInt32() >= 10 && message.Result.ToInt32() <= 17)
            {
                message.Result = new IntPtr(1); // Fixed-size dialog: do not expose resize edges.
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int preference = 1; // 모서리를 둥글리지 않는다.
                _nativeFrame = DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)) == 0;
                int color = ColorTranslator.ToWin32(BorderColor);
                DwmSetWindowAttribute(Handle, 34, ref color, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 창 안쪽에 굵은 테두리를 두른다(메인 화면 상자의 도트 테두리와 같은 굵기).
            int thickness = Math.Max(1, (int)Math.Round(3f * DeviceDpi / 96f));
            using (var brush = new SolidBrush(BorderColor))
            {
                e.Graphics.FillRectangle(brush, 0, 0, ClientSize.Width, thickness);
                e.Graphics.FillRectangle(brush, 0, ClientSize.Height - thickness, ClientSize.Width, thickness);
                e.Graphics.FillRectangle(brush, 0, 0, thickness, ClientSize.Height);
                e.Graphics.FillRectangle(brush, ClientSize.Width - thickness, 0, thickness, ClientSize.Height);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class UpdatePromptDialog : UpdateDialog
    {
        public UpdatePromptDialog(Version version, string releaseNotes) : base("업데이트 안내", true)
        {
            var title = AddText("title", "새 버전을 사용할 수 있습니다", 13.0f, FontStyle.Bold, TextColor, 18, 20, 348, 30);
            title.MouseDown += DragWindow;
            title.Font = ArcadeFonts.Get(ArcadeFonts.Pixel, 24f * DeviceDpi / 96f, true);
            title.ForeColor = AccentColor;
            AddText("version", "버전 " + version, 9.5f, FontStyle.Bold, ArcadeColors.Cyan, 18, 56, 384, 22);
            var notesBox = new UpdateNotesPanel();
            notesBox.Name = "notesBox";
            notesBox.AccessibleName = "업데이트 내용";
            notesBox.FillColor = ArcadeColors.Background;
            notesBox.BorderColor = ArcadeColors.Line;
            notesBox.SetBounds(18, 86, 384, 0);
            Controls.Add(notesBox);

            var notesTitle = AddText("notesTitle", "업데이트 내용", 9.5f, FontStyle.Bold, TextColor, 14, 12, 356, 22);
            notesTitle.BackColor = notesBox.FillColor;
            notesBox.Controls.Add(notesTitle);
            var notes = AddText("notes", releaseNotes, 10.0f, FontStyle.Regular, MutedColor, 14, 40, 356, 42);
            notes.BackColor = notesBox.FillColor;
            notesBox.Controls.Add(notes);
            Size measured = TextRenderer.MeasureText(notes.Text, notes.Font, new Size(notes.Width, Int32.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            notes.Height = Math.Max(42, measured.Height + 4);
            notesBox.Height = notes.Bottom + 14;
            int buttonY = notesBox.Bottom + 16;
            ClientSize = new Size(420, buttonY + 38 + 16);

            var later = AddAction("laterButton", "나중에", false, 208, buttonY);
            later.DialogResult = DialogResult.Cancel;
            later.TabIndex = 0;
            var update = AddAction("updateButton", "업데이트", true, 310, buttonY);
            update.DialogResult = DialogResult.OK;
            update.TabIndex = 1;
            AcceptButton = update;
            CancelButton = later;
            ActiveControl = later;
        }
    }

    internal sealed class UpdateTextLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (ArcadeFonts.IsOutlineFont(Font))
            {
                // 도트 글꼴은 메인 화면과 같은 방식으로 그린다.
                using (var brush = new SolidBrush(ForeColor))
                {
                    ArcadeSurface.DrawSmoothText(e.Graphics, Text, Font, brush, ClientRectangle, 0);
                }
                return;
            }

            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        }
    }

    // 업데이트 내용을 담는 각진 상자.
    internal sealed class UpdateNotesPanel : Panel
    {
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }

        public UpdateNotesPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int thickness = Math.Max(1, (int)Math.Round(2f * e.Graphics.DpiX / 96f));
            using (var border = new SolidBrush(BorderColor))
            using (var fill = new SolidBrush(FillColor))
            {
                e.Graphics.FillRectangle(border, ClientRectangle);
                e.Graphics.FillRectangle(fill, Rectangle.Inflate(ClientRectangle, -thickness, -thickness));
            }
        }
    }

    // 내려받기 진행 막대.
    internal sealed class UpdateProgressBar : Control
    {
        private int _value;

        public UpdateProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public int Value
        {
            get { return _value; }
            set
            {
                _value = Math.Max(0, Math.Min(100, value));
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int thickness = Math.Max(1, (int)Math.Round(2f * e.Graphics.DpiX / 96f));
            Rectangle inner = Rectangle.Inflate(ClientRectangle, -thickness, -thickness);
            using (var border = new SolidBrush(ArcadeColors.Line))
            using (var track = new SolidBrush(ArcadeColors.Background))
            using (var bar = new SolidBrush(ArcadeColors.Accent))
            {
                e.Graphics.FillRectangle(border, ClientRectangle);
                e.Graphics.FillRectangle(track, inner);
                e.Graphics.FillRectangle(bar, inner.X, inner.Y, inner.Width * _value / 100, inner.Height);
            }
        }
    }

    // A real Button preserves Enter/Escape, tab navigation and accessibility while matching RoundButton.
    internal sealed class UpdateActionButton : Button
    {
        public Color FillColor { get; set; }
        public Color LineColor { get; set; }
        private bool _hover;
        private bool _pressed;

        public UpdateActionButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color canvas = Parent == null ? BackColor : Parent.BackColor;
            e.Graphics.Clear(canvas);
            if (Width < 3 || Height < 3) return;
            // 각진 단추: 면 아래로 그림자가 떨어지고, 올리면 밝아지고 누르면 살짝 내려간다.
            int shadow = Math.Max(1, (int)Math.Round(4f * e.Graphics.DpiX / 96f));
            int offset = _pressed ? Math.Max(1, shadow / 2) : 0;
            var face = new Rectangle(0, offset, Width, Height - shadow);
            using (var shadowBrush = new SolidBrush(LineColor))
            using (var faceBrush = new SolidBrush(_hover ? ArcadeColors.Brighten(FillColor, 1.12f) : FillColor))
            {
                e.Graphics.FillRectangle(shadowBrush, 0, offset + shadow, Width, Height - shadow - offset);
                e.Graphics.FillRectangle(faceBrush, face);
            }
            if (ArcadeFonts.IsOutlineFont(Font))
            {
                using (var brush = new SolidBrush(ForeColor))
                {
                    ArcadeSurface.DrawSmoothText(e.Graphics, Text, Font, brush, face, 1);
                }
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, face, ForeColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(face, -4, -4), ForeColor, FillColor);
        }
    }
}