using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GyeolheePinball
{
    // "오락실 도트" 화면의 색. 값은 종겜핀볼 Web 결희 판(밝은 보라)과 같다.
    internal static class ArcadeColors
    {
        public static readonly Color Background = Color.FromArgb(0x2e, 0x2a, 0x5c);
        public static readonly Color GridLine = Color.FromArgb(52, 48, 97);
        public static readonly Color Header = Color.FromArgb(0x5f, 0x53, 0xb8);
        public static readonly Color Ink = Color.FromArgb(0x1b, 0x18, 0x38);
        public static readonly Color Panel = Color.FromArgb(0x48, 0x44, 0x8a);
        public static readonly Color Border = Color.FromArgb(0xa0, 0x9c, 0xe2);
        public static readonly Color Line = Color.FromArgb(0xd3, 0xd0, 0xf8);
        public static readonly Color Text = Color.FromArgb(0xf4, 0xf4, 0xf4);
        public static readonly Color Soft = Color.FromArgb(0xec, 0xeb, 0xfb);
        public static readonly Color Accent = Color.FromArgb(0xa9, 0xd8, 0xff);
        public static readonly Color Cyan = Color.FromArgb(0xcd, 0xbc, 0xff);
        public static readonly Color Rose = Color.FromArgb(0xc2, 0x3a, 0x52);
        public static readonly Color Pink = Color.FromArgb(0xff, 0x9e, 0xc4);
        public static readonly Color Green = Color.FromArgb(0xa7, 0xf0, 0x70);
        public static readonly Color Blue = Color.FromArgb(0x41, 0xa6, 0xf6);
        public static readonly Color EditFill = Color.FromArgb(86, 83, 135);

        public static Color ForSource(GiftSource source)
        {
            if (source == GiftSource.AdBalloon)
            {
                return Green;
            }

            if (source == GiftSource.ChallengeGift)
            {
                return Pink;
            }

            if (source == GiftSource.BattleMission)
            {
                return Color.FromArgb(0xff, 0xcd, 0x75);
            }

            return Blue;
        }

        public static Color Brighten(Color color, float factor)
        {
            return Color.FromArgb(
                color.A,
                Math.Min(255, (int)Math.Round(color.R * factor)),
                Math.Min(255, (int)Math.Round(color.G * factor)),
                Math.Min(255, (int)Math.Round(color.B * factor)));
        }

        public static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)Math.Round(from.R + ((to.R - from.R) * amount)),
                (int)Math.Round(from.G + ((to.G - from.G) * amount)),
                (int)Math.Round(from.B + ((to.B - from.B) * amount)));
        }
    }

    // 글꼴은 EXE에 내장된 파일을 먼저 쓰고, 없으면 PC에 설치된 글꼴, 그것도 없으면 본문 글꼴로 대신한다.
    internal static class ArcadeFonts
    {
        [DllImport("gdi32.dll")]
        private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, [In] ref uint pcFonts);

        private static readonly PrivateFontCollection Collection = new PrivateFontCollection();
        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();
        private static readonly Bitmap MeasureBitmap = new Bitmap(1, 1);
        private static readonly Graphics MeasureGraphics = Graphics.FromImage(MeasureBitmap);
        public static readonly StringFormat Format = CreateFormat();

        public static readonly FontFamily Body;
        public static readonly FontFamily Pixel;
        private static readonly FontFamily PixelBold;
        public static readonly FontFamily Digits;
        public static readonly FontFamily Display;
        public static readonly bool PixelEmbedded;
        public static readonly bool DigitsEmbedded;
        public static readonly bool DisplayEmbedded;

        static ArcadeFonts()
        {
            Body = FindInstalled("Noto Sans KR", "Malgun Gothic", "Segoe UI") ?? FontFamily.GenericSansSerif;

            bool pixelRegular = LoadEmbedded("ArcadeFontGalmuri11");
            LoadEmbedded("ArcadeFontGalmuri11Bold");
            DigitsEmbedded = LoadEmbedded("ArcadeFontPressStart2P");
            DisplayEmbedded = LoadEmbedded("ArcadeFontDoHyeon");
            PixelEmbedded = pixelRegular;

            // 갈무리11은 보통 굵기와 굵은 글씨가 서로 다른 글꼴 이름("Galmuri11 Regular", "Galmuri11 Bold")으로 잡힌다.
            Pixel = FindPrivate("Galmuri11 Regular") ?? FindPrivate("Galmuri11") ?? FindInstalled("Galmuri11") ?? Body;
            PixelBold = FindPrivate("Galmuri11 Bold");
            Digits = FindPrivate("Press Start 2P") ?? FindInstalled("Press Start 2P") ?? Body;
            Display = FindPrivate("Do Hyeon") ?? FindInstalled("Do Hyeon", "BM DoHyeon", "배달의민족 도현") ?? Body;
            // 윤곽선으로 그리는 글씨와 폭이 맞도록 획 맞춤 없이 잰다.
            MeasureGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        private static StringFormat CreateFormat()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            format.FormatFlags &= ~StringFormatFlags.LineLimit;
            return format;
        }

        private static bool LoadEmbedded(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        return false;
                    }

                    byte[] bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int count = stream.Read(bytes, read, bytes.Length - read);
                        if (count <= 0)
                        {
                            break;
                        }
                        read += count;
                    }

                    // 글꼴 메모리는 프로그램이 끝날 때까지 쓰이므로 풀지 않는다.
                    IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
                    Marshal.Copy(bytes, 0, memory, bytes.Length);
                    Collection.AddMemoryFont(memory, bytes.Length);
                    uint added = 0;
                    AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, ref added);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static FontFamily FindPrivate(string name)
        {
            foreach (FontFamily family in Collection.Families)
            {
                if (String.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return family;
                }
            }

            return null;
        }

        private static FontFamily FindInstalled(params string[] names)
        {
            try
            {
                FontFamily[] families = FontFamily.Families;
                foreach (string name in names)
                {
                    foreach (FontFamily family in families)
                    {
                        if (String.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return family;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        public static Font Get(FontFamily family, float pixels, bool bold)
        {
            float size = Math.Max(6f, (float)Math.Round(pixels * 2f) / 2f);
            string key = family.Name + "|" + size + "|" + (bold ? "b" : "r");
            Font font;
            if (Cache.TryGetValue(key, out font))
            {
                return font;
            }

            FontStyle style = bold ? FontStyle.Bold : FontStyle.Regular;
            if (bold && PixelBold != null && Object.ReferenceEquals(family, Pixel))
            {
                // 굵은 갈무리11은 따로 그려진 글꼴 파일을 쓴다(보통 굵기를 기계적으로 굵게 하지 않는다).
                family = PixelBold;
                style = FontStyle.Regular;
            }
            if (!family.IsStyleAvailable(style))
            {
                style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            }

            font = new Font(family, size, style, GraphicsUnit.Pixel);
            Cache[key] = font;
            return font;
        }

        // 도현체는 글자가 줄 위쪽에 붙어 있어, 세로 가운데에 놓으려면 조금 내려야 한다.
        public static int BaselineDrop(Font font)
        {
            return font.Name == "Do Hyeon" ? (int)Math.Round(font.Size * 0.11f, MidpointRounding.AwayFromZero) : 0;
        }

        // 윤곽선으로 그리는 글꼴: 도트 글꼴과 도현체. 작은 크기용 획 맞춤 정보가 없어서 획을 픽셀에 맞추면 찌그러진다.
        public static bool IsOutlineFont(Font font)
        {
            string name = font.Name;
            return name.StartsWith("Galmuri", StringComparison.OrdinalIgnoreCase) || name == "Press Start 2P" || name == "Do Hyeon";
        }

        public static float Measure(string text, Font font)
        {
            if (String.IsNullOrEmpty(text))
            {
                return 0f;
            }

            lock (MeasureGraphics)
            {
                // 그리는 방식과 같은 방식으로 잰다.
                MeasureGraphics.TextRenderingHint = IsOutlineFont(font) ? TextRenderingHint.AntiAlias : TextRenderingHint.AntiAliasGridFit;
                return MeasureGraphics.MeasureString(text, font, 100000, Format).Width;
            }
        }
    }

    internal sealed class ArcadeZone
    {
        public string Key;
        public Rectangle Bounds;
        public Action Click;
        public Action DragStart;
        public Action<float, float> Drag;
        public Action DragEnd;
        public Cursor Cursor = Cursors.Hand;
        public string Tip;
    }

    // 직접 그리는 판. 그릴 때마다 누를 수 있는 자리(zone)를 다시 등록하고, 마우스는 그 자리를 기준으로 처리한다.
    // 좌표는 웹 시안과 같은 2560 기준 값으로 쓰고, Zoom을 곱해 실제 픽셀로 바꾼다.
    internal class ArcadeSurface : Control
    {
        private readonly List<ArcadeZone> _zones = new List<ArcadeZone>();
        private readonly ToolTip _toolTip = new ToolTip();
        private string _hotKey;
        private string _downKey;
        private ArcadeZone _dragZone;
        private Point _dragOrigin;
        private string _tipText = "";
        private Rectangle _zoneClip = Rectangle.Empty;
        private Point _lastMouse;

        public float Zoom = 0.5f;
        public Action<ArcadeSurface, Graphics> Painter;

        public ArcadeSurface()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);
            TabStop = false;
            BackColor = ArcadeColors.Background;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams createParams = base.CreateParams;
                createParams.Style |= 0x02000000; // WS_CLIPCHILDREN
                return createParams;
            }
        }

        public int Px(float value)
        {
            return (int)Math.Round(value * Zoom, MidpointRounding.AwayFromZero);
        }

        public Rectangle Rect(float x, float y, float width, float height)
        {
            int left = Px(x);
            int top = Px(y);
            // 아주 가는 선도 사라지지 않게 최소 1픽셀은 남긴다.
            int right = Math.Max(left + (width > 0f ? 1 : 0), Px(x + width));
            int bottom = Math.Max(top + (height > 0f ? 1 : 0), Px(y + height));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        public Font F(FontFamily family, float pixels, bool bold)
        {
            return ArcadeFonts.Get(family, pixels * Zoom, bold);
        }

        public float TextWidth(string text, FontFamily family, float pixels, bool bold)
        {
            return ArcadeFonts.Measure(text, F(family, pixels, bold)) / Zoom;
        }

        public bool IsHot(string key)
        {
            return key != null && key == _hotKey;
        }

        public bool IsDown(string key)
        {
            return key != null && key == _downKey;
        }

        public void SetZoneClip(float x, float y, float width, float height)
        {
            _zoneClip = Rect(x, y, width, height);
        }

        public void ClearZoneClip()
        {
            _zoneClip = Rectangle.Empty;
        }

        public ArcadeZone Zone(string key, float x, float y, float width, float height, Action click)
        {
            Rectangle bounds = Rect(x, y, width, height);
            if (!_zoneClip.IsEmpty)
            {
                bounds.Intersect(_zoneClip);
            }

            var zone = new ArcadeZone();
            zone.Key = key;
            zone.Bounds = bounds;
            zone.Click = click;
            _zones.Add(zone);
            return zone;
        }

        public void Fill(Graphics graphics, Color color, float x, float y, float width, float height)
        {
            Rectangle rect = Rect(x, y, width, height);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            using (var brush = new SolidBrush(color))
            {
                graphics.FillRectangle(brush, rect);
            }
        }

        // 모서리가 비는 십자 모양 테두리(상자 바깥쪽에 그린다).
        public void DotBorder(Graphics graphics, Color color, float x, float y, float width, float height, float thickness)
        {
            Fill(graphics, color, x, y - thickness, width, thickness);
            Fill(graphics, color, x, y + height, width, thickness);
            Fill(graphics, color, x - thickness, y, thickness, height);
            Fill(graphics, color, x + width, y, thickness, height);
        }

        // 상자 안쪽에 그리는 네모 테두리.
        public void Frame(Graphics graphics, Color color, float x, float y, float width, float height, float thickness)
        {
            Fill(graphics, color, x, y, width, thickness);
            Fill(graphics, color, x, y + height - thickness, width, thickness);
            Fill(graphics, color, x, y + thickness, thickness, height - (thickness * 2));
            Fill(graphics, color, x + width - thickness, y + thickness, thickness, height - (thickness * 2));
        }

        public void DashedFrame(Graphics graphics, Color color, float x, float y, float width, float height, float thickness)
        {
            Rectangle rect = Rect(x, y, width, height);
            int t = Math.Max(1, Px(thickness));
            using (var pen = new Pen(color, t))
            {
                pen.DashStyle = DashStyle.Dash;
                pen.Alignment = PenAlignment.Inset;
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
        }

        // align: 0 왼쪽, 1 가운데, 2 오른쪽
        public void Write(Graphics graphics, string text, Font font, Color color, float x, float y, float width, float height, int align)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            Rectangle rect = Rect(x, y, width, height);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            using (var brush = new SolidBrush(color))
            {
                DrawSmoothText(graphics, text, font, brush, new RectangleF(rect.X, rect.Y + ArcadeFonts.BaselineDrop(font), rect.Width, rect.Height), align);
            }
        }

        // 글자를 윤곽선으로 바꿔 부드럽게 채운다. 웹 브라우저처럼 획을 픽셀에 억지로 맞추지 않으므로
        // 창이 작아 글씨가 어중간한 크기가 되어도 글자 모양이 고르게 나온다.
        // 글자를 놓는 자리는 정수 픽셀에 맞춘다(글자 밑선과 왼쪽 끝). 자리가 반 픽셀만 어긋나도 가로 획의
        // 위아래가 두 줄에 걸쳐 흐려진다. 도트 글꼴은 자리와 크기가 맞으면 점이 그대로 또렷하게 나온다.
        // align: 0 왼쪽, 1 가운데, 2 오른쪽. 칸보다 긴 글은 왼쪽부터 쓰고 말줄임표로 자른다.
        public static void DrawSmoothText(Graphics graphics, string text, Font font, SolidBrush brush, RectangleF bounds, int align)
        {
            FontFamily family = font.FontFamily;
            float em = family.GetEmHeight(font.Style);
            float ascent = font.Size * family.GetCellAscent(font.Style) / em;
            float line = font.Size * family.GetLineSpacing(font.Style) / em;
            float baseline = (float)Math.Round(bounds.Y + ((bounds.Height - line) / 2f) + ascent, MidpointRounding.AwayFromZero);
            float top = baseline - ascent;

            float textWidth = ArcadeFonts.Measure(text, font);
            bool fits = textWidth <= bounds.Width + 0.5f;
            float left = bounds.X;
            if (fits && align == 1)
            {
                left = bounds.X + ((bounds.Width - textWidth) / 2f);
            }
            else if (fits && align == 2)
            {
                left = bounds.Right - textWidth;
            }
            left = (float)Math.Round(left, MidpointRounding.AwayFromZero);
            var layout = new RectangleF(left, top, fits ? textWidth + font.Size : bounds.Right - left, line * 2f);

            using (var format = (StringFormat)ArcadeFonts.Format.Clone())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Near;
                format.Trimming = fits ? StringTrimming.None : StringTrimming.EllipsisCharacter;
                if (ArcadeFonts.IsOutlineFont(font))
                {
                    FillSmoothText(graphics, text, font, brush, layout, format);
                }
                else
                {
                    // 본문 글꼴(Noto Sans KR 등)은 작은 크기용 획 맞춤 정보가 들어 있는 글꼴이라, 그 정보를 써서 그려야
                    // 또렷하다. 윤곽선으로 그리면 굵은 글씨의 획이 픽셀 사이에 걸쳐 뭉개진다.
                    layout.Y = (float)Math.Round(layout.Y, MidpointRounding.AwayFromZero);
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    graphics.TextContrast = 0;
                    graphics.DrawString(text, font, brush, layout, format);
                }
            }
        }

        private static void FillSmoothText(Graphics graphics, string text, Font font, SolidBrush brush, RectangleF layout, StringFormat format)
        {
            SmoothingMode smoothing = graphics.SmoothingMode;
            PixelOffsetMode offset = graphics.PixelOffsetMode;
            CompositingQuality compositing = graphics.CompositingQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            // 밝은 글씨는 가장자리를 바탕과 섞을 때 밝기 보정을 한다. 보정이 없으면 어두운 바탕의 밝은 글씨가
            // 실제보다 가늘고 흐리게 보인다(작은 글씨일수록 심하다). 어두운 글씨(색 딱지, 밝은 단추 위)는
            // 보정하면 반대로 옅어지므로 하지 않는다.
            Color ink = brush.Color;
            bool lightText = ((ink.R * 299) + (ink.G * 587) + (ink.B * 114)) / 1000 >= 128;
            graphics.CompositingQuality = lightText ? CompositingQuality.GammaCorrected : CompositingQuality.Default;
            try
            {
                using (var path = new GraphicsPath())
                {
                    path.AddString(text, font.FontFamily, (int)font.Style, font.Size, layout, format);
                    graphics.FillPath(brush, path);
                }
            }
            finally
            {
                graphics.SmoothingMode = smoothing;
                graphics.PixelOffsetMode = offset;
                graphics.CompositingQuality = compositing;
            }
        }

        // 아래로 그림자가 떨어지는 도트 단추. 올리면 밝아지고 누르면 살짝 내려간다.
        public ArcadeZone Button(Graphics graphics, string key, string text, Font font, Color fill, Color fore, Color shadow, float shadowHeight, float x, float y, float width, float height, Action click)
        {
            float offset = IsDown(key) ? 3f : 0f;
            Color face = IsHot(key) ? ArcadeColors.Brighten(fill, 1.12f) : fill;
            Fill(graphics, shadow, x, y + offset + shadowHeight, width, height);
            Fill(graphics, face, x, y + offset, width, height);
            Write(graphics, text, font, fore, x, y + offset, width, height, 1);
            return Zone(key, x, y, width, height + shadowHeight, click);
        }

        // 테두리만 있는 작은 단추(삭제, 닫기).
        public ArcadeZone OutlineButton(Graphics graphics, string key, string text, Font font, Color color, Color back, float x, float y, float width, float height, Action click)
        {
            float offset = IsDown(key) ? 3f : 0f;
            Color ink = IsHot(key) ? color : ArcadeColors.Blend(back, color, 0.75f);
            Frame(graphics, ink, x, y + offset, width, height, 2f);
            Write(graphics, text, font, ink, x, y + offset, width, height, 1);
            return Zone(key, x, y, width, height, click);
        }

        // 작은 네모·동그라미는 실제 픽셀로 크기를 정한 뒤 줄 높이의 한가운데에 놓는다.
        // (좌표를 따로따로 반올림하면 작은 창에서 한쪽으로 1픽셀씩 쏠린다.)
        private Rectangle CenteredBox(float x, float y, float rowHeight, float size)
        {
            Rectangle row = Rect(x, y, size, rowHeight);
            int pixels = Math.Max(3, Px(size));
            return new Rectangle(row.X, row.Y + ((row.Height - pixels) / 2), pixels, pixels);
        }

        private static void FillBox(Graphics graphics, Color color, Rectangle rect)
        {
            using (var brush = new SolidBrush(color))
            {
                graphics.FillRectangle(brush, rect);
            }
        }

        public void CheckBox(Graphics graphics, float x, float y, float rowHeight, bool isChecked)
        {
            Rectangle box = CenteredBox(x, y, rowHeight, 26f);
            int border = Math.Max(1, Px(3f));
            FillBox(graphics, ArcadeColors.Line, box);
            FillBox(graphics, ArcadeColors.Background, Rectangle.Inflate(box, -border, -border));
            if (isChecked)
            {
                int inset = Math.Max(border + 1, Px(7f));
                FillBox(graphics, ArcadeColors.Text, Rectangle.Inflate(box, -inset, -inset));
            }
        }

        public void RadioButton(Graphics graphics, float x, float y, float rowHeight, bool isChecked)
        {
            Rectangle rect = CenteredBox(x, y, rowHeight, 26f);
            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(ArcadeColors.Background))
            using (var pen = new Pen(ArcadeColors.Line, Math.Max(1f, 3f * Zoom)))
            using (var dot = new SolidBrush(ArcadeColors.Text))
            {
                float inset = pen.Width / 2f;
                var circle = new RectangleF(rect.X + inset, rect.Y + inset, rect.Width - pen.Width, rect.Height - pen.Width);
                graphics.FillEllipse(fill, circle);
                graphics.DrawEllipse(pen, circle);
                if (isChecked)
                {
                    float size = Math.Max(3f, 10f * Zoom);
                    graphics.FillEllipse(dot, rect.X + ((rect.Width - size) / 2f), rect.Y + ((rect.Height - size) / 2f), size, size);
                }
            }
            graphics.SmoothingMode = previous;
        }

        // 종류 색 네모. outline이 0보다 크면 글자색 테두리를 두른다. x는 색 네모의 왼쪽이다.
        public void Swatch(Graphics graphics, Color color, float x, float y, float rowHeight, float size, float outline)
        {
            Rectangle box = CenteredBox(x, y, rowHeight, size);
            if (outline > 0f)
            {
                int edge = Math.Max(1, Px(outline));
                FillBox(graphics, ArcadeColors.Text, Rectangle.Inflate(box, edge, edge));
            }
            FillBox(graphics, color, box);
        }
        public void TriangleDown(Graphics graphics, Color color, float x, float y, float width, float height)
        {
            Rectangle rect = Rect(x, y, width, height);
            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(color))
            {
                graphics.FillPolygon(brush, new[]
                {
                    new PointF(rect.Left, rect.Top),
                    new PointF(rect.Right, rect.Top),
                    new PointF(rect.Left + (rect.Width / 2f), rect.Bottom)
                });
            }
            graphics.SmoothingMode = previous;
        }

        // 세로 스크롤바(굵기 22). offset은 0 ~ content - view.
        public void ScrollBar(Graphics graphics, string key, float x, float y, float height, float content, float view, float offset, Action<float> setOffset, Color track, Color thumb)
        {
            Fill(graphics, track, x, y, 22f, height);
            // 픽셀 반올림으로 생기는 한두 픽셀 차이는 넘친 것으로 보지 않는다.
            if (content <= view + 3f || height <= 0f)
            {
                return;
            }

            float max = content - view;
            float thumbHeight = Math.Min(height, Math.Max(60f, height * view / content));
            float travel = Math.Max(1f, height - thumbHeight);
            float thumbY = y + (travel * Math.Max(0f, Math.Min(1f, offset / max)));
            float inset = IsHot(key) || IsDown(key) ? 3f : 5f;
            Fill(graphics, thumb, x + inset, thumbY + inset, 22f - (inset * 2f), thumbHeight - (inset * 2f));

            Zone(key + "-track", x, y, 22f, height, delegate
            {
                float clicked = _lastMouse.Y / Zoom;
                setOffset(clicked < thumbY ? Math.Max(0f, offset - view) : Math.Min(max, offset + view));
            }).Cursor = Cursors.Default;

            float startOffset = 0f;
            ArcadeZone zone = Zone(key, x, thumbY, 22f, thumbHeight, null);
            zone.Cursor = Cursors.Default;
            zone.DragStart = delegate { startOffset = offset; };
            zone.Drag = delegate(float dx, float dy)
            {
                setOffset(Math.Max(0f, Math.Min(max, startOffset + (dy * max / travel))));
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 끄는 중인 자리(_dragZone)는 따로 잡고 있으므로 목록은 매번 새로 만든다.
            _zones.Clear();
            _zoneClip = Rectangle.Empty;
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            e.Graphics.SmoothingMode = SmoothingMode.None;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Default;
            if (Painter != null)
            {
                Painter(this, e.Graphics);
            }
        }

        private ArcadeZone HitTest(Point point)
        {
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                if (_zones[i].Bounds.Contains(point))
                {
                    return _zones[i];
                }
            }

            return null;
        }

        private void SetHot(ArcadeZone zone)
        {
            string key = zone == null ? null : zone.Key;
            if (key != _hotKey)
            {
                _hotKey = key;
                Invalidate();
            }

            Cursor = zone == null ? Cursors.Default : zone.Cursor;
            string tip = zone == null || zone.Tip == null ? "" : zone.Tip;
            if (tip != _tipText)
            {
                _tipText = tip;
                _toolTip.SetToolTip(this, tip);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragZone != null)
            {
                // 끄는 동안 이 판 자체가 움직일 수 있으므로(메모장) 메시지에 담긴 좌표가 아니라 지금 마우스 위치를 쓴다.
                Point now = Cursor.Position;
                _dragZone.Drag((now.X - _dragOrigin.X) / Zoom, (now.Y - _dragOrigin.Y) / Zoom);
                return;
            }

            SetHot(HitTest(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dragZone == null)
            {
                SetHot(null);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            _lastMouse = e.Location;
            ArcadeZone zone = HitTest(e.Location);
            if (zone == null)
            {
                return;
            }

            _downKey = zone.Key;
            if (zone.Drag != null)
            {
                _dragZone = zone;
                _dragOrigin = Cursor.Position;
                if (zone.DragStart != null)
                {
                    zone.DragStart();
                }
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            string downKey = _downKey;
            ArcadeZone dragZone = _dragZone;
            _downKey = null;
            _dragZone = null;
            Invalidate();

            if (dragZone != null)
            {
                if (dragZone.DragEnd != null)
                {
                    dragZone.DragEnd();
                }
                return;
            }

            ArcadeZone zone = HitTest(e.Location);
            if (zone != null && zone.Key == downKey && zone.Click != null)
            {
                zone.Click();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // 테두리 없는 입력칸. 바깥 테두리는 판이 그린다.
    internal class ArcadeTextBox : TextBox
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private string _placeholder = "";

        public ArcadeTextBox()
        {
            BorderStyle = BorderStyle.None;
            BackColor = ArcadeColors.Background;
            ForeColor = ArcadeColors.Text;
        }

        public string Placeholder
        {
            get { return _placeholder; }
            set
            {
                _placeholder = value ?? "";
                ApplyPlaceholder();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyPlaceholder();
        }

        private void ApplyPlaceholder()
        {
            if (IsHandleCreated && !Multiline)
            {
                SendMessage(Handle, 0x1501, (IntPtr)1, _placeholder); // EM_SETCUEBANNER
            }
        }

        // 테두리 상자(2560 기준 좌표) 안에 세로 가운데로 놓는다.
        public void Place(ArcadeSurface surface, float x, float y, float width, float height, float inset, FontFamily family, float pixels, bool bold)
        {
            Font font = surface.F(family, pixels, bold);
            if (!Object.ReferenceEquals(Font, font))
            {
                Font = font;
            }

            Rectangle frame = surface.Rect(x + inset, y, width - (inset * 2f), height);
            if (Multiline)
            {
                Bounds = frame;
                return;
            }

            int boxHeight = PreferredHeight;
            Rectangle bounds = new Rectangle(frame.X, frame.Y + ((frame.Height - boxHeight) / 2) + ArcadeFonts.BaselineDrop(font), Math.Max(4, frame.Width), boxHeight);
            if (Bounds != bounds)
            {
                Bounds = bounds;
            }
        }
    }

    // 숫자만 받는 입력칸.
    internal sealed class ArcadeNumberBox : ArcadeTextBox
    {
        private int _value = 100;
        private bool _updating;

        public int Minimum = 1;
        public int Maximum = 99999;
        public event EventHandler ValueChanged = delegate { };

        public ArcadeNumberBox()
        {
            Text = "100";
            MaxLength = 7;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int next = Math.Max(Minimum, Math.Min(Maximum, value));
                bool changed = next != _value;
                _value = next;
                ShowValue();
                if (changed)
                {
                    ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        private void ShowValue()
        {
            _updating = true;
            try
            {
                Text = _value.ToString();
            }
            finally
            {
                _updating = false;
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (!Char.IsControl(e.KeyChar) && !Char.IsDigit(e.KeyChar))
            {
                e.Handled = true;
            }
            base.OnKeyPress(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (_updating)
            {
                return;
            }

            string digits = System.Text.RegularExpressions.Regex.Replace(Text ?? "", "[^0-9]", "");
            int parsed;
            if (!Int32.TryParse(digits, out parsed))
            {
                parsed = Minimum;
            }

            int next = Math.Max(Minimum, Math.Min(Maximum, parsed));
            if (next != _value)
            {
                _value = next;
                ValueChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            ShowValue();
        }
    }
}
