using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GyeolheePinball
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (SelfUpdater.TryHandleApplyMode(args, UpdateProduct.Standard))
            {
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    WriteCrashLog(e.Exception);
                    MessageBox.Show(e.Exception.Message, "결희 종겜핀볼 실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    WriteCrashLog(e.ExceptionObject as Exception);
                };
                var mainForm = new MainForm();
                mainForm.Shown += delegate
                {
                    SelfUpdater.SignalSuccessfulStartup(args);
                    SelfUpdater.BeginUpdateCheck(mainForm, UpdateProduct.Standard);
                };
                Application.Run(mainForm);
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);
                MessageBox.Show(ex.Message, "결희 종겜핀볼 실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void WriteCrashLog(Exception ex)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(baseDir, "GyeolheePinball-error.log");
                string message = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine;
                message += ex == null ? "Unknown error" : ex.ToString();
                message += Environment.NewLine + Environment.NewLine;
                File.AppendAllText(path, message, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    internal static class UiFont
    {
        private static readonly string FamilyName = ResolveFamily();

        public static Font Make(float size, FontStyle style)
        {
            float snappedSize = Math.Max(7f, (float)Math.Round(size * 2f, MidpointRounding.AwayFromZero) / 2f);
            return new Font(FamilyName, snappedSize, style, GraphicsUnit.Point);
        }

        private static string ResolveFamily()
        {
            string[] preferred = new string[]
            {
                "Pretendard",
                "Noto Sans KR",
                "Malgun Gothic",
                "Segoe UI Variable Text",
                "Segoe UI"
            };

            try
            {
                foreach (FontFamily family in FontFamily.Families)
                {
                    for (int i = 0; i < preferred.Length; i++)
                    {
                        if (String.Equals(family.Name, preferred[i], StringComparison.OrdinalIgnoreCase))
                        {
                            return family.Name;
                        }
                    }
                }
            }
            catch
            {
            }

            return "Malgun Gothic";
        }
    }

    // MainForm(메인 화면)은 ArcadeMainForm.cs에 있다.

    internal sealed class SoopLiveInfo
    {
        public string StreamerId;
        public string StreamerName;
        public string Title;
        public string ChatNo;
        public string Token;
        public string Host;
        public int Port;
        public string Bps;
        public string GeoCc;
        public string GeoRc;
        public string AcceptLanguage;
        public string ServiceLanguage;
    }

    internal sealed class SoopLiveChatClient : IDisposable
    {
        private const string Fs = "\u000c";
        private const string Esc = "\u001b";
        private const string Tab = "\u0009";
        private const string CmdConnect = "\u001b\u0009000100000600\u000c\u000c\u000c16\u000c";

        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private SoopLiveInfo _info;
        private bool _disposed;
        private bool _joinedRaised;

        public event Action<SoopLiveInfo> BroadcastResolved = delegate { };
        public event Action Joined = delegate { };
        public event Action<GiftSource, string, int> BalloonReceived = delegate { };
        public event Action<string, string> ChatReceived = delegate { };
        public event Action<string> Disconnected = delegate { };

        public async Task ConnectAsync(string streamerId, string password)
        {
            if (String.IsNullOrWhiteSpace(streamerId))
            {
                throw new InvalidOperationException("SOOP ID가 비어 있습니다.");
            }

            _disposed = false;
            _joinedRaised = false;
            _cts = new CancellationTokenSource();
            _info = await LoadLiveInfoAsync(streamerId.Trim(), password ?? "");
            BroadcastResolved(_info);

            string wsUrl = "wss://" + _info.Host.ToLowerInvariant() + ":" + _info.Port + "/Websocket/" + _info.StreamerId;
            _socket = new ClientWebSocket();
            _socket.Options.AddSubProtocol("chat");
            await _socket.ConnectAsync(new Uri(wsUrl), _cts.Token);
            await SendRawAsync(CmdConnect);
            StartBackgroundLoops();
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                }
            }
            catch
            {
            }

            try
            {
                if (_socket != null)
                {
                    _socket.Abort();
                    _socket.Dispose();
                }
            }
            catch
            {
            }
        }

        private void StartBackgroundLoops()
        {
            Task.Run((Func<Task>)ReceiveLoopAsync);
            Task.Run((Func<Task>)PingLoopAsync);
        }

        private async Task<SoopLiveInfo> LoadLiveInfoAsync(string streamerId, string password)
        {
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;

            string json = await Task.Run(delegate
            {
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    client.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                    client.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    var values = new NameValueCollection();
                    values["bid"] = streamerId;
                    values["type"] = "live";
                    values["pwd"] = password ?? "";
                    values["player_type"] = "html5";
                    values["stream_type"] = "common";
                    values["mode"] = "landing";
                    values["from_api"] = "0";
                    byte[] response = client.UploadValues("https://live.sooplive.co.kr/afreeca/player_live_api.php?bjid=" + Uri.EscapeDataString(streamerId), "POST", values);
                    return Encoding.UTF8.GetString(response);
                }
            });

            return ParseLiveInfo(json, streamerId);
        }

        private SoopLiveInfo ParseLiveInfo(string json, string streamerId)
        {
            var root = _serializer.DeserializeObject(json) as Dictionary<string, object>;
            Dictionary<string, object> channel = GetObject(root, "CHANNEL");
            if (channel == null)
            {
                throw new InvalidOperationException("SOOP 방송 정보를 읽지 못했습니다.");
            }

            int result = GetInt(channel, "RESULT");
            if (result != 1)
            {
                throw new SoopNotLiveException(result, streamerId + " 방송이 현재 라이브 상태가 아닙니다.");
            }

            string host = GetString(channel, "CHDOMAIN");
            if (host.Length == 0)
            {
                host = GetString(channel, "CHIP");
            }

            int chatPort = GetInt(channel, "CHPT") + 1;
            if (host.Length == 0 || chatPort <= 1)
            {
                throw new InvalidOperationException("채팅 서버 정보를 찾지 못했습니다.");
            }

            var info = new SoopLiveInfo();
            info.StreamerId = GetString(channel, "BJID");
            if (info.StreamerId.Length == 0)
            {
                info.StreamerId = streamerId;
            }
            info.StreamerName = GetString(channel, "BJNICK");
            if (info.StreamerName.Length == 0)
            {
                info.StreamerName = info.StreamerId;
            }
            info.Title = GetString(channel, "TITLE");
            info.ChatNo = GetString(channel, "CHATNO");
            info.Token = GetString(channel, "FTK");
            info.Host = host;
            info.Port = chatPort;
            info.Bps = GetString(channel, "BPS");
            info.GeoCc = GetString(channel, "geo_cc");
            info.GeoRc = GetString(channel, "geo_rc");
            info.AcceptLanguage = GetString(channel, "acpt_lang");
            info.ServiceLanguage = GetString(channel, "svc_lang");
            return info;
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[8192];
            try
            {
                while (!_disposed && _socket != null && _socket.State == WebSocketState.Open)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                RaiseDisconnected("채팅 서버가 연결을 종료했습니다.");
                                return;
                            }

                            stream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        byte[] packet = stream.ToArray();
                        if (packet.Length > 0)
                        {
                            await ProcessPacketAsync(packet);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                RaiseDisconnected(ex.Message);
            }
        }

        private async Task PingLoopAsync()
        {
            try
            {
                while (!_disposed && _cts != null && !_cts.IsCancellationRequested)
                {
                    await Task.Delay(60000, _cts.Token);
                    await SendPacketAsync(0, Fs);
                }
            }
            catch
            {
            }
        }

        private async Task ProcessPacketAsync(byte[] packet)
        {
            if (packet.Length < 14)
            {
                return;
            }

            string header = Encoding.UTF8.GetString(packet, 0, 14);
            int serviceCommand;
            if (!Int32.TryParse(header.Substring(2, 4), out serviceCommand))
            {
                return;
            }

            int bodyStart = packet.Length > 15 ? 15 : 14;
            string body = packet.Length > bodyStart ? Encoding.UTF8.GetString(packet, bodyStart, packet.Length - bodyStart) : "";
            string[] parts = body.Split('\u000c');

            if (serviceCommand == 1)
            {
                await SendJoinAsync();
                return;
            }

            if (serviceCommand == 2)
            {
                if (parts.Length > 0 && parts[0] == "비밀번호가 틀렸습니다.")
                {
                    RaiseDisconnected("비밀번호가 틀렸습니다.");
                    return;
                }

                if (!_joinedRaised)
                {
                    _joinedRaised = true;
                    Joined();
                }
                return;
            }

            if (serviceCommand == 5)
            {
                if (parts.Length >= 6)
                {
                    string message = parts[0];
                    string nickname = parts[5];
                    if (nickname.Length > 0 && message.Length > 0)
                    {
                        ChatReceived(nickname, message);
                    }
                }
                return;
            }

            // 후원 패킷마다 닉네임과 개수의 필드 위치가 다르므로 분기별로 검증한다.
            if (serviceCommand == 18)
            {
                if (parts.Length >= 4)
                {
                    int count;
                    if (Int32.TryParse(parts[3], out count) && count > 0)
                    {
                        BalloonReceived(GiftSource.StarBalloon, parts[2], count);
                    }
                }
                return;
            }

            if (serviceCommand == 33)
            {
                if (parts.Length >= 6)
                {
                    int count;
                    if (Int32.TryParse(parts[5], out count) && count > 0)
                    {
                        BalloonReceived(GiftSource.StarBalloon, parts[4], count);
                    }
                }
                return;
            }

            if (serviceCommand == 87)
            {
                if (parts.Length >= 10)
                {
                    string nickname = parts[3];
                    if (nickname.Length == 0)
                    {
                        nickname = parts[2];
                    }

                    int count;
                    if (nickname.Length > 0 && Int32.TryParse(parts[9], out count) && count > 0)
                    {
                        BalloonReceived(GiftSource.AdBalloon, nickname, count);
                    }
                }
                return;
            }

            if (serviceCommand == 121)
            {
                if (parts.Length > 0)
                {
                    HandleChallengeGift(parts[0]);
                }
                return;
            }

            if (serviceCommand == 88)
            {
                RaiseDisconnected("방송이 종료되었습니다.");
            }
        }

        private void HandleChallengeGift(string json)
        {
            if (String.IsNullOrWhiteSpace(json))
            {
                return;
            }

            Dictionary<string, object> gift;
            try
            {
                gift = _serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                return;
            }

            if (gift == null)
            {
                return;
            }

            // 121번은 type으로 나뉜다. CHALLENGE_GIFT = 도전미션, GIFT = 대결미션. 그 밖(NOTICE, SETTLE 등)은 후원이 아니다.
            string type = GetString(gift, "type");
            GiftSource source;
            if (String.Equals(type, "CHALLENGE_GIFT", StringComparison.OrdinalIgnoreCase))
            {
                source = GiftSource.ChallengeGift;
            }
            else if (String.Equals(type, "GIFT", StringComparison.OrdinalIgnoreCase))
            {
                source = GiftSource.BattleMission;
            }
            else
            {
                return;
            }

            string nickname = GetString(gift, "user_nick");
            if (nickname.Length == 0)
            {
                nickname = GetString(gift, "user_id");
            }

            int count = GetInt(gift, "gift_count");
            if (nickname.Length > 0 && count > 0)
            {
                BalloonReceived(source, nickname, count);
            }
        }

        private async Task SendJoinAsync()
        {
            if (_info == null)
            {
                return;
            }

            var body = new StringBuilder();
            body.Append(Fs).Append(_info.ChatNo);
            body.Append(Fs).Append(_info.Token);
            body.Append(Fs).Append("0").Append(Fs).Append(Fs).Append("log\u0011");
            body.Append("\u0006&\u0006set_bps\u0006=\u0006").Append(_info.Bps);
            body.Append("\u0006&\u0006view_bps\u0006=\u0006").Append(_info.Bps);
            body.Append("\u0006&\u0006quality\u0006=\u0006ori");
            body.Append("\u0006&\u0006geo_cc\u0006=\u0006").Append(_info.GeoCc);
            body.Append("\u0006&\u0006geo_rc\u0006=\u0006").Append(_info.GeoRc);
            body.Append("\u0006&\u0006acpt_lang\u0006=\u0006").Append(_info.AcceptLanguage);
            body.Append("\u0006&\u0006svc_lang\u0006=\u0006").Append(_info.ServiceLanguage);
            body.Append("\u0006&\u0006subscribe\u0006=\u00060");
            body.Append("\u0006&\u0006lowlatency\u0006=\u00061");
            body.Append("\u0012pwd\u0011\u0012");
            body.Append("auth_info\u0011NULL\u0012");
            body.Append("pver\u00112\u0012");
            body.Append("access_system\u0011html5\u0012");
            body.Append(Fs);
            await SendPacketAsync(2, body.ToString());
        }

        private async Task SendPacketAsync(int serviceCommand, string body)
        {
            string header = Esc + Tab + serviceCommand.ToString("D4") + body.Length.ToString("D6") + "00";
            await SendRawAsync(header + body);
        }

        private async Task SendRawAsync(string text)
        {
            if (_disposed || _socket == null || _socket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
        }

        private void RaiseDisconnected(string reason)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Disconnected(reason);
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key))
            {
                return null;
            }

            return source[key] as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null)
            {
                return "";
            }

            return Convert.ToString(source[key]);
        }

        private static int GetInt(Dictionary<string, object> source, string key)
        {
            string value = GetString(source, key);
            int parsed;
            return Int32.TryParse(value, out parsed) ? parsed : 0;
        }
    }

    internal sealed class GiftSourcePopup : RoundedPanel
    {
        private readonly Color _line;
        private int _backdropSplitX;
        private Color _leftBackdropColor;
        private Color _rightBackdropColor;
        private Bitmap _backdropImage;
        private readonly GiftSourceOption _starBalloon;
        private readonly GiftSourceOption _adBalloon;
        private readonly GiftSourceOption _challengeGift;

        public event EventHandler SelectionChanged;
        public event EventHandler SelectionRejected;

        public bool CollectStarBalloon { get { return _starBalloon.Checked; } }
        public bool CollectAdBalloon { get { return _adBalloon.Checked; } }
        public bool CollectChallengeGift { get { return _challengeGift.Checked; } }

        public GiftSourcePopup(
            Color text,
            Color muted,
            Color purple,
            Color line,
            bool collectStarBalloon,
            bool collectAdBalloon,
            bool collectChallengeGift)
        {
            _line = line;
            Size = new Size(244, 140);
            Radius = 12;
            FillColor = Color.FromArgb(246, 240, 255);
            BorderColor = Color.FromArgb(97, 65, 144);
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            _leftBackdropColor = Color.FromArgb(229, 224, 250);
            _rightBackdropColor = FillColor;

            var title = new Label();
            title.Text = "수집 대상";
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.ForeColor = text;
            title.BackColor = Color.Transparent;
            title.Font = UiFont.Make(11.5f, FontStyle.Bold);
            title.SetBounds(14, 9, 216, 24);
            Controls.Add(title);

            var subtitle = new Label();
            subtitle.Text = "수집할 후원 종류를 선택하세요.";
            subtitle.AutoSize = false;
            subtitle.TextAlign = ContentAlignment.MiddleLeft;
            subtitle.ForeColor = muted;
            subtitle.BackColor = Color.Transparent;
            subtitle.Font = UiFont.Make(8.5f, FontStyle.Regular);
            subtitle.SetBounds(14, 32, 216, 20);
            Controls.Add(subtitle);

            _starBalloon = CreateOption("별풍선", 14, 58, 104, 32, collectStarBalloon, text, purple);
            _adBalloon = CreateOption("애드벌룬", 126, 58, 104, 32, collectAdBalloon, text, purple);
            _challengeGift = CreateOption("도전미션", 14, 98, 104, 32, collectChallengeGift, text, purple);
            Controls.Add(_starBalloon);
            Controls.Add(_adBalloon);
            Controls.Add(_challengeGift);

        }

        public void SetSelections(bool starBalloon, bool adBalloon, bool challengeGift)
        {
            _starBalloon.Checked = starBalloon;
            _adBalloon.Checked = adBalloon;
            _challengeGift.Checked = challengeGift;
        }

        public void SetBackdrop(int splitX, Color leftColor, Color rightColor)
        {
            int nextSplitX = Math.Max(0, Math.Min(Width, splitX));
            if (_backdropSplitX == nextSplitX &&
                _leftBackdropColor == leftColor &&
                _rightBackdropColor == rightColor)
            {
                return;
            }

            _backdropSplitX = nextSplitX;
            _leftBackdropColor = leftColor;
            _rightBackdropColor = rightColor;
            Invalidate();
        }

        public void SetBackdropImage(Bitmap image)
        {
            Bitmap nextBackdrop = image == null ? null : new Bitmap(image);
            Bitmap previousBackdrop = _backdropImage;
            _backdropImage = nextBackdrop;
            if (previousBackdrop != null)
            {
                previousBackdrop.Dispose();
            }
            Invalidate();
        }

        private GiftSourceOption CreateOption(string text, int x, int y, int width, int height, bool isChecked, Color foreColor, Color accent)
        {
            var option = new GiftSourceOption();
            option.Text = text;
            option.Checked = isChecked;
            option.SetBounds(x, y, width, height);
            option.Font = UiFont.Make(9.0f, FontStyle.Bold);
            option.ForeColor = foreColor;
            option.FillColor = Color.FromArgb(255, 253, 255);
            option.CheckedFillColor = Color.FromArgb(235, 224, 251);
            option.BorderColor = Color.FromArgb(198, 167, 228);
            option.AccentColor = accent;
            option.Cursor = Cursors.Hand;
            option.Click += HandleOptionClick;
            return option;
        }

        private void HandleOptionClick(object sender, EventArgs e)
        {
            if (!_starBalloon.Checked && !_adBalloon.Checked && !_challengeGift.Checked)
            {
                GiftSourceOption option = sender as GiftSourceOption;
                if (option != null)
                {
                    option.Checked = true;
                }
                if (SelectionRejected != null)
                {
                    SelectionRejected(this, EventArgs.Empty);
                }
                return;
            }

            if (SelectionChanged != null)
            {
                SelectionChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_backdropImage != null && _backdropImage.Size == ClientSize)
            {
                e.Graphics.DrawImageUnscaled(_backdropImage, Point.Empty);
                return;
            }

            if (_backdropSplitX > 0)
            {
                using (var leftBrush = new SolidBrush(_leftBackdropColor))
                {
                    e.Graphics.FillRectangle(leftBrush, 0, 0, _backdropSplitX, Height);
                }
            }

            using (var rightBrush = new SolidBrush(_rightBackdropColor))
            {
                e.Graphics.FillRectangle(rightBrush, _backdropSplitX, 0, Width - _backdropSplitX, Height);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            const int renderScale = 4;
            const int borderWidth = 2;
            int renderWidth = Width * renderScale;
            int renderHeight = Height * renderScale;
            using (var chrome = new Bitmap(renderWidth, renderHeight, PixelFormat.Format32bppPArgb))
            using (Graphics chromeGraphics = Graphics.FromImage(chrome))
            {
                chromeGraphics.Clear(Color.Transparent);
                chromeGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                chromeGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                chromeGraphics.CompositingQuality = CompositingQuality.HighQuality;

                Rectangle outerRect = new Rectangle(0, 0, renderWidth - 1, renderHeight - 1);
                int inset = borderWidth * renderScale;
                Rectangle innerRect = new Rectangle(
                    inset,
                    inset,
                    Math.Max(1, renderWidth - (inset * 2) - 1),
                    Math.Max(1, renderHeight - (inset * 2) - 1));
                using (GraphicsPath outerPath = Shape.Rounded(outerRect, Radius * renderScale))
                using (GraphicsPath innerPath = Shape.Rounded(innerRect, Math.Max(1, (Radius - borderWidth) * renderScale)))
                using (var borderBrush = new SolidBrush(BorderColor))
                using (var fillBrush = new SolidBrush(FillColor))
                {
                    chromeGraphics.FillPath(borderBrush, outerPath);
                    chromeGraphics.FillPath(fillBrush, innerPath);
                }

                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
                e.Graphics.CompositingMode = CompositingMode.SourceOver;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(
                    chrome,
                    new Rectangle(0, 0, Width, Height),
                    new Rectangle(0, 0, renderWidth, renderHeight),
                    GraphicsUnit.Pixel);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _backdropImage != null)
            {
                _backdropImage.Dispose();
                _backdropImage = null;
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class PopupDismissMessageFilter : IMessageFilter
    {
        private const int WmKeyDown = 0x0100;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmRightButtonDown = 0x0204;
        private const int WmMiddleButtonDown = 0x0207;
        private const int WmNonClientLeftButtonDown = 0x00A1;

        private readonly Control _popup;
        private readonly Control _anchor;
        private readonly Action _dismiss;

        public PopupDismissMessageFilter(Control popup, Control anchor, Action dismiss)
        {
            _popup = popup;
            _anchor = anchor;
            _dismiss = dismiss;
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (_popup == null || !_popup.Visible || _popup.IsDisposed)
            {
                return false;
            }

            if (message.Msg == WmKeyDown && (Keys)(int)message.WParam == Keys.Escape)
            {
                _dismiss();
                return true;
            }

            if (message.Msg != WmLeftButtonDown &&
                message.Msg != WmRightButtonDown &&
                message.Msg != WmMiddleButtonDown &&
                message.Msg != WmNonClientLeftButtonDown)
            {
                return false;
            }

            Point screenPoint = Control.MousePosition;
            bool insidePopup = _popup.RectangleToScreen(_popup.ClientRectangle).Contains(screenPoint);
            bool insideAnchor = _anchor != null && !_anchor.IsDisposed &&
                                _anchor.RectangleToScreen(_anchor.ClientRectangle).Contains(screenPoint);
            if (!insidePopup && !insideAnchor)
            {
                _dismiss();
            }

            return false;
        }
    }

    internal sealed class GiftSourceOption : Control
    {
        private bool _checked;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value)
                {
                    return;
                }

                _checked = value;
                Invalidate();
            }
        }

        public Color FillColor { get; set; }
        public Color CheckedFillColor { get; set; }
        public Color BorderColor { get; set; }
        public Color AccentColor { get; set; }

        public GiftSourceOption()
        {
            FillColor = Color.White;
            CheckedFillColor = Color.FromArgb(239, 231, 255);
            BorderColor = Color.FromArgb(213, 197, 235);
            AccentColor = Color.FromArgb(157, 105, 223);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Checked = !Checked;
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle body = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Checked ? CheckedFillColor : FillColor;
            Color border = Checked ? AccentColor : BorderColor;
            using (GraphicsPath bodyPath = Shape.Rounded(body, 8))
            using (SolidBrush bodyBrush = new SolidBrush(fill))
            using (Pen bodyPen = new Pen(border))
            {
                e.Graphics.FillPath(bodyBrush, bodyPath);
                e.Graphics.DrawPath(bodyPen, bodyPath);
            }

            int boxSize = 16;
            Rectangle box = new Rectangle(9, (Height - boxSize) / 2, boxSize, boxSize);
            using (GraphicsPath boxPath = Shape.Rounded(box, 4))
            using (SolidBrush boxBrush = new SolidBrush(Checked ? AccentColor : Color.White))
            using (Pen boxPen = new Pen(Checked ? AccentColor : Color.FromArgb(115, 133, 165)))
            {
                e.Graphics.FillPath(boxBrush, boxPath);
                e.Graphics.DrawPath(boxPen, boxPath);
            }

            if (Checked)
            {
                using (var checkPen = new Pen(Color.White, 2f))
                {
                    checkPen.StartCap = LineCap.Round;
                    checkPen.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(checkPen, new[]
                    {
                        new Point(box.X + 4, box.Y + 8),
                        new Point(box.X + 7, box.Y + 11),
                        new Point(box.X + 12, box.Y + 5)
                    });
                }
            }

            Rectangle textRect = new Rectangle(box.Right + 7, 0, Width - box.Right - 10, Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textRect,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        }
    }

    internal sealed class EntryRowControl : UserControl
    {
        public const int RowHeight = 40;
        private const int NameHorizontalPadding = 10;

        private CollectedEntry _entry;
        private int _index;
        private readonly Color _text;
        private readonly Color _muted;
        private readonly Color _purple;
        private readonly Color _lavender;
        private readonly Color _line;
        private readonly Color _card;
        private readonly Color _editFill = Color.FromArgb(250, 247, 255);
        private readonly Color _editBorder = Color.FromArgb(219, 201, 240);

        private readonly PillLabel _indexBadge;
        private readonly RoundedPanel _editFrame;
        private readonly TextBox _nameBox;
        private readonly Label _metaLabel;
        private readonly RoundedPanel _coinFrame;
        private readonly TextBox _coinBox;
        private readonly Label _deleteLabel;
        private bool _syncingEntryValues;
        private bool _showMeta;
        private bool _showCoin;

        public event EventHandler EntryChanged;
        public event EventHandler DeleteClicked;
        public event EventHandler BlankClicked;

        public CollectedEntry Entry { get { return _entry; } }
        public bool IsEditing { get { return (_nameBox != null && _nameBox.Focused) || (_coinBox != null && _coinBox.Focused); } }

        public EntryRowControl(CollectedEntry entry, int index, Color text, Color muted, Color purple, Color lavender, Color line, Color card)
        {
            _entry = entry;
            _index = index;
            _text = text;
            _muted = muted;
            _purple = purple;
            _lavender = lavender;
            _line = line;
            _card = card;

            BackColor = _card;
            Height = RowHeight;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            _indexBadge = new PillLabel();
            _indexBadge.Text = _index.ToString();
            _indexBadge.Font = UiFont.Make(9.0f, FontStyle.Bold);
            _indexBadge.ForeColor = Color.FromArgb(97, 65, 144);
            _indexBadge.FillColor = _lavender;
            _indexBadge.BorderColor = _lavender;
            _indexBadge.Radius = 8;
            Controls.Add(_indexBadge);
            // 표시 모드에서는 행이 직접 그린다. 숨긴 컨트롤은 글꼴·영역과 편집 상태를 유지한다.
            _indexBadge.Visible = false;

            _editFrame = new RoundedPanel();
            _editFrame.Radius = 7;
            _editFrame.FillColor = _editFill;
            _editFrame.BorderColor = _editBorder;
            _editFrame.BackColor = _card;
            Controls.Add(_editFrame);
            _editFrame.Visible = false;

            _nameBox = new TextBox();
            _nameBox.BorderStyle = BorderStyle.None;
            _nameBox.BackColor = _editFill;
            _nameBox.ForeColor = _text;
            _nameBox.Font = UiFont.Make(10.5f, FontStyle.Regular);
            _nameBox.TextAlign = HorizontalAlignment.Center;
            _nameBox.Text = _entry.PinballName;
            _nameBox.TextChanged += delegate
            {
                if (_syncingEntryValues)
                {
                    return;
                }

                _entry.PinballName = _nameBox.Text;
                if (EntryChanged != null)
                {
                    EntryChanged(this, EventArgs.Empty);
                }
            };
            _nameBox.GotFocus += delegate
            {
                _editFrame.BorderColor = _purple;
                Invalidate(_editFrame.Bounds);
            };
            _nameBox.LostFocus += delegate
            {
                _editFrame.BorderColor = _editBorder;
                _nameBox.Visible = false;
                Invalidate(_editFrame.Bounds);
            };
            Controls.Add(_nameBox);
            _nameBox.Visible = false;
            _nameBox.BringToFront();

            _metaLabel = new Label();
            _metaLabel.AutoSize = false;
            _metaLabel.BackColor = _card;
            _metaLabel.ForeColor = Color.FromArgb(96, 80, 127);
            _metaLabel.Font = UiFont.Make(8.5f, FontStyle.Regular);
            _metaLabel.TextAlign = ContentAlignment.MiddleLeft;
            _metaLabel.Text = _entry.Nickname + " · " + GiftSourceInfo.GetLabel(_entry.Source) + " " + _entry.BalloonCount + "개";
            Controls.Add(_metaLabel);
            _metaLabel.Visible = false;

            _coinFrame = new RoundedPanel();
            _coinFrame.Radius = 7;
            _coinFrame.FillColor = _lavender;
            _coinFrame.BorderColor = Color.FromArgb(191, 157, 228);
            _coinFrame.BackColor = _card;
            _coinFrame.Cursor = Cursors.IBeam;
            Controls.Add(_coinFrame);
            _coinFrame.Visible = false;

            _coinBox = new TextBox();
            _coinBox.BorderStyle = BorderStyle.None;
            _coinBox.BackColor = _lavender;
            _coinBox.ForeColor = Color.FromArgb(92, 59, 145);
            _coinBox.Font = UiFont.Make(9.5f, FontStyle.Bold);
            _coinBox.TextAlign = HorizontalAlignment.Center;
            _coinBox.Text = GetCoinDisplayText();
            _coinBox.TextChanged += delegate
            {
                if (_syncingEntryValues)
                {
                    return;
                }

                int coins;
                if (Int32.TryParse(_coinBox.Text.Trim(), out coins) && coins > 0)
                {
                    if (coins > 9999)
                    {
                        coins = 9999;
                        _syncingEntryValues = true;
                        _coinBox.Text = coins.ToString();
                        _coinBox.SelectionStart = _coinBox.TextLength;
                        _syncingEntryValues = false;
                    }

                    _entry.CoinCount = coins;
                    LayoutChildren();
                    if (EntryChanged != null)
                    {
                        EntryChanged(this, EventArgs.Empty);
                    }
                }
            };
            _coinBox.KeyPress += delegate(object sender, KeyPressEventArgs e)
            {
                if (!Char.IsControl(e.KeyChar) && !Char.IsDigit(e.KeyChar))
                {
                    e.Handled = true;
                }
            };
            _coinBox.GotFocus += delegate
            {
                _syncingEntryValues = true;
                _coinBox.Text = _entry.CoinCount.ToString();
                _syncingEntryValues = false;
                _coinFrame.BorderColor = _purple;
                Invalidate(_coinFrame.Bounds);
                MoveCoinCursorToEnd();
            };
            _coinBox.MouseUp += delegate
            {
                MoveCoinCursorToEnd();
            };
            _coinBox.LostFocus += delegate
            {
                if (_coinBox.Text.Trim().Length == 0 || _entry.CoinCount <= 0)
                {
                    _entry.CoinCount = 1;
                }

                _syncingEntryValues = true;
                _coinBox.Text = GetCoinDisplayText();
                _syncingEntryValues = false;
                _coinFrame.BorderColor = Color.FromArgb(191, 157, 228);
                _coinBox.Visible = false;
                Invalidate(_coinFrame.Bounds);
                if (EntryChanged != null)
                {
                    EntryChanged(this, EventArgs.Empty);
                }
            };
            Controls.Add(_coinBox);
            _coinBox.Visible = false;
            _coinBox.BringToFront();

            _deleteLabel = new Label();
            _deleteLabel.Text = "×";
            _deleteLabel.AutoSize = false;
            _deleteLabel.TextAlign = ContentAlignment.MiddleCenter;
            _deleteLabel.BackColor = _card;
            _deleteLabel.ForeColor = _muted;
            _deleteLabel.Font = UiFont.Make(9.2f, FontStyle.Regular);
            _deleteLabel.Cursor = Cursors.Hand;
            _deleteLabel.Click += delegate
            {
                if (DeleteClicked != null)
                {
                    DeleteClicked(this, EventArgs.Empty);
                }
            };
            Controls.Add(_deleteLabel);
            _deleteLabel.Visible = false;

            Resize += delegate { LayoutChildren(); };
            LayoutChildren();
        }

        private void RaiseBlankClicked(object sender, MouseEventArgs e)
        {
            if (BlankClicked != null)
            {
                BlankClicked(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _deleteLabel.Bounds.Contains(e.Location))
            {
                if (DeleteClicked != null)
                {
                    DeleteClicked(this, EventArgs.Empty);
                }
                return;
            }

            if (e.Button == MouseButtons.Left && _editFrame.Bounds.Contains(e.Location))
            {
                _coinBox.Visible = false;
                _nameBox.Visible = true;
                _nameBox.BringToFront();
                _nameBox.Focus();
                _nameBox.SelectionStart = _nameBox.TextLength;
                return;
            }

            if (e.Button == MouseButtons.Left && _showCoin && _coinFrame.Bounds.Contains(e.Location))
            {
                _nameBox.Visible = false;
                _coinBox.Visible = true;
                _coinBox.BringToFront();
                _coinBox.Focus();
                MoveCoinCursorToEnd();
                return;
            }

            RaiseBlankClicked(this, e);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor next = Cursors.Default;
            if (_deleteLabel.Bounds.Contains(e.Location))
            {
                next = Cursors.Hand;
            }
            else if (_editFrame.Bounds.Contains(e.Location) || (_showCoin && _coinFrame.Bounds.Contains(e.Location)))
            {
                next = Cursors.IBeam;
            }

            if (Cursor != next)
            {
                Cursor = next;
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        public void SetIndex(int index)
        {
            if (_index == index)
            {
                return;
            }

            _index = index;
            _indexBadge.Text = _index.ToString();
            Invalidate(_indexBadge.Bounds);
        }

        public void SetEntry(CollectedEntry entry, int index)
        {
            if (entry == null)
            {
                return;
            }

            if (Object.ReferenceEquals(_entry, entry))
            {
                SetIndex(index);
                return;
            }

            _entry = entry;
            _index = index;
            _syncingEntryValues = true;
            try
            {
                _indexBadge.Text = _index.ToString();
                _nameBox.Text = _entry.PinballName;
                _metaLabel.Text = _entry.Nickname + " · " + GiftSourceInfo.GetLabel(_entry.Source) + " " + _entry.BalloonCount + "개";
                _coinBox.Text = GetCoinDisplayText();
            }
            finally
            {
                _syncingEntryValues = false;
            }

            LayoutChildren();
            Invalidate();
        }

        public void RefreshEntryValues()
        {
            _metaLabel.Text = _entry.Nickname + " · " + GiftSourceInfo.GetLabel(_entry.Source) + " " + _entry.BalloonCount + "개";
            if (!_nameBox.Focused && _nameBox.Text != _entry.PinballName)
            {
                _syncingEntryValues = true;
                _nameBox.Text = _entry.PinballName;
                _syncingEntryValues = false;
            }
            if (!_coinBox.Focused && _coinBox.Text != GetCoinDisplayText())
            {
                _syncingEntryValues = true;
                _coinBox.Text = GetCoinDisplayText();
                _syncingEntryValues = false;
            }
            LayoutChildren();
            Invalidate();
        }

        private void LayoutChildren()
        {
            int badge = 22;
            int left = 0;
            int editX = left + badge + 8;
            int deleteW = 28;
            int coinW = _entry.CoinCount >= 1000 ? 80 : (_entry.CoinCount >= 100 ? 74 : 68);
            int coinX = Width - deleteW - coinW - 10;
            bool showMeta = Width >= 470;
            int metaW = showMeta ? Math.Min(220, Math.Max(160, Width / 5)) : 0;
            int metaX = coinX - metaW - 8;
            int editW = Math.Max(150, metaX - editX - 10);
            if (!showMeta)
            {
                editW = Math.Max(110, coinX - editX - 10);
            }

            _indexBadge.SetBounds(left, (RowHeight - badge) / 2, badge, badge);
            _deleteLabel.SetBounds(Width - deleteW - 4, (RowHeight - 26) / 2, deleteW, 26);
            const int controlY = 5;
            _editFrame.SetBounds(editX, controlY, editW, 30);
            _nameBox.SetBounds(
                editX + NameHorizontalPadding,
                controlY + Math.Max(0, (30 - _nameBox.PreferredHeight) / 2),
                Math.Max(40, editW - (NameHorizontalPadding * 2)),
                _nameBox.PreferredHeight);
            _metaLabel.SetBounds(metaX, controlY, metaW, 30);
            _coinFrame.SetBounds(coinX, controlY, coinW, 30);
            _coinBox.SetBounds(coinX + 5, controlY + Math.Max(0, (30 - _coinBox.PreferredHeight) / 2), Math.Max(18, coinW - 10), _coinBox.PreferredHeight);
            _showMeta = showMeta;
            _showCoin = Width >= 330;
            if (!_showCoin)
            {
                _coinBox.Visible = false;
            }
        }

        private string GetCoinDisplayText()
        {
            return _entry.CoinCount.ToString() + " 코인";
        }

        private void MoveCoinCursorToEnd()
        {
            if (_coinBox == null)
            {
                return;
            }

            Action move = delegate
            {
                if (!_coinBox.IsDisposed)
                {
                    _coinBox.SelectionStart = _coinBox.TextLength;
                    _coinBox.SelectionLength = 0;
                }
            };

            if (IsHandleCreated)
            {
                BeginInvoke(move);
            }
            else
            {
                move();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(_card))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle badgeBounds = new Rectangle(
                _indexBadge.Left,
                _indexBadge.Top,
                Math.Max(1, _indexBadge.Width - 1),
                Math.Max(1, _indexBadge.Height - 1));
            using (GraphicsPath badgePath = Shape.Rounded(badgeBounds, _indexBadge.Radius))
            using (var badgeBrush = new SolidBrush(_indexBadge.FillColor))
            {
                e.Graphics.FillPath(badgeBrush, badgePath);
            }
            TextRenderer.DrawText(
                e.Graphics,
                _index.ToString(),
                _indexBadge.Font,
                _indexBadge.Bounds,
                _indexBadge.ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            Rectangle editBounds = new Rectangle(
                _editFrame.Left,
                _editFrame.Top,
                Math.Max(1, _editFrame.Width - 1),
                Math.Max(1, _editFrame.Height - 1));
            using (GraphicsPath editPath = Shape.Rounded(editBounds, _editFrame.Radius))
            using (var editBrush = new SolidBrush(_editFill))
            using (var editPen = new Pen(_editFrame.BorderColor))
            {
                e.Graphics.FillPath(editBrush, editPath);
                e.Graphics.DrawPath(editPen, editPath);
            }
            if (!_nameBox.Visible)
            {
                Rectangle nameBounds = _editFrame.Bounds;
                nameBounds.Inflate(-NameHorizontalPadding, 0);
                TextRenderer.DrawText(
                    e.Graphics,
                    _entry.PinballName,
                    _nameBox.Font,
                    nameBounds,
                    _nameBox.ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            if (_showMeta)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    _entry.Nickname + " · " + GiftSourceInfo.GetLabel(_entry.Source) + " " + _entry.BalloonCount + "개",
                    _metaLabel.Font,
                    _metaLabel.Bounds,
                    _metaLabel.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            if (_showCoin)
            {
                Rectangle coinBounds = new Rectangle(
                    _coinFrame.Left,
                    _coinFrame.Top,
                    Math.Max(1, _coinFrame.Width - 1),
                    Math.Max(1, _coinFrame.Height - 1));
                using (GraphicsPath coinPath = Shape.Rounded(coinBounds, _coinFrame.Radius))
                using (var coinBrush = new SolidBrush(_lavender))
                using (var coinPen = new Pen(_coinFrame.BorderColor))
                {
                    e.Graphics.FillPath(coinBrush, coinPath);
                    e.Graphics.DrawPath(coinPen, coinPath);
                }
                if (!_coinBox.Visible)
                {
                    TextRenderer.DrawText(
                        e.Graphics,
                        GetCoinDisplayText(),
                        _coinBox.Font,
                        _coinFrame.Bounds,
                        _coinBox.ForeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                }
            }

            TextRenderer.DrawText(
                e.Graphics,
                "×",
                _deleteLabel.Font,
                _deleteLabel.Bounds,
                _deleteLabel.ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            using (var pen = new Pen(_line))
            {
                e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }
    }

    internal sealed class InputRequiredDialog : Form
    {
        private readonly Color _line;
        private readonly Color _surface;

        public InputRequiredDialog(Font baseFont, Color text, Color muted, Color purple, Color line)
        {
            _line = line;
            _surface = Color.FromArgb(250, 247, 255);

            Text = "입력 필요";
            ClientSize = new Size(358, 164);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = _surface;
            Font = baseFont;
            Padding = new Padding(1);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            var close = new Label();
            close.Text = "×";
            close.AutoSize = false;
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.ForeColor = muted;
            close.Font = UiFont.Make(11f, FontStyle.Regular);
            close.Cursor = Cursors.Hand;
            close.SetBounds(ClientSize.Width - 42, 16, 24, 24);
            close.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(close);

            var title = new Label();
            title.Text = "입력 필요";
            title.AutoSize = false;
            title.BackColor = Color.Transparent;
            title.ForeColor = text;
            title.Font = UiFont.Make(13.2f, FontStyle.Bold);
            title.SetBounds(24, 20, 250, 28);
            Controls.Add(title);

            var info = new PillLabel();
            info.Text = "i";
            info.TextAlign = ContentAlignment.MiddleCenter;
            info.Font = UiFont.Make(12f, FontStyle.Bold);
            info.ForeColor = Color.White;
            info.FillColor = purple;
            info.BorderColor = purple;
            info.Radius = 19;
            info.SetBounds(24, 62, 38, 38);
            Controls.Add(info);

            var body = new Label();
            body.Text = "SOOP 방송 주소 또는 SOOP ID를\r\n입력해 주세요.";
            body.AutoSize = false;
            body.BackColor = Color.Transparent;
            body.ForeColor = muted;
            body.Font = UiFont.Make(9.1f, FontStyle.Regular);
            body.TextAlign = ContentAlignment.MiddleLeft;
            body.SetBounds(76, 60, 256, 44);
            Controls.Add(body);

            var confirm = DialogButton("확인", Color.White, purple, purple);
            confirm.SetBounds(252, 112, 82, 36);
            confirm.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(confirm);

            Resize += delegate { ApplyWindowRegion(); };
            ApplyWindowRegion();
        }

        private RoundButton DialogButton(string text, Color foreColor, Color fillColor, Color borderColor)
        {
            var button = new RoundButton();
            button.Text = text;
            button.ForeColor = foreColor;
            button.FillColor = fillColor;
            button.BorderColor = borderColor;
            button.CanvasColor = _surface;
            button.Radius = 11;
            button.Font = UiFont.Make(9.2f, FontStyle.Bold);
            return button;
        }

        private void ApplyWindowRegion()
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            using (GraphicsPath path = Shape.Rounded(new Rectangle(0, 0, Width, Height), 18))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter || keyData == Keys.Escape)
            {
                DialogResult = DialogResult.OK;
                Close();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, 18))
            using (SolidBrush brush = new SolidBrush(_surface))
            using (Pen pen = new Pen(_line))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal sealed class ClearEntriesDialog : Form
    {
        private readonly Color _line;
        private readonly Color _surface;

        public ClearEntriesDialog(Font baseFont, Color text, Color muted, Color purple, Color line)
        {
            _line = line;
            _surface = Color.FromArgb(246, 240, 255);

            Text = "목록 비우기";
            ClientSize = new Size(244, 132);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = _surface;
            Font = baseFont;
            Padding = new Padding(1);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            var close = new Label();
            close.Text = "×";
            close.AutoSize = false;
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.ForeColor = muted;
            close.Font = UiFont.Make(11f, FontStyle.Regular);
            close.Cursor = Cursors.Hand;
            close.SetBounds(ClientSize.Width - 34, 10, 24, 24);
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(close);

            var title = new Label();
            title.Text = "수집 목록을 비울까요?";
            title.AutoSize = false;
            title.BackColor = Color.Transparent;
            title.ForeColor = text;
            title.Font = UiFont.Make(12.2f, FontStyle.Bold);
            title.SetBounds(18, 13, 180, 26);
            Controls.Add(title);

            var body = new Label();
            body.Text = "목록과 핀볼 입력값이 함께 비워집니다.";
            body.AutoSize = false;
            body.BackColor = Color.Transparent;
            body.ForeColor = Color.FromArgb(105, 91, 137);
            body.Font = UiFont.Make(8.9f, FontStyle.Regular);
            body.SetBounds(18, 40, 216, 25);
            Controls.Add(body);

            var cancel = DialogButton("취소", Color.FromArgb(82, 68, 111), Color.White, Color.FromArgb(205, 184, 232));
            cancel.SetBounds(82, 82, 68, 34);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            var clear = DialogButton("비우기", Color.White, purple, purple);
            clear.SetBounds(158, 82, 68, 34);
            clear.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(clear);

            Resize += delegate { ApplyWindowRegion(); };
            ApplyWindowRegion();
        }

        private RoundButton DialogButton(string text, Color foreColor, Color fillColor, Color borderColor)
        {
            var button = new RoundButton();
            button.Text = text;
            button.ForeColor = foreColor;
            button.FillColor = fillColor;
            button.BorderColor = borderColor;
            button.CanvasColor = _surface;
            button.Radius = 11;
            button.Font = UiFont.Make(9.2f, FontStyle.Bold);
            return button;
        }

        private void ApplyWindowRegion()
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            using (GraphicsPath path = Shape.Rounded(new Rectangle(0, 0, Width, Height), 18))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = Shape.Rounded(rect, 18))
            using (SolidBrush brush = new SolidBrush(_surface))
            using (Pen pen = new Pen(Color.FromArgb(198, 169, 227)))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal sealed class FocusSinkPanel : Panel
    {
        public FocusSinkPanel()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = false;
            BackColor = Color.Transparent;
        }
    }

    internal sealed class ToastBanner : Control
    {
        public ToastBanner()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
            ForeColor = Color.White;
            Font = UiFont.Make(9.0f, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle shadowRect = new Rectangle(2, 3, Width - 5, Height - 5);
            Rectangle bodyRect = new Rectangle(0, 0, Width - 5, Height - 6);

            using (GraphicsPath shadowPath = Shape.Rounded(shadowRect, 12))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(58, 4, 8, 24)))
            {
                e.Graphics.FillPath(shadow, shadowPath);
            }

            using (GraphicsPath bodyPath = Shape.Rounded(bodyRect, 12))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(62, 44, 92)))
            using (Pen border = new Pen(Color.FromArgb(188, 148, 226)))
            {
                e.Graphics.FillPath(fill, bodyPath);
                e.Graphics.DrawPath(border, bodyPath);
            }

            using (SolidBrush accent = new SolidBrush(Color.FromArgb(119, 192, 239)))
            {
                e.Graphics.FillEllipse(accent, bodyRect.X + 15, bodyRect.Y + ((bodyRect.Height - 8) / 2), 8, 8);
            }

            Rectangle textRect = new Rectangle(bodyRect.X + 40, bodyRect.Y + 1, bodyRect.Width - 56, bodyRect.Height - 2);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textRect,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            DoubleBuffered = true;
        }
    }

    internal sealed class VerticalScrollPanel : Panel
    {
        private const int ScrollBarInset = 14;
        private const int ThumbWidth = 7;
        private const int MinimumThumbHeight = 32;
        private readonly Color _thumbColor = Color.FromArgb(184, 190, 200);
        private readonly Color _thumbHoverColor = Color.FromArgb(159, 169, 184);
        private readonly Color _thumbDragColor = Color.FromArgb(126, 137, 154);
        private int _contentHeight;
        private int _scrollOffset;
        private int _wheelRemainder;
        private bool _thumbHot;
        private bool _dragging;
        private int _dragStartY;
        private int _dragStartOffset;

        public event EventHandler ScrollOffsetChanged;

        public int ContentWidth
        {
            get { return Math.Max(1, ClientSize.Width - (HasScrollBar ? ScrollBarInset : 0)); }
        }

        public int ScrollOffset
        {
            get { return _scrollOffset; }
        }

        public bool HasScrollBar
        {
            get { return _contentHeight > ClientSize.Height && ClientSize.Height > 0; }
        }

        public VerticalScrollPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            DoubleBuffered = true;
            AutoScroll = false;
            TabStop = false;
        }

        public void SetContentHeight(int contentHeight)
        {
            int nextHeight = Math.Max(0, contentHeight);
            if (_contentHeight == nextHeight)
            {
                SetScrollOffset(_scrollOffset);
                return;
            }

            _contentHeight = nextHeight;
            SetScrollOffset(_scrollOffset);
            Invalidate();
        }

        public void ScrollTo(int offset)
        {
            SetScrollOffset(offset);
        }

        public void ScrollToBottom()
        {
            SetScrollOffset(MaxScrollOffset);
        }

        public void ScrollByWheel(int delta)
        {
            if (!HasScrollBar || delta == 0)
            {
                return;
            }

            _wheelRemainder += delta;
            int detents = _wheelRemainder / SystemInformation.MouseWheelScrollDelta;
            if (detents == 0)
            {
                return;
            }

            _wheelRemainder -= detents * SystemInformation.MouseWheelScrollDelta;
            SetScrollOffset(_scrollOffset - (detents * EntryRowControl.RowHeight));
        }

        private int MaxScrollOffset
        {
            get { return Math.Max(0, _contentHeight - ClientSize.Height); }
        }

        private void SetScrollOffset(int offset)
        {
            int clamped = Math.Max(0, Math.Min(MaxScrollOffset, offset));
            if (_scrollOffset == clamped)
            {
                return;
            }

            _scrollOffset = clamped;
            Invalidate(new Rectangle(Math.Max(0, Width - ScrollBarInset), 0, ScrollBarInset, Height));
            if (ScrollOffsetChanged != null)
            {
                ScrollOffsetChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            SetScrollOffset(_scrollOffset);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollByWheel(e.Delta);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HasScrollBar && e.X >= Width - ScrollBarInset)
            {
                Rectangle thumb = GetThumbBounds();
                if (thumb.Contains(e.Location))
                {
                    _dragging = true;
                    _dragStartY = e.Y;
                    _dragStartOffset = _scrollOffset;
                    Capture = true;
                }
                else
                {
                    int page = Math.Max(EntryRowControl.RowHeight, ClientSize.Height - EntryRowControl.RowHeight);
                    SetScrollOffset(_scrollOffset + (e.Y < thumb.Top ? -page : page));
                }

                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                Rectangle track = GetTrackBounds();
                Rectangle thumb = GetThumbBounds();
                int travel = Math.Max(1, track.Height - thumb.Height);
                int offset = _dragStartOffset + ((e.Y - _dragStartY) * MaxScrollOffset / travel);
                SetScrollOffset(offset);
            }

            bool hot = HasScrollBar && GetThumbBounds().Contains(e.Location);
            if (_thumbHot != hot)
            {
                _thumbHot = hot;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                Invalidate();
            }

            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!_dragging && _thumbHot)
            {
                _thumbHot = false;
                Invalidate();
            }

            base.OnMouseLeave(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture && _dragging)
            {
                _dragging = false;
                Invalidate();
            }

            base.OnMouseCaptureChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!HasScrollBar)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle thumb = GetThumbBounds();
            Color color = _dragging ? _thumbDragColor : (_thumbHot ? _thumbHoverColor : _thumbColor);
            using (GraphicsPath path = Shape.Rounded(thumb, 3))
            using (var brush = new SolidBrush(color))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        private Rectangle GetTrackBounds()
        {
            return new Rectangle(Math.Max(0, Width - ScrollBarInset), 4, ScrollBarInset, Math.Max(1, Height - 8));
        }

        private Rectangle GetThumbBounds()
        {
            if (!HasScrollBar)
            {
                return Rectangle.Empty;
            }

            Rectangle track = GetTrackBounds();
            int thumbHeight = Math.Min(
                track.Height,
                Math.Max(MinimumThumbHeight, (int)Math.Round(track.Height * ((double)ClientSize.Height / Math.Max(1, _contentHeight)))));
            int travel = Math.Max(0, track.Height - thumbHeight);
            int thumbY = track.Y;
            if (MaxScrollOffset > 0)
            {
                thumbY += (int)Math.Round(travel * ((double)_scrollOffset / MaxScrollOffset));
            }

            return new Rectangle(Width - ThumbWidth - 2, thumbY, ThumbWidth, thumbHeight);
        }
    }

    internal sealed class GradientSurface : Panel
    {
        public Color TopColor { get; set; }
        public Color BottomColor { get; set; }

        public GradientSurface()
        {
            TopColor = Color.White;
            BottomColor = Color.White;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            using (var brush = new LinearGradientBrush(bounds, TopColor, BottomColor, LinearGradientMode.ForwardDiagonal))
            {
                e.Graphics.FillRectangle(brush, bounds);
            }

            using (var dot = new SolidBrush(Color.FromArgb(24, 173, 132, 219)))
            {
                for (int x = 12; x < Width; x += 28)
                {
                    for (int y = 8; y < Height + Math.Abs(AutoScrollPosition.Y); y += 28)
                    {
                        e.Graphics.FillEllipse(dot, x, y + AutoScrollPosition.Y, 2, 2);
                    }
                }
            }
        }
    }

    internal class RoundedPanel : Panel
    {
        public int Radius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }

        public RoundedPanel()
        {
            Radius = 16;
            FillColor = Color.White;
            BorderColor = Color.FromArgb(213, 197, 235);
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, Radius))
            using (SolidBrush brush = new SolidBrush(FillColor))
            using (Pen pen = new Pen(BorderColor))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal sealed class PillLabel : Label
    {
        public int Radius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }

        public PillLabel()
        {
            Radius = 12;
            FillColor = Color.White;
            BorderColor = Color.White;
            BackColor = Color.Transparent;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, Radius))
            using (SolidBrush brush = new SolidBrush(FillColor))
            using (Pen pen = new Pen(BorderColor))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }

            TextRenderer.DrawText(e.Graphics, Text, Font, rect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class CreditBadge : Control
    {
        public int Radius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public Color MarkStartColor { get; set; }
        public Color MarkEndColor { get; set; }

        public CreditBadge()
        {
            Radius = 10;
            FillColor = Color.FromArgb(238, 229, 255);
            BorderColor = Color.FromArgb(190, 157, 224);
            MarkStartColor = Color.FromArgb(205, 146, 235);
            MarkEndColor = Color.FromArgb(115, 193, 239);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, Radius))
            using (SolidBrush brush = new SolidBrush(FillColor))
            using (Pen pen = new Pen(BorderColor))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }

            int markSize = Math.Min(8, Math.Max(7, Height - 11));
            Rectangle markRect = new Rectangle(7, (Height - markSize) / 2, markSize, markSize);
            using (LinearGradientBrush markBrush = new LinearGradientBrush(markRect, MarkStartColor, MarkEndColor, LinearGradientMode.ForwardDiagonal))
            {
                e.Graphics.FillEllipse(markBrush, markRect);
            }

            using (SolidBrush shine = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
            {
                e.Graphics.FillEllipse(shine, markRect.X + 2, markRect.Y + 2, 2, 2);
            }

            Rectangle textRect = new Rectangle(markRect.Right + 4, -1, Width - markRect.Right - 5, Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textRect,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    internal sealed class GradientBadge : Control
    {
        public Color StartColor { get; set; }
        public Color EndColor { get; set; }

        public GradientBadge()
        {
            StartColor = Color.White;
            EndColor = Color.White;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, 12))
            using (LinearGradientBrush brush = new LinearGradientBrush(rect, StartColor, EndColor, LinearGradientMode.ForwardDiagonal))
            {
                e.Graphics.FillPath(brush, path);
            }

            TextRenderer.DrawText(e.Graphics, Text, Font, rect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class ImageBadge : Control
    {
        public Image Image { get; set; }
        public int Radius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }

        public ImageBadge()
        {
            Radius = 12;
            FillColor = Color.White;
            BorderColor = Color.FromArgb(213, 197, 235);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, Radius))
            using (SolidBrush brush = new SolidBrush(FillColor))
            using (Pen pen = new Pen(BorderColor))
            {
                e.Graphics.FillPath(brush, path);

                GraphicsState state = e.Graphics.Save();
                e.Graphics.SetClip(path);
                if (Image != null)
                {
                    Rectangle imageRect = Cover(Image.Size, rect);
                    e.Graphics.DrawImage(Image, imageRect);
                }
                e.Graphics.Restore(state);

                e.Graphics.DrawPath(pen, path);
            }
        }

        private static Rectangle Cover(Size imageSize, Rectangle bounds)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0)
            {
                return bounds;
            }

            float scale = Math.Max((float)bounds.Width / imageSize.Width, (float)bounds.Height / imageSize.Height);
            int width = (int)Math.Ceiling(imageSize.Width * scale);
            int height = (int)Math.Ceiling(imageSize.Height * scale);
            int x = bounds.X + ((bounds.Width - width) / 2);
            int y = bounds.Y + ((bounds.Height - height) / 2);
            return new Rectangle(x, y, width, height);
        }
    }

    internal enum ButtonGlyph
    {
        None,
        Flask,
        Trash,
        Play,
        Stop
    }

    internal sealed class RoundButton : Control
    {
        private bool _hover;
        private bool _pressed;

        public int Radius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public Color GradientColor { get; set; }
        public Color CanvasColor { get; set; }
        public bool UseGradient { get; set; }
        public ButtonGlyph Glyph { get; set; }

        public RoundButton()
        {
            Radius = 10;
            FillColor = Color.White;
            BorderColor = Color.FromArgb(213, 197, 235);
            GradientColor = Color.Empty;
            CanvasColor = Color.White;
            UseGradient = false;
            Glyph = ButtonGlyph.None;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = CanvasColor;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            _pressed = true;
            Invalidate();
            Focus();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = FillColor;
            if (_pressed)
            {
                fill = Shift(fill, -14);
            }
            else if (_hover)
            {
                fill = Shift(fill, 8);
            }

            using (SolidBrush background = new SolidBrush(CanvasColor))
            {
                e.Graphics.FillRectangle(background, ClientRectangle);
            }

            using (GraphicsPath path = Shape.Rounded(rect, Radius))
            using (Pen pen = new Pen(BorderColor))
            {
                if (UseGradient && GradientColor != Color.Empty)
                {
                    using (LinearGradientBrush brush = new LinearGradientBrush(rect, fill, GradientColor, LinearGradientMode.Horizontal))
                    {
                        e.Graphics.FillPath(brush, path);
                    }
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(fill))
                    {
                        e.Graphics.FillPath(brush, path);
                    }
                }

                e.Graphics.DrawPath(pen, path);
            }

            DrawCenteredButtonContent(e.Graphics, rect);
        }

        private void DrawCenteredButtonContent(Graphics graphics, Rectangle rect)
        {
            string text = Text ?? "";
            Size textSize = TextRenderer.MeasureText(
                graphics,
                text,
                Font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int iconSize = Glyph == ButtonGlyph.None ? 0 : ((Glyph == ButtonGlyph.Play || Glyph == ButtonGlyph.Stop) ? 18 : 16);
            int gap = iconSize > 0 && text.Length > 0 ? 7 : 0;
            int contentWidth = Math.Min(rect.Width - 6, iconSize + gap + textSize.Width);
            int startX = rect.X + Math.Max(3, (rect.Width - contentWidth) / 2);

            if (iconSize > 0)
            {
                int iconOffsetY = Glyph == ButtonGlyph.Flask || Glyph == ButtonGlyph.Trash ? 1 : 0;
                DrawGlyph(graphics, new Rectangle(startX, rect.Y + ((rect.Height - iconSize) / 2) + iconOffsetY, iconSize, iconSize));
            }

            Rectangle textRect = new Rectangle(startX + iconSize + gap, rect.Y, Math.Max(1, contentWidth - iconSize - gap), rect.Height);
            TextRenderer.DrawText(
                graphics,
                text,
                Font,
                textRect,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawGlyph(Graphics graphics, Rectangle bounds)
        {
            Func<float, float, PointF> point = delegate(float x, float y)
            {
                return new PointF(
                    bounds.X + (x * bounds.Width / 24f),
                    bounds.Y + (y * bounds.Height / 24f));
            };

            using (var pen = new Pen(ForeColor, Math.Max(1.5f, bounds.Width * 0.095f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (Glyph == ButtonGlyph.Flask)
                {
                    graphics.DrawLine(pen, point(8.5f, 2f), point(15.5f, 2f));
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(point(10f, 2f), point(10f, 8.5f));
                        path.AddLine(point(10f, 8.5f), point(5.97f, 15.58f));
                        path.AddBezier(
                            point(5.97f, 15.58f),
                            point(3.94f, 19.14f),
                            point(6.42f, 21f),
                            point(9.45f, 21f));
                        path.AddLine(point(9.45f, 21f), point(14.55f, 21f));
                        path.AddBezier(
                            point(14.55f, 21f),
                            point(17.58f, 21f),
                            point(20.06f, 19.14f),
                            point(18.03f, 15.58f));
                        path.AddLine(point(18.03f, 15.58f), point(14f, 8.5f));
                        path.AddLine(point(14f, 8.5f), point(14f, 2f));
                        graphics.DrawPath(pen, path);
                    }
                    graphics.DrawLine(pen, point(7f, 16f), point(17f, 16f));
                }
                else if (Glyph == ButtonGlyph.Trash)
                {
                    graphics.DrawLine(pen, point(3f, 6f), point(21f, 6f));
                    graphics.DrawLine(pen, point(8f, 6f), point(8f, 4f));
                    graphics.DrawBezier(pen, point(8f, 4f), point(8f, 2.9f), point(8.9f, 2f), point(10f, 2f));
                    graphics.DrawLine(pen, point(10f, 2f), point(14f, 2f));
                    graphics.DrawBezier(pen, point(14f, 2f), point(15.1f, 2f), point(16f, 2.9f), point(16f, 4f));
                    graphics.DrawLine(pen, point(16f, 4f), point(16f, 6f));
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(point(19f, 6f), point(18f, 20f));
                        path.AddBezier(point(18f, 20f), point(17.92f, 21.1f), point(17.1f, 22f), point(16f, 22f));
                        path.AddLine(point(16f, 22f), point(8f, 22f));
                        path.AddBezier(point(8f, 22f), point(6.9f, 22f), point(6.08f, 21.1f), point(6f, 20f));
                        path.AddLine(point(6f, 20f), point(5f, 6f));
                        graphics.DrawPath(pen, path);
                    }
                    graphics.DrawLine(pen, point(10f, 11f), point(10f, 17f));
                    graphics.DrawLine(pen, point(14f, 11f), point(14f, 17f));
                }
                else if (Glyph == ButtonGlyph.Play)
                {
                    using (var brush = new SolidBrush(ForeColor))
                    using (var path = new GraphicsPath())
                    {
                        path.AddPolygon(new[] { point(5f, 3f), point(21f, 12f), point(5f, 21f) });
                        graphics.FillPath(brush, path);
                    }
                }
                else if (Glyph == ButtonGlyph.Stop)
                {
                    Rectangle stopRect = Rectangle.Round(new RectangleF(
                        point(5f, 5f).X,
                        point(5f, 5f).Y,
                        bounds.Width * 14f / 24f,
                        bounds.Height * 14f / 24f));
                    using (var brush = new SolidBrush(ForeColor))
                    using (GraphicsPath path = Shape.Rounded(stopRect, 2))
                    {
                        graphics.FillPath(brush, path);
                    }
                }
            }
        }

        private static Color Shift(Color color, int amount)
        {
            return Color.FromArgb(Clamp(color.R + amount), Clamp(color.G + amount), Clamp(color.B + amount));
        }

        private static int Clamp(int value)
        {
            return Math.Max(0, Math.Min(255, value));
        }
    }

    internal class RoundTextBox : UserControl
    {
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_GETLINECOUNT = 0x00BA;
        private const int EM_LINESCROLL = 0x00B6;
        private const int TextScrollBarInset = 17;
        private const int TextScrollThumbWidth = 7;
        private const int TextScrollThumbMinimumHeight = 32;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly TextBox _box;
        private bool _placeholderActive;
        private string _suffixText;
        private bool _textScrollThumbHot;
        private bool _textScrollDragging;
        private int _textScrollDragStartY;
        private int _textScrollDragStartLine;

        public event EventHandler InnerTextChanged;

        public string Placeholder { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public Color FocusBorderColor { get; set; }
        public Color SuffixColor { get; set; }
        public int Radius { get; set; }

        public string SuffixText
        {
            get { return _suffixText; }
            set
            {
                _suffixText = value ?? "";
                LayoutInner();
                Invalidate();
            }
        }

        public bool Multiline
        {
            get { return _box.Multiline; }
            set
            {
                _box.Multiline = value;
                _box.ScrollBars = ScrollBars.None;
                LayoutInner();
                QueueTextScrollRefresh();
            }
        }

        public override string Text
        {
            get { return _placeholderActive ? "" : _box.Text; }
            set
            {
                _placeholderActive = false;
                _box.ForeColor = Color.FromArgb(9, 17, 39);
                _box.Text = value;
                ApplyPlaceholder();
            }
        }

        public void SetTextPreserveView(string value)
        {
            value = value ?? "";
            bool hadFocus = _box.Focused;
            int firstVisibleLine = 0;
            if (_box.Multiline && _box.IsHandleCreated)
            {
                firstVisibleLine = SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }

            int selectionStart = Math.Max(0, Math.Min(_box.SelectionStart, value.Length));
            int selectionLength = Math.Max(0, Math.Min(_box.SelectionLength, value.Length - selectionStart));
            _placeholderActive = false;
            _box.ForeColor = Color.FromArgb(9, 17, 39);
            _box.Text = value;
            _box.SelectionStart = selectionStart;
            _box.SelectionLength = selectionLength;

            if (_box.Multiline && _box.IsHandleCreated)
            {
                int currentFirstLine = SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int delta = firstVisibleLine - currentFirstLine;
                if (delta != 0)
                {
                    SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(delta));
                }
            }

            if (hadFocus)
            {
                _box.Focus();
            }
            ApplyPlaceholder();
            QueueTextScrollRefresh();
        }

        public void SetTextScrollToBottom(string value)
        {
            value = value ?? "";
            bool hadFocus = _box.Focused;
            _placeholderActive = false;
            _box.ForeColor = Color.FromArgb(9, 17, 39);
            _box.Text = value;
            if (_box.Multiline)
            {
                _box.SelectionStart = _box.TextLength;
                _box.SelectionLength = 0;
                _box.ScrollToCaret();
            }
            else
            {
                _box.SelectionStart = Math.Min(_box.TextLength, value.Length);
                _box.SelectionLength = 0;
            }
            if (hadFocus)
            {
                _box.Focus();
            }
            ApplyPlaceholder();
            QueueTextScrollRefresh();
        }

        public void AppendTextPreserveView(string value)
        {
            AppendTextCore(value, false);
        }

        public void AppendTextScrollToBottom(string value)
        {
            AppendTextCore(value, true);
        }

        private void AppendTextCore(string value, bool scrollToBottom)
        {
            value = value ?? "";
            if (value.Length == 0)
            {
                return;
            }

            bool hadFocus = _box.Focused;
            int firstVisibleLine = 0;
            if (_box.Multiline && _box.IsHandleCreated)
            {
                firstVisibleLine = SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }

            int selectionStart = _placeholderActive ? 0 : _box.SelectionStart;
            int selectionLength = _placeholderActive ? 0 : _box.SelectionLength;
            if (_placeholderActive)
            {
                _box.Text = "";
                _placeholderActive = false;
            }

            _box.ForeColor = Color.FromArgb(9, 17, 39);
            _box.AppendText(value);
            if (scrollToBottom)
            {
                _box.SelectionStart = _box.TextLength;
                _box.SelectionLength = 0;
                if (_box.Multiline)
                {
                    _box.ScrollToCaret();
                }
            }
            else
            {
                selectionStart = Math.Max(0, Math.Min(selectionStart, _box.TextLength));
                selectionLength = Math.Max(0, Math.Min(selectionLength, _box.TextLength - selectionStart));
                _box.SelectionStart = selectionStart;
                _box.SelectionLength = selectionLength;
                if (_box.Multiline && _box.IsHandleCreated)
                {
                    int currentFirstLine = SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    int delta = firstVisibleLine - currentFirstLine;
                    if (delta != 0)
                    {
                        SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(delta));
                    }
                }
            }

            if (hadFocus)
            {
                _box.Focus();
            }
            ApplyPlaceholder();
            QueueTextScrollRefresh();
        }

        public RoundTextBox() : this(9.5f)
        {
        }

        public RoundTextBox(float textSize)
        {
            Placeholder = "";
            _suffixText = "";
            FillColor = Color.FromArgb(250, 247, 255);
            BorderColor = Color.FromArgb(219, 201, 240);
            FocusBorderColor = Color.FromArgb(157, 105, 223);
            SuffixColor = Color.FromArgb(105, 91, 137);
            Radius = 10;
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = FillColor;
            _box.ForeColor = Color.FromArgb(9, 17, 39);
            _box.Font = UiFont.Make(textSize, FontStyle.Regular);
            _box.TextChanged += delegate
            {
                if (!_placeholderActive && InnerTextChanged != null)
                {
                    InnerTextChanged(this, EventArgs.Empty);
                }
                QueueTextScrollRefresh();
            };
            _box.GotFocus += delegate
            {
                if (_placeholderActive)
                {
                    _box.Text = "";
                    _box.ForeColor = Color.FromArgb(9, 17, 39);
                    _placeholderActive = false;
                }
                Invalidate();
            };
            _box.LostFocus += delegate
            {
                ApplyPlaceholder();
                Invalidate();
            };
            _box.MouseWheel += delegate(object sender, MouseEventArgs e)
            {
                int lines = SystemInformation.MouseWheelScrollLines;
                int step = lines < 0 ? Math.Max(1, GetVisibleLineCount() - 1) : Math.Max(1, lines);
                ScrollTextToLine(GetFirstVisibleLine() - (Math.Sign(e.Delta) * step));
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null)
                {
                    handled.Handled = true;
                }
                QueueTextScrollRefresh();
            };
            _box.KeyUp += delegate { QueueTextScrollRefresh(); };
            _box.MouseUp += delegate { QueueTextScrollRefresh(); };
            Controls.Add(_box);

            Resize += delegate { LayoutInner(); };
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            ApplyPlaceholder();
            LayoutInner();
        }

        private void ApplyPlaceholder()
        {
            if (!Focused && !_box.Focused && _box.Text.Length == 0 && Placeholder.Length > 0)
            {
                _placeholderActive = true;
                _box.ForeColor = Color.FromArgb(105, 91, 137);
                _box.Text = Placeholder;
            }
        }

        private void LayoutInner()
        {
            int padX = 13;
            int padY = _box.Multiline ? 11 : Math.Max(6, (Height - _box.Font.Height) / 2);
            int suffixReserve = GetSuffixReserve();
            int scrollReserve = _box.Multiline ? TextScrollBarInset : 0;
            _box.BackColor = FillColor;
            _box.SetBounds(padX, padY, Math.Max(1, Width - (padX * 2) - suffixReserve - scrollReserve), Math.Max(1, Height - (padY * 2)));
            QueueTextScrollRefresh();
        }

        private int GetSuffixReserve()
        {
            if (String.IsNullOrEmpty(_suffixText))
            {
                return 0;
            }

            return Math.Max(24, TextRenderer.MeasureText(_suffixText, _box.Font).Width + 12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shape.Rounded(rect, Radius))
            using (SolidBrush brush = new SolidBrush(FillColor))
            using (Pen pen = new Pen(_box.Focused ? FocusBorderColor : BorderColor))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }

            if (!String.IsNullOrEmpty(_suffixText))
            {
                int padX = 13;
                int suffixReserve = GetSuffixReserve();
                Rectangle suffixRect = new Rectangle(Width - padX - suffixReserve, 0, suffixReserve, Height - 1);
                TextRenderer.DrawText(e.Graphics, _suffixText, _box.Font, suffixRect, SuffixColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            DrawTextScrollBar(e.Graphics);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HasTextScrollBar && e.X >= Width - TextScrollBarInset)
            {
                Rectangle thumb = GetTextScrollThumbBounds();
                if (thumb.Contains(e.Location))
                {
                    _textScrollDragging = true;
                    _textScrollDragStartY = e.Y;
                    _textScrollDragStartLine = GetFirstVisibleLine();
                    Capture = true;
                }
                else
                {
                    int page = Math.Max(1, GetVisibleLineCount() - 1);
                    ScrollTextToLine(GetFirstVisibleLine() + (e.Y < thumb.Top ? -page : page));
                }

                _box.Focus();
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_textScrollDragging)
            {
                Rectangle track = GetTextScrollTrackBounds();
                Rectangle thumb = GetTextScrollThumbBounds();
                int travel = Math.Max(1, track.Height - thumb.Height);
                int line = _textScrollDragStartLine + ((e.Y - _textScrollDragStartY) * GetMaximumFirstVisibleLine() / travel);
                ScrollTextToLine(line);
            }

            bool hot = HasTextScrollBar && GetTextScrollThumbBounds().Contains(e.Location);
            if (_textScrollThumbHot != hot)
            {
                _textScrollThumbHot = hot;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_textScrollDragging)
            {
                _textScrollDragging = false;
                Capture = false;
                Invalidate();
            }

            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!_textScrollDragging && _textScrollThumbHot)
            {
                _textScrollThumbHot = false;
                Invalidate();
            }

            base.OnMouseLeave(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture && _textScrollDragging)
            {
                _textScrollDragging = false;
                Invalidate();
            }

            base.OnMouseCaptureChanged(e);
        }

        private bool HasTextScrollBar
        {
            get { return _box.Multiline && GetMaximumFirstVisibleLine() > 0; }
        }

        private int GetFirstVisibleLine()
        {
            if (!_box.IsHandleCreated)
            {
                return 0;
            }

            return Math.Max(0, SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32());
        }

        private int GetTextLineCount()
        {
            if (!_box.IsHandleCreated)
            {
                return Math.Max(1, (_box.Text ?? "").Split('\n').Length);
            }

            return Math.Max(1, SendMessage(_box.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32());
        }

        private int GetVisibleLineCount()
        {
            return Math.Max(1, _box.ClientSize.Height / Math.Max(1, _box.Font.Height));
        }

        private int GetMaximumFirstVisibleLine()
        {
            return Math.Max(0, GetTextLineCount() - GetVisibleLineCount());
        }

        private void ScrollTextToLine(int line)
        {
            if (!_box.IsHandleCreated)
            {
                return;
            }

            int target = Math.Max(0, Math.Min(GetMaximumFirstVisibleLine(), line));
            int current = GetFirstVisibleLine();
            if (target != current)
            {
                SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(target - current));
            }
            Invalidate();
        }

        private Rectangle GetTextScrollTrackBounds()
        {
            int top = 10;
            return new Rectangle(Width - TextScrollThumbWidth - 5, top, TextScrollThumbWidth, Math.Max(1, Height - (top * 2)));
        }

        private Rectangle GetTextScrollThumbBounds()
        {
            if (!HasTextScrollBar)
            {
                return Rectangle.Empty;
            }

            Rectangle track = GetTextScrollTrackBounds();
            int lineCount = GetTextLineCount();
            int visibleLines = GetVisibleLineCount();
            int thumbHeight = Math.Min(
                track.Height,
                Math.Max(TextScrollThumbMinimumHeight, (int)Math.Round(track.Height * ((double)visibleLines / Math.Max(1, lineCount)))));
            int travel = Math.Max(0, track.Height - thumbHeight);
            int maximum = GetMaximumFirstVisibleLine();
            int top = track.Top;
            if (maximum > 0)
            {
                top += (int)Math.Round(travel * ((double)Math.Min(maximum, GetFirstVisibleLine()) / maximum));
            }

            return new Rectangle(track.X, top, TextScrollThumbWidth, thumbHeight);
        }

        private void DrawTextScrollBar(Graphics graphics)
        {
            if (!HasTextScrollBar)
            {
                return;
            }

            Rectangle thumb = GetTextScrollThumbBounds();
            Color color = _textScrollDragging
                ? Color.FromArgb(126, 137, 154)
                : (_textScrollThumbHot ? Color.FromArgb(159, 169, 184) : Color.FromArgb(184, 190, 200));
            using (GraphicsPath path = Shape.Rounded(thumb, 3))
            using (var brush = new SolidBrush(color))
            {
                graphics.FillPath(brush, path);
            }
        }

        private void QueueTextScrollRefresh()
        {
            Invalidate();
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            BeginInvoke((MethodInvoker)delegate
            {
                if (!IsDisposed)
                {
                    Invalidate();
                }
            });
        }
    }

    internal sealed class NumberBox : RoundTextBox
    {
        public event EventHandler ValueChanged;

        public int Minimum { get; set; }
        public int Maximum { get; set; }

        public int Value
        {
            get
            {
                int value;
                if (!Int32.TryParse(Text, out value))
                {
                    value = Minimum;
                }
                return Math.Max(Minimum, Math.Min(Maximum, value));
            }
            set
            {
                Text = Math.Max(Minimum, Math.Min(Maximum, value)).ToString();
            }
        }

        public NumberBox()
        {
            Minimum = 0;
            Maximum = 30000;
            InnerTextChanged += delegate
            {
                string cleaned = Regex.Replace(Text, "[^0-9]", "");
                if (cleaned != Text)
                {
                    Text = cleaned;
                }
                if (ValueChanged != null)
                {
                    ValueChanged(this, EventArgs.Empty);
                }
            };
        }
    }

    internal static class Shape
    {
        public static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return path;
            }

            int safeRadius = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
            int diameter = Math.Max(1, safeRadius * 2);
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PendingGift
    {
        public GiftSource Source { get; set; }
        public string Nickname { get; set; }
        public int BalloonCount { get; set; }
        public int CoinCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    internal sealed class CollectedEntry
    {
        public GiftSource Source { get; set; }
        public string Nickname { get; set; }
        public int BalloonCount { get; set; }
        public int CoinCount { get; set; }
        public string PinballName { get; set; }
        public string ReceivedAt { get; set; }
        // 코인을 직접 고친 줄은 기준 개수나 보너스를 바꿔도 다시 계산하지 않는다.
        public bool CoinEdited { get; set; }
        public DateTime AddedAt { get; set; }
    }

    internal enum GiftSource
    {
        StarBalloon,
        AdBalloon,
        ChallengeGift,
        BattleMission
    }

    // 방송 정보 조회에서 방송 중이 아니라고 나온 경우. Result는 SOOP이 돌려준 상태 번호다.
    internal sealed class SoopNotLiveException : InvalidOperationException
    {
        // 19세 방송: 로그인해야 들어갈 수 있어 손님으로는 수집할 수 없다.
        public const int AdultOnly = -6;

        public int Result { get; private set; }

        public SoopNotLiveException(int result, string message) : base(message)
        {
            Result = result;
        }
    }

    internal static class GiftSourceInfo
    {
        public static string GetLabel(GiftSource source)
        {
            if (source == GiftSource.AdBalloon)
            {
                return "애드벌룬";
            }

            if (source == GiftSource.ChallengeGift)
            {
                return "도전미션";
            }

            if (source == GiftSource.BattleMission)
            {
                return "대결미션";
            }

            return "별풍선";
        }
    }
}
