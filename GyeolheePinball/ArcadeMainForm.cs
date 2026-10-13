using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GyeolheePinball
{
    // 메인 화면. 모양과 배치는 종겜핀볼 Web의 결희 판("오락실 도트", 밝은 보라)을 따른다.
    // 좌표는 웹 시안의 2560×1440 기준 값이고, 창 크기에 맞춰 같은 비율로 줄이거나 늘린다.
    // 창이 기준보다 길면 남는 높이는 명단에, 넓으면 남는 폭은 명단의 내용 칸에 준다.
    internal sealed class MainForm : Form, IMessageFilter
    {
        private const string PinballUrl = "https://gyeon-ai.github.io/GyeolheePinball-Web/";
        private const int DirectPinballUrlLimit = 1800;
        private const float BaseWidth = 2560f;
        private const float BaseHeight = 1440f;
        private const float RowHeight = 92f;
        private const float HeaderHeight = 169f;
        private const float TokenLineHeight = 40.8f;
        private const int PendingMinutes = 10;
        private const int MemoDefaultX = 764;
        private const int MemoDefaultY = 336;
        private const int MemoDefaultWidth = 880;
        private const int MemoDefaultHeight = 600;
        private const int MemoDefaultFont = 42;

        private enum ConnectionState
        {
            Off,
            Connecting,
            On
        }

        // 업데이트 안내창 등 기존 보조 창이 쓰는 색. 바꾸지 않는다.
        private readonly Color _text = Color.FromArgb(10, 18, 34);
        private readonly Color _muted = Color.FromArgb(68, 83, 111);
        private readonly Color _purple = Color.FromArgb(37, 99, 235);
        private readonly Color _line = Color.FromArgb(180, 205, 242);

        private ArcadeSurface _stage;
        private ArcadeTextBox _streamInput;
        private ArcadeNumberBox _thresholdInput;
        private ArcadeTextBox _searchInput;
        private ArcadeTextBox _rowEditor;
        private ArcadeSurface _waitPopup;
        private ArcadeSurface _addPopup;
        private ArcadeSurface _kindPopup;
        private ArcadeSurface _memoPopup;
        private ArcadeSurface _confirmPopup;
        private ArcadeTextBox _addNameInput;
        private ArcadeTextBox _addNickInput;
        private ArcadeNumberBox _addCountInput;
        private ArcadeTextBox _memoText;
        private Image _iconImage;

        private readonly List<CollectedEntry> _entries = new List<CollectedEntry>();
        private readonly List<PendingGift> _pending = new List<PendingGift>();
        private readonly List<CollectedEntry> _visibleEntries = new List<CollectedEntry>();
        private readonly List<KeyValuePair<PendingGift, DateTime>> _expiredGifts = new List<KeyValuePair<PendingGift, DateTime>>();
        private readonly StringBuilder _generatedPinballText = new StringBuilder();
        private readonly Dictionary<string, KeyValuePair<string, DateTime>> _labelFlashes = new Dictionary<string, KeyValuePair<string, DateTime>>();
        private readonly Dictionary<string, float> _tokenCharWidths = new Dictionary<string, float>();
        private readonly List<List<TokenRun>> _tokenLines = new List<List<TokenRun>>();
        private readonly System.Windows.Forms.Timer _connectTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _reconnectTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _animationTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _pendingTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _saveTimer = new System.Windows.Forms.Timer();

        private SoopLiveChatClient _chatClient;
        private string _streamerId = "";
        private string _streamerName = "";
        private bool _connected;
        private bool _connecting;
        private bool _manualDisconnect;
        private bool _joinedOnce;
        private bool _exactMode;
        private bool _nicknamePinballMode;
        private bool _bonusPlusOne = true;
        private bool _collectStarBalloon = true;
        private bool _collectAdBalloon = true;
        private bool _collectChallengeGift = true;
        private bool _collectBattleMission = true;
        private int _reconnectAttempt;
        private ConnectionState _connectionState = ConnectionState.Off;
        private string _statusText = "연결 안 됨";
        private string _statusTip = "";
        private int _blinkPhase;

        private long _generatedPinballCoinTotal;
        private string _visibleEntriesSearchText = "";
        private float _listScroll;
        private CollectedEntry _editEntry;
        private bool _editCoin;
        private bool _updatingRowEditor;
        private DateTime _flashUntil = DateTime.MinValue;

        private bool _tokensDirty = true;
        private float _tokenLayoutWidth = -1f;
        private float _tokenScroll;
        private bool _tokenStickToBottom = true;

        private float _waitScroll;
        private bool _addOpenAtMouseDown;
        private bool _waitOpenAtMouseDown;
        private GiftSource _addKind = GiftSource.StarBalloon;

        private bool _memoOpen;
        private int _memoX = MemoDefaultX;
        private int _memoY = MemoDefaultY;
        private int _memoWidth = MemoDefaultWidth;
        private int _memoHeight = MemoDefaultHeight;
        private int _memoFont = MemoDefaultFont;
        private int _memoDragX;
        private int _memoDragY;

        private string _statePath;
        private bool _loadingState;

        // 화면 배치(2560 기준 좌표). LayoutUi가 채운다.
        private float _lw = BaseWidth;
        private float _lh = BaseHeight;
        private float _cx;
        private float _cy;
        private float _cw;
        private float _ch;
        private float _listY;
        private float _listH;
        private float _rowsY;
        private float _rowsH;
        private float _nameX;
        private float _nameW;
        private float _nickX;
        private float _srcX;
        private float _coinX;
        private float _delX;
        private float _rx;
        private float _tokY;
        private float _tokH;
        private float _bigY;
        private float _smallY;
        private float _searchX;
        private float _addButtonX;
        private float _addButtonW;
        private float _clearButtonX;
        private float _clearButtonW;
        private float _waitBoxRight;

        private sealed class TokenRun
        {
            public string Text;
            public Color Color;
            public float X;
        }

        public MainForm()
        {
            Text = "결희 종겜핀볼";
            AutoScaleMode = AutoScaleMode.None;
            float dpiScale = Math.Max(1f, DeviceDpi / 96f);
            ClientSize = new Size((int)Math.Round(1280 * dpiScale), (int)Math.Round(800 * dpiScale));
            // 갈무리용: --client=폭x높이
            Match clientArgument = Regex.Match(GetArgumentValue("--client="), "^(\\d+)x(\\d+)$");
            if (clientArgument.Success)
            {
                ClientSize = new Size(Int32.Parse(clientArgument.Groups[1].Value), Int32.Parse(clientArgument.Groups[2].Value));
            }
            MinimumSize = new Size(960, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = UiFont.Make(9.5f, FontStyle.Regular);
            BackColor = ArcadeColors.Background;
            DoubleBuffered = true;
            Icon = LoadEmbeddedIcon("GyeolheePinballIcon");
            _iconImage = LoadEmbeddedImage("GyeolheePinballArcadeIcon");

            BuildUi();
            WireEvents();
            _statePath = ResolveStatePath();
            LoadState();

            int previewCount = GetArgumentInt("--preview-count=", 0, 1000);
            if (previewCount > 0)
            {
                SeedManyPreviewRows(previewCount);
            }
            else if (HasArgument("--preview-many"))
            {
                SeedManyPreviewRows(12);
            }
            else if (HasArgument("--preview"))
            {
                SeedPreviewRows();
            }
            string searchPreview = GetArgumentValue("--search-preview=");
            if (searchPreview.Length > 0)
            {
                _searchInput.Text = searchPreview;
            }
            RefreshPinballText(false);
            RefreshCounts();
            LayoutUi();

            Resize += delegate { LayoutUi(); };
            Shown += delegate
            {
                LayoutUi();
                int stressCount = GetArgumentInt("--stress-count=", 0, 1000);
                if (stressCount > 0)
                {
                    AddStressEntries(stressCount);
                    return;
                }

                string livePreviewId = GetArgumentValue("--live-preview=");
                if (livePreviewId.Length > 0)
                {
                    _streamInput.Text = livePreviewId;
                    StartConnection();
                    return;
                }

                if (HasArgument("--manual-test-preview"))
                {
                    AddManualTestEntry();
                }
                if (HasArgument("--wait-preview"))
                {
                    SeedPendingPreview();
                    OpenWaitPopup();
                }
                if (HasArgument("--add-preview"))
                {
                    OpenAddPopup();
                }
                if (HasArgument("--clear-preview"))
                {
                    ClearEntries();
                }
                if (HasArgument("--update-preview"))
                {
                    BeginInvoke((MethodInvoker)ShowUpdatePreview);
                }
                if (HasArgument("--memo-preview") && !_memoOpen)
                {
                    ToggleMemo();
                }
            };
            FormClosing += delegate { SaveStateNow(); };
            FormClosed += delegate
            {
                Application.RemoveMessageFilter(this);
                CloseChatClient();
            };
            Application.AddMessageFilter(this);
        }

        // ───────── 화면 만들기 ─────────

        private void BuildUi()
        {
            _stage = new ArcadeSurface();
            _stage.Dock = DockStyle.Fill;
            _stage.Painter = DrawStage;
            Controls.Add(_stage);

            _streamInput = new ArcadeTextBox();
            _streamInput.Placeholder = "SOOP ID 또는 방송 주소";
            _stage.Controls.Add(_streamInput);

            _thresholdInput = new ArcadeNumberBox();
            _thresholdInput.TextAlign = HorizontalAlignment.Right;
            _stage.Controls.Add(_thresholdInput);

            _searchInput = new ArcadeTextBox();
            _stage.Controls.Add(_searchInput);

            _rowEditor = new ArcadeTextBox();
            _rowEditor.BackColor = ArcadeColors.EditFill;
            _rowEditor.Visible = false;
            _stage.Controls.Add(_rowEditor);

            _waitPopup = new ArcadeSurface();
            _waitPopup.Painter = DrawWaitPopup;
            _waitPopup.Visible = false;
            _stage.Controls.Add(_waitPopup);

            _addPopup = new ArcadeSurface();
            _addPopup.Painter = DrawAddPopup;
            _addPopup.Visible = false;
            _stage.Controls.Add(_addPopup);

            _addNameInput = new ArcadeTextBox();
            _addNameInput.Placeholder = "명단에 넣을 내용";
            _addPopup.Controls.Add(_addNameInput);
            _addNickInput = new ArcadeTextBox();
            _addPopup.Controls.Add(_addNickInput);
            _addCountInput = new ArcadeNumberBox();
            _addCountInput.TextAlign = HorizontalAlignment.Right;
            _addPopup.Controls.Add(_addCountInput);

            _kindPopup = new ArcadeSurface();
            _kindPopup.Painter = DrawKindPopup;
            _kindPopup.Visible = false;
            _stage.Controls.Add(_kindPopup);

            _memoPopup = new ArcadeSurface();
            _memoPopup.Painter = DrawMemoPopup;
            _memoPopup.BackColor = Color.White;
            _memoPopup.Visible = false;
            _stage.Controls.Add(_memoPopup);

            _confirmPopup = new ArcadeSurface();
            _confirmPopup.Painter = DrawConfirmPopup;
            _confirmPopup.Visible = false;
            _stage.Controls.Add(_confirmPopup);

            _memoText = new ArcadeTextBox();
            _memoText.Multiline = true;
            _memoText.AcceptsReturn = true;
            _memoText.ScrollBars = ScrollBars.Vertical;
            _memoText.BackColor = Color.White;
            _memoText.ForeColor = Color.FromArgb(0x17, 0x17, 0x1c);
            _memoPopup.Controls.Add(_memoText);
        }

        private void WireEvents()
        {
            _stage.MouseDown += delegate
            {
                // 판의 아무 곳이나 누르면 열려 있던 말풍선과 줄 고치기를 닫는다.
                _addOpenAtMouseDown = _addPopup.Visible;
                _waitOpenAtMouseDown = _waitPopup.Visible;
                CloseAddPopup();
                CloseWaitPopup();
                CloseConfirmPopup();
                EndRowEdit();
                _stage.Focus();
            };

            EventHandler closePopups = delegate
            {
                CloseAddPopup();
                CloseWaitPopup();
            };
            _streamInput.Enter += closePopups;
            _thresholdInput.Enter += closePopups;
            _searchInput.Enter += closePopups;
            _memoText.Enter += closePopups;

            _streamInput.TextChanged += delegate { QueueSave(); };
            _streamInput.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && _connectionState == ConnectionState.Off)
                {
                    e.SuppressKeyPress = true;
                    StartConnection();
                }
            };
            _thresholdInput.ValueChanged += delegate
            {
                RecalculateCoins();
                RefreshPinballText(false);
                RefreshCounts();
                QueueSave();
            };
            _searchInput.TextChanged += delegate { RefreshCounts(); };

            _rowEditor.TextChanged += delegate { ApplyRowEditorText(); };
            _rowEditor.Leave += delegate { EndRowEdit(); };
            _rowEditor.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    EndRowEdit();
                    _stage.Focus();
                }
            };
            _rowEditor.KeyPress += delegate(object sender, KeyPressEventArgs e)
            {
                if (_editCoin && !Char.IsControl(e.KeyChar) && !Char.IsDigit(e.KeyChar))
                {
                    e.Handled = true;
                }
            };

            KeyEventHandler addKeys = delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddFromPopup();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    CloseAddPopup();
                }
            };
            _addNameInput.KeyDown += addKeys;
            _addNickInput.KeyDown += addKeys;
            _addCountInput.KeyDown += addKeys;
            EventHandler closeKindList = delegate { _kindPopup.Visible = false; };
            _addNameInput.Enter += closeKindList;
            _addNickInput.Enter += closeKindList;
            _addCountInput.Enter += closeKindList;
            _addPopup.MouseDown += delegate { _kindPopup.Visible = false; };

            _memoText.TextChanged += delegate { QueueSave(); };

            _connectTimer.Interval = 800;
            _connectTimer.Tick += delegate { _connectTimer.Stop(); };

            _reconnectTimer.Tick += delegate
            {
                _reconnectTimer.Stop();
                if (!_manualDisconnect && !_connected)
                {
                    StartConnection(true);
                }
            };

            _animationTimer.Interval = 40;
            _animationTimer.Tick += delegate { AnimationTick(); };

            // 기다리는 후원의 남은 시간을 고쳐 그리고, 10분이 지난 것을 정리한다.
            _pendingTimer.Interval = 5000;
            _pendingTimer.Tick += delegate
            {
                int before = _pending.Count;
                PrunePendingGifts();
                if (_pending.Count == 0 && _expiredGifts.Count == 0)
                {
                    _pendingTimer.Stop();
                }
                if (before != _pending.Count)
                {
                    QueueSave();
                }
                InvalidatePending();
            };

            _saveTimer.Interval = 250;
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                SaveStateNow();
            };
        }

        private Image LoadEmbeddedImage(string resourceName)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return null;
            }

            using (stream)
            {
                return new Bitmap(stream);
            }
        }

        private Icon LoadEmbeddedIcon(string resourceName)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return null;
            }

            using (stream)
            using (var icon = new Icon(stream, 256, 256))
            {
                return (Icon)icon.Clone();
            }
        }

        // ───────── 배치 ─────────

        private void LayoutUi()
        {
            if (_stage == null)
            {
                return;
            }

            Size client = ClientSize;
            if (client.Width <= 0 || client.Height <= 0)
            {
                return;
            }

            float zoom = Math.Min(client.Width / BaseWidth, client.Height / BaseHeight);
            zoom = Math.Max(0.2f, zoom);
            if (Math.Abs(zoom - _stage.Zoom) > 0.0001f)
            {
                _tokenCharWidths.Clear();
                _tokensDirty = true;
            }
            _stage.Zoom = zoom;
            _waitPopup.Zoom = zoom;
            _addPopup.Zoom = zoom;
            _kindPopup.Zoom = zoom;
            _memoPopup.Zoom = zoom;
            _confirmPopup.Zoom = zoom;
            _lw = client.Width / zoom;
            _lh = client.Height / zoom;

            _cx = 525f;
            _cy = HeaderHeight + 6f + 33f;
            _cw = _lw - 1020f;
            _ch = _lh - 42f - _cy;
            _listY = _cy + 121f;
            _listH = _ch - 121f;
            _rowsY = _listY + 64f;
            _rowsH = Math.Max(0f, _listH - 64f);
            float contentRight = _cx + _cw - 22f - 33f;
            _delX = contentRight - 78f;
            _coinX = _delX - 15f - 148f;
            _srcX = _coinX - 15f - 216f;
            _nickX = _srcX - 15f - 172f;
            _nameX = _cx + 33f;
            _nameW = Math.Max(60f, _nickX - 15f - _nameX);

            _rx = _lw - 462f;
            _smallY = _lh - 42f - 8f - 78f;
            _bigY = _smallY - 24f - 12f - 126f;
            _tokY = _cy;
            _tokH = Math.Max(120f, _bigY - 24f - _tokY);

            _clearButtonW = _stage.TextWidth("모두 지우기", ArcadeFonts.Pixel, 26f, false) + 54f;
            _clearButtonX = _cx + _cw - 33f - _clearButtonW;
            _addButtonW = Math.Max(
                _stage.TextWidth("추가", ArcadeFonts.Pixel, 26f, false),
                _stage.TextWidth("닫기", ArcadeFonts.Pixel, 26f, false)) + 54f;
            _addButtonX = _clearButtonX - 15f - _addButtonW;
            _searchX = _addButtonX - 15f - 255f;
            _waitBoxRight = _lw - 42f;

            _streamInput.Place(_stage, 69f, _cy + 99f, 396f, 69f, 22f, ArcadeFonts.Body, 24f, false);
            _thresholdInput.Place(_stage, 69f, _cy + 388f, 108f, 66f, 14f, ArcadeFonts.Body, 24f, true);
            _searchInput.Place(_stage, _searchX, _cy + 24f, 255f, 66f, 22f, ArcadeFonts.Body, 24f, false);

            ClampListScroll();
            PlaceRowEditor();
            LayoutWaitPopup();
            LayoutAddPopup();
            LayoutMemoPopup();
            LayoutConfirmPopup();
            _stage.Invalidate();
        }

        private void ClampListScroll()
        {
            float max = Math.Max(0f, (_visibleEntries.Count * RowHeight) - _rowsH);
            _listScroll = Math.Max(0f, Math.Min(max, _listScroll));
        }

        // ───────── 메인 판 그리기 ─────────

        private void DrawStage(ArcadeSurface s, Graphics g)
        {
            s.Fill(g, ArcadeColors.Background, 0f, 0f, _lw, _lh);
            for (float y = 0f; y < _lh; y += 24f)
            {
                s.Fill(g, ArcadeColors.GridLine, 0f, y, _lw, 3f);
            }
            for (float x = 0f; x < _lw; x += 24f)
            {
                s.Fill(g, ArcadeColors.GridLine, x, 0f, 3f, _lh);
            }

            DrawHeader(s, g);
            DrawSetup(s, g);
            DrawList(s, g);
            DrawTokens(s, g);
            DrawActions(s, g);

            // 오른쪽 아래 표시
            Font creditFont = s.F(ArcadeFonts.Body, 21f, true);
            float creditW = s.TextWidth("© 견아", ArcadeFonts.Body, 21f, true);
            float creditX = _lw - 42f - creditW;
            float creditY = _lh - 8f - 30f;
            s.Write(g, "© 견아", creditFont, ArcadeColors.Soft, creditX, creditY, creditW + 4f, 30f, 0);
            s.Fill(g, ArcadeColors.Accent, creditX - 48f, creditY + 9f, 12f, 12f);
            s.Fill(g, ArcadeColors.Rose, creditX - 36f, creditY + 9f, 12f, 12f);
            s.Fill(g, ArcadeColors.Cyan, creditX - 24f, creditY + 9f, 12f, 12f);
        }

        private void DrawHeader(ArcadeSurface s, Graphics g)
        {
            s.Fill(g, ArcadeColors.Header, 0f, 0f, _lw, HeaderHeight);
            s.Fill(g, ArcadeColors.Ink, 0f, HeaderHeight, _lw, 6f);

            float iconY = (HeaderHeight - 90f) / 2f;
            s.Fill(g, ArcadeColors.Ink, 42f, iconY + 8f, 90f, 90f);
            s.Fill(g, Color.FromArgb(0xcd, 0xbc, 0xff), 42f, iconY, 90f, 90f);
            if (_iconImage != null)
            {
                InterpolationMode previous = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_iconImage, s.Rect(42f, iconY, 90f, 90f));
                g.InterpolationMode = previous;
            }

            Font titleFont = s.F(ArcadeFonts.Pixel, 60f, true);
            float titleW = s.TextWidth("결희 핀볼", ArcadeFonts.Pixel, 60f, true);
            float titleY = (HeaderHeight - 72f) / 2f;
            s.Write(g, "결희 핀볼", titleFont, ArcadeColors.Rose, 168f, titleY + 6f, titleW + 12f, 72f, 0);
            s.Write(g, "결희 핀볼", titleFont, ArcadeColors.Accent, 162f, titleY, titleW + 12f, 72f, 0);

            float memoX = 162f + titleW + 6f + 30f;
            s.Button(
                g, "memo", _memoOpen ? "메모장 닫기" : "메모장", s.F(ArcadeFonts.Pixel, 36f, false),
                Color.FromArgb(0x3d, 0x34, 0x86), ArcadeColors.Text, ArcadeColors.Ink, 8f,
                memoX, (HeaderHeight - 88f) / 2f,
                Math.Max(228f, s.TextWidth("메모장 닫기", ArcadeFonts.Pixel, 36f, false) + 40f), 80f, ToggleMemo);

            // 점수판 3개(오른쪽부터 채운다)
            long coinTotal = 0;
            long giftTotal = 0;
            foreach (CollectedEntry entry in _entries)
            {
                if (entry != null)
                {
                    coinTotal += entry.CoinCount;
                    giftTotal += entry.BalloonCount;
                }
            }

            string waitText = _pending.Count.ToString("#,0");
            float waitLabelW = s.TextWidth("수집 대기 중", ArcadeFonts.Body, 24f, true) + 12f + 16f;
            float waitW = Math.Max(165f, Math.Max(waitLabelW, s.TextWidth(waitText, ArcadeFonts.Digits, 32f, false))) + 48f;
            float waitX = _lw - 42f - waitW;
            _waitBoxRight = waitX + waitW;
            DrawScoreBox(s, g, waitX, waitW, "수집 대기 중", waitText, 32f, ArcadeColors.Pink, ArcadeColors.Rose, true);
            s.Zone("wait", waitX, 24f, waitW, 121f, delegate
            {
                if (!_waitOpenAtMouseDown)
                {
                    OpenWaitPopup();
                }
            });

            string giftText = giftTotal.ToString("#,0");
            float giftW = Math.Max(225f, s.TextWidth(giftText, ArcadeFonts.Digits, 32f, false)) + 48f;
            float giftX = waitX - 18f - giftW;
            DrawScoreBox(s, g, giftX, giftW, "후원 개수", giftText, 32f, ArcadeColors.Cyan, ArcadeColors.Cyan, false);

            string coinText = coinTotal.ToString("#,0");
            float coinW = Math.Max(285f, s.TextWidth(coinText, ArcadeFonts.Digits, 48f, false)) + 48f;
            float coinX = giftX - 18f - coinW;
            DrawScoreBox(s, g, coinX, coinW, "핀볼 개수", coinText, 48f, ArcadeColors.Accent, ArcadeColors.Accent, false);
        }

        private void DrawScoreBox(ArcadeSurface s, Graphics g, float x, float width, string label, string value, float digitSize, Color color, Color border, bool arrow)
        {
            Color fill = arrow && s.IsHot("wait") ? ArcadeColors.Brighten(Color.FromArgb(40, 36, 80), 1.12f) : ArcadeColors.Ink;
            s.Fill(g, fill, x, 24f, width, 121f);
            s.DotBorder(g, border, x, 24f, width, 121f, 6f);
            s.Write(g, label, s.F(ArcadeFonts.Body, 24f, true), color, x + 24f, 36f, width - 48f, 35f, 0);
            if (arrow)
            {
                float labelW = s.TextWidth(label, ArcadeFonts.Body, 24f, true);
                s.TriangleDown(g, color, x + 24f + labelW + 12f, 49f, 16f, 10f);
            }
            s.Write(g, value, s.F(ArcadeFonts.Digits, digitSize, false), color, x + 24f, 71f, width - 30f, 60f, 0);
        }

        private void DrawPanel(ArcadeSurface s, Graphics g, float x, float y, float width, float height)
        {
            s.Fill(g, ArcadeColors.Panel, x, y, width, height);
            s.DotBorder(g, ArcadeColors.Border, x, y, width, height, 6f);
        }

        private void DrawInputFrame(ArcadeSurface s, Graphics g, float x, float y, float width, float height, float border)
        {
            s.Fill(g, ArcadeColors.Background, x, y, width, height);
            s.Frame(g, ArcadeColors.Line, x, y, width, height, border);
        }

        private void DrawSetup(ArcadeSurface s, Graphics g)
        {
            Font body = s.F(ArcadeFonts.Body, 24f, false);
            Font bodyBold = s.F(ArcadeFonts.Body, 24f, true);
            float top = _cy;

            // 방송 연결
            DrawPanel(s, g, 42f, top, 450f, 293f);
            s.Write(g, "SOOP ID", s.F(ArcadeFonts.Pixel, 30f, true), ArcadeColors.Text, 69f, top + 24f, 180f, 54f, 0);

            Color statusColor = _connectionState == ConnectionState.On ? ArcadeColors.Green
                : _connectionState == ConnectionState.Connecting ? Color.FromArgb(0xff, 0xcd, 0x75)
                : Color.FromArgb(0x94, 0xb0, 0xc2);
            Font statusFont = s.F(ArcadeFonts.Pixel, 24f, false);
            float statusTextW = Math.Min(190f, s.TextWidth(_statusText, ArcadeFonts.Pixel, 24f, false) + 2f);
            float chipW = 18f + 15f + 12f + statusTextW + 18f;
            float chipX = 465f - chipW;
            s.Fill(g, ArcadeColors.Ink, chipX, top + 24f, chipW, 54f);
            s.DotBorder(g, statusColor, chipX, top + 24f, chipW, 54f, 6f);
            if (_connectionState != ConnectionState.Connecting || _blinkPhase == 0)
            {
                s.Swatch(g, statusColor, chipX + 18f, top + 24f, 54f, 15f, 0f);
            }
            s.Write(g, _statusText, statusFont, statusColor, chipX + 45f, top + 24f, statusTextW, 54f, 0);
            ArcadeZone statusZone = s.Zone("status", chipX, top + 24f, chipW, 54f, null);
            statusZone.Cursor = Cursors.Default;
            statusZone.Tip = _statusTip;

            DrawInputFrame(s, g, 69f, top + 99f, 396f, 69f, 4f);
            string connectText = _connectionState == ConnectionState.Off ? "수집 시작"
                : _connectionState == ConnectionState.Connecting ? "연결 취소"
                : "수집 중단";
            s.Button(
                g, "connect", connectText, s.F(ArcadeFonts.Pixel, 32f, false),
                ArcadeColors.Rose, ArcadeColors.Text, Color.FromArgb(0x5d, 0x4b, 0xb0), 8f,
                69f, top + 183f, 396f, 72f, ToggleConnection);

            // 수집 설정
            float panelY = top + 317f;
            DrawPanel(s, g, 42f, panelY, 450f, _lh - 42f - panelY);

            float y = panelY + 24f;
            s.Write(g, "후원 개수", bodyBold, ArcadeColors.Soft, 69f, y, 396f, 35f, 0);
            y += 47f;
            DrawInputFrame(s, g, 69f, y, 108f, 66f, 2f);
            s.Write(g, "개", body, ArcadeColors.Text, 189f, y, 30f, 66f, 0);
            float exactW = 26f + 8f + s.TextWidth("정확히", ArcadeFonts.Body, 24f, false);
            float atLeastW = 26f + 8f + s.TextWidth("이상", ArcadeFonts.Body, 24f, false);
            float exactX = 189f + s.TextWidth("개", ArcadeFonts.Body, 24f, false) + 21f;
            float atLeastX = exactX + exactW + 12f;
            s.RadioButton(g, exactX, y, 66f, _exactMode);
            s.Write(g, "정확히", body, ArcadeColors.Text, exactX + 34f, y, exactW, 66f, 0);
            s.Zone("cmp-exact", exactX, y, exactW, 66f, delegate { SetExactMode(true); });
            s.RadioButton(g, atLeastX, y, 66f, !_exactMode);
            s.Write(g, "이상", body, ArcadeColors.Text, atLeastX + 34f, y, atLeastW, 66f, 0);
            s.Zone("cmp-min", atLeastX, y, atLeastW, 66f, delegate { SetExactMode(false); });

            y += 66f + 27f;
            s.Fill(g, ArcadeColors.Header, 69f, y, 396f, 66f);
            s.CheckBox(g, 83f, y, 66f, _bonusPlusOne);
            float bonusW = s.TextWidth("보너스 +1", ArcadeFonts.Body, 24f, true);
            s.Write(g, "보너스 +1", bodyBold, ArcadeColors.Text, 119f, y, bonusW + 4f, 66f, 0);
            s.Write(g, "10개마다 1개 더", body, ArcadeColors.Soft, 119f + bonusW + 10f, y, 465f - (119f + bonusW + 10f), 66f, 0);
            s.Zone("bonus", 69f, y, 396f, 66f, delegate { SetBonus(!_bonusPlusOne); });

            y += 66f + 27f;
            s.Write(g, "수집 대상", bodyBold, ArcadeColors.Soft, 69f, y, 396f, 35f, 0);
            y += 47f;
            DrawKindCell(s, g, 69f, y, GiftSource.StarBalloon, _collectStarBalloon);
            DrawKindCell(s, g, 273f, y, GiftSource.AdBalloon, _collectAdBalloon);
            DrawKindCell(s, g, 69f, y + 76f, GiftSource.ChallengeGift, _collectChallengeGift);
            DrawKindCell(s, g, 273f, y + 76f, GiftSource.BattleMission, _collectBattleMission);

            y += 140f + 27f;
            s.Write(g, "핀볼 반영", bodyBold, ArcadeColors.Soft, 69f, y, 396f, 35f, 0);
            y += 47f;
            DrawModeCell(s, g, 69f, y, "닉네임", _nicknamePinballMode, true);
            DrawModeCell(s, g, 273f, y, "채팅 내용", !_nicknamePinballMode, false);
        }

        private void DrawKindCell(ArcadeSurface s, Graphics g, float x, float y, GiftSource source, bool isChecked)
        {
            string key = "kind-" + (int)source;
            if (s.IsHot(key))
            {
                s.Fill(g, ArcadeColors.Blend(ArcadeColors.Panel, Color.FromArgb(127, 127, 127), 0.3f), x, y, 192f, 64f);
            }
            s.Frame(g, ArcadeColors.Border, x, y, 192f, 64f, 2f);
            s.CheckBox(g, x + 10f, y, 64f, isChecked);
            s.Swatch(g, ArcadeColors.ForSource(source), x + 44f, y, 64f, 18f, 0f);
            s.Write(g, GiftSourceInfo.GetLabel(source), s.F(ArcadeFonts.Body, 24f, false), ArcadeColors.Text, x + 70f, y, 118f, 64f, 0);
            s.Zone(key, x, y, 192f, 64f, delegate { ToggleGiftSource(source); });
        }

        private void DrawModeCell(ArcadeSurface s, Graphics g, float x, float y, string label, bool isChecked, bool nickname)
        {
            string key = nickname ? "mode-nick" : "mode-chat";
            if (s.IsHot(key))
            {
                s.Fill(g, ArcadeColors.Blend(ArcadeColors.Panel, Color.FromArgb(127, 127, 127), 0.3f), x, y, 192f, 64f);
            }
            s.Frame(g, ArcadeColors.Border, x, y, 192f, 64f, 2f);
            s.RadioButton(g, x + 10f, y, 64f, isChecked);
            s.Write(g, label, s.F(ArcadeFonts.Body, 24f, false), ArcadeColors.Text, x + 44f, y, 144f, 64f, 0);
            s.Zone(key, x, y, 192f, 64f, delegate { SetNicknameMode(nickname); });
        }

        private void DrawList(ArcadeSurface s, Graphics g)
        {
            DrawPanel(s, g, _cx, _cy, _cw, _ch);

            Font titleFont = s.F(ArcadeFonts.Pixel, 42f, false);
            float titleW = s.TextWidth("핀볼 내용", ArcadeFonts.Pixel, 42f, false);
            s.Write(g, "핀볼 내용", titleFont, ArcadeColors.Text, _cx + 33f, _cy + 21f, titleW + 6f, 72f, 0);
            float peopleX = _cx + 33f + titleW + 14f;
            float searchLabelW = s.TextWidth("검색", ArcadeFonts.Pixel, 26f, false);
            float searchLabelX = _searchX - 15f - searchLabelW;
            s.Write(g, _entries.Count.ToString("#,0") + "명", s.F(ArcadeFonts.Body, 24f, false), ArcadeColors.Soft, peopleX, _cy + 31f, Math.Max(0f, searchLabelX - 12f - peopleX), 62f, 0);

            s.Write(g, "검색", s.F(ArcadeFonts.Pixel, 26f, false), ArcadeColors.Soft, searchLabelX, _cy + 24f, searchLabelW + 4f, 66f, 0);
            DrawInputFrame(s, g, _searchX, _cy + 24f, 255f, 66f, 4f);
            s.Button(
                g, "add-toggle", _addPopup.Visible ? "닫기" : "추가", s.F(ArcadeFonts.Pixel, 26f, false),
                ArcadeColors.Cyan, ArcadeColors.Background, Color.FromArgb(0x7a, 0x63, 0xc9), 6f,
                _addButtonX, _cy + 21f, _addButtonW, 66f,
                delegate
                {
                    if (!_addOpenAtMouseDown)
                    {
                        OpenAddPopup();
                    }
                });
            s.Button(
                g, "clear", "모두 지우기", s.F(ArcadeFonts.Pixel, 26f, false),
                Color.FromArgb(0x5d, 0x4b, 0xb0), ArcadeColors.Text, ArcadeColors.Ink, 6f,
                _clearButtonX, _cy + 21f, _clearButtonW, 66f, ClearEntries);
            s.Fill(g, ArcadeColors.Border, _cx, _cy + 117f, _cw, 4f);

            // 열 이름
            Font body = s.F(ArcadeFonts.Body, 24f, false);
            s.Write(g, "내용", body, ArcadeColors.Soft, _nameX, _listY + 15f, _nameW, 35f, 0);
            s.Write(g, "닉네임", body, ArcadeColors.Soft, _nickX, _listY + 15f, 172f, 35f, 2);
            s.Write(g, "개수", body, ArcadeColors.Soft, _srcX, _listY + 15f, 216f, 35f, 2);
            s.Write(g, "코인", body, ArcadeColors.Soft, _coinX, _listY + 15f, 148f, 35f, 2);
            s.Fill(g, ArcadeColors.Border, _cx, _listY + 62f, _cw - 22f, 2f);

            // 줄은 보이는 것만 그린다. 새 줄이 맨 위다.
            ClampListScroll();
            int count = _visibleEntries.Count;
            if (count > 0 && _rowsH > 0f)
            {
                int first = Math.Max(0, (int)(_listScroll / RowHeight));
                int last = Math.Min(count - 1, (int)((_listScroll + _rowsH) / RowHeight));
                Rectangle clip = s.Rect(_cx, _rowsY, _cw - 22f, _rowsH);
                g.SetClip(clip);
                s.SetZoneClip(_cx, _rowsY, _cw - 22f, _rowsH);
                DateTime now = DateTime.Now;
                for (int display = first; display <= last; display++)
                {
                    CollectedEntry entry = _visibleEntries[count - 1 - display];
                    DrawRow(s, g, entry, _rowsY + (display * RowHeight) - _listScroll, now);
                }
                s.ClearZoneClip();
                g.ResetClip();
            }

            s.ScrollBar(
                g, "list-scroll", _cx + _cw - 22f, _listY, _listH,
                (count * RowHeight) + 64f, _listH, _listScroll,
                delegate(float offset)
                {
                    EndRowEdit();
                    _listScroll = offset;
                    ClampListScroll();
                    _stage.Invalidate();
                },
                Color.FromArgb(84, 81, 136), ArcadeColors.Line);
        }

        private void DrawRow(ArcadeSurface s, Graphics g, CollectedEntry entry, float y, DateTime now)
        {
            int id = RuntimeHelpers.GetHashCode(entry);
            float rowW = _cw - 22f;

            double age = (now - entry.AddedAt).TotalMilliseconds;
            if (age >= 0 && age < 1100)
            {
                s.Fill(g, ArcadeColors.Blend(Color.FromArgb(0xff, 0xcd, 0x75), ArcadeColors.Panel, (float)(age / 1100.0)), _cx, y, rowW, 90f);
            }

            bool editingName = Object.ReferenceEquals(_editEntry, entry) && !_editCoin;
            bool editingCoin = Object.ReferenceEquals(_editEntry, entry) && _editCoin;

            string nameKey = "name-" + id;
            if (editingName)
            {
                s.Fill(g, ArcadeColors.EditFill, _nameX - 12f, y + 12f, _nameW + 12f, 66f);
                s.Frame(g, Color.White, _nameX - 16f, y + 8f, _nameW + 20f, 74f, 4f);
            }
            else
            {
                if (s.IsHot(nameKey))
                {
                    s.Frame(g, Color.White, _nameX - 12f, y + 12f, _nameW + 12f, 66f, 2f);
                }
                string name = entry.PinballName ?? "";
                s.Write(g, name, s.F(ArcadeFonts.Display, 45f, false), Color.White, _nameX - 2f, y + 12f, _nameW - 8f, 66f, 0);
            }
            s.Zone(nameKey, _nameX - 12f, y + 12f, _nameW + 12f, 66f, delegate { BeginRowEdit(entry, false); }).Cursor = Cursors.IBeam;

            s.Write(g, entry.Nickname ?? "", s.F(ArcadeFonts.Body, 24f, false), ArcadeColors.Text, _nickX, y, 172f, 90f, 2);

            string chip = GiftSourceInfo.GetLabel(entry.Source) + " " + entry.BalloonCount.ToString("#,0");
            float chipW = Math.Min(216f, s.TextWidth(chip, ArcadeFonts.Body, 24f, true) + 28f);
            s.Fill(g, ArcadeColors.ForSource(entry.Source), _srcX + 216f - chipW, y + 23f, chipW, 44f);
            s.Write(g, chip, s.F(ArcadeFonts.Body, 24f, true), ArcadeColors.Background, _srcX + 216f - chipW + 6f, y + 23f, chipW - 12f, 44f, 1);

            string coinKey = "coin-" + id;
            if (editingCoin)
            {
                s.Fill(g, ArcadeColors.EditFill, _coinX, y + 15f, 148f, 60f);
                s.Frame(g, ArcadeColors.Accent, _coinX - 4f, y + 11f, 156f, 68f, 4f);
            }
            else
            {
                if (s.IsHot(coinKey))
                {
                    s.Frame(g, ArcadeColors.Accent, _coinX, y + 15f, 148f, 60f, 2f);
                }
                s.Write(g, entry.CoinCount.ToString(), s.F(ArcadeFonts.Digits, 32f, false), ArcadeColors.Accent, _coinX + 6f, y + 15f, 136f, 60f, 2);
            }
            s.Zone(coinKey, _coinX, y + 15f, 148f, 60f, delegate { BeginRowEdit(entry, true); }).Cursor = Cursors.IBeam;

            s.OutlineButton(
                g, "del-" + id, "삭제", s.F(ArcadeFonts.Body, 24f, true), ArcadeColors.Text, ArcadeColors.Panel,
                _delX, y + 18f, 78f, 54f, delegate { DeleteEntry(entry); });
            s.Fill(g, ArcadeColors.Border, _cx, y + 90f, rowW, 2f);
        }

        private void DrawTokens(ArcadeSurface s, Graphics g)
        {
            s.Fill(g, ArcadeColors.Ink, _rx, _tokY, 420f, _tokH);
            s.DotBorder(g, ArcadeColors.Border, _rx, _tokY, 420f, _tokH, 6f);
            s.Fill(g, Color.FromArgb(43, 40, 67), _rx, _tokY, 420f, 83f);
            s.Write(g, "수집된 핀볼 내용", s.F(ArcadeFonts.Pixel, 30f, false), Color.FromArgb(0xdf, 0xe6, 0xee), _rx + 27f, _tokY + 21f, 370f, 45f, 0);
            s.Fill(g, ArcadeColors.Border, _rx, _tokY + 83f, 420f, 3f);

            float viewY = _tokY + 86f;
            float viewH = Math.Max(0f, _tokH - 86f);
            float textW = 420f - 27f - 5f - 22f;
            Font font = s.F(ArcadeFonts.Body, 24f, false);
            if (_tokensDirty || Math.Abs(_tokenLayoutWidth - textW) > 0.01f)
            {
                LayoutTokens(s, font, textW);
            }

            float content = (_tokenLines.Count * TokenLineHeight) + 15f + 24f;
            float max = Math.Max(0f, content - viewH);
            if (_tokenStickToBottom)
            {
                _tokenScroll = max;
            }
            _tokenScroll = Math.Max(0f, Math.Min(max, _tokenScroll));

            if (_tokenLines.Count > 0 && viewH > 0f)
            {
                Rectangle clip = s.Rect(_rx, viewY, 420f - 22f, viewH);
                g.SetClip(clip);
                int first = Math.Max(0, (int)((_tokenScroll - 15f) / TokenLineHeight));
                int last = Math.Min(_tokenLines.Count - 1, (int)((_tokenScroll + viewH) / TokenLineHeight));
                int originX = s.Px(_rx + 27f);
                var brushes = new Dictionary<Color, SolidBrush>();
                try
                {
                    for (int i = first; i <= last; i++)
                    {
                        Rectangle lineRect = s.Rect(_rx + 27f, viewY + 15f + (i * TokenLineHeight) - _tokenScroll, textW, TokenLineHeight);
                        foreach (TokenRun run in _tokenLines[i])
                        {
                            SolidBrush brush;
                            if (!brushes.TryGetValue(run.Color, out brush))
                            {
                                brush = new SolidBrush(run.Color);
                                brushes[run.Color] = brush;
                            }
                            ArcadeSurface.DrawSmoothText(g, run.Text, font, brush, new RectangleF(originX + run.X, lineRect.Y, 4000f, lineRect.Height), 0);
                        }
                    }
                }
                finally
                {
                    foreach (SolidBrush brush in brushes.Values)
                    {
                        brush.Dispose();
                    }
                }
                g.ResetClip();
            }

            s.ScrollBar(
                g, "token-scroll", _rx + 420f - 22f, viewY, viewH, content, viewH, _tokenScroll,
                delegate(float offset) { SetTokenScroll(offset); },
                Color.FromArgb(49, 47, 72), ArcadeColors.Line);
        }

        private void SetTokenScroll(float offset)
        {
            float viewH = Math.Max(0f, _tokH - 86f);
            float max = Math.Max(0f, (_tokenLines.Count * TokenLineHeight) + 39f - viewH);
            _tokenScroll = Math.Max(0f, Math.Min(max, offset));
            // 맨 아래까지 내리면 다시 새 내용을 따라간다.
            _tokenStickToBottom = _tokenScroll >= max - 1f;
            _stage.Invalidate();
        }

        // "이름*코인" 조각을 종류 색으로 나눠, 칸 폭에 맞춰 글자 단위로 줄을 바꾼다.
        private void LayoutTokens(ArcadeSurface s, Font font, float textWidthLogical)
        {
            _tokensDirty = false;
            _tokenLayoutWidth = textWidthLogical;
            _tokenLines.Clear();
            float maxWidth = textWidthLogical * s.Zoom;

            var parts = new List<KeyValuePair<string, Color>>();
            foreach (CollectedEntry entry in _entries)
            {
                string part = BuildPinballEntryText(entry);
                if (part.Length > 0)
                {
                    parts.Add(new KeyValuePair<string, Color>(part, ArcadeColors.ForSource(entry.Source)));
                }
            }

            var line = new List<TokenRun>();
            float x = 0f;
            for (int p = 0; p < parts.Count; p++)
            {
                string text = parts[p].Key + (p < parts.Count - 1 ? "," : "");
                var run = new StringBuilder();
                float runX = x;
                int i = 0;
                while (i < text.Length)
                {
                    int length = Char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                    string unit = text.Substring(i, length);
                    float width;
                    if (!_tokenCharWidths.TryGetValue(unit, out width))
                    {
                        width = ArcadeFonts.Measure(unit, font);
                        _tokenCharWidths[unit] = width;
                    }

                    if (x + width > maxWidth && x > 0f)
                    {
                        if (run.Length > 0)
                        {
                            line.Add(new TokenRun { Text = run.ToString(), Color = parts[p].Value, X = runX });
                            run.Clear();
                        }
                        _tokenLines.Add(line);
                        line = new List<TokenRun>();
                        x = 0f;
                        runX = 0f;
                    }

                    run.Append(unit);
                    x += width;
                    i += length;
                }

                if (run.Length > 0)
                {
                    line.Add(new TokenRun { Text = run.ToString(), Color = parts[p].Value, X = runX });
                }
            }

            if (line.Count > 0)
            {
                _tokenLines.Add(line);
            }
        }

        private void DrawActions(ArcadeSurface s, Graphics g)
        {
            s.Button(
                g, "open", LabelFor("open", "핀볼 사이트 열기"), s.F(ArcadeFonts.Pixel, 45f, true),
                Color.FromArgb(0xa9, 0xd8, 0xff), Color.FromArgb(0x14, 0x14, 0x1c), Color.FromArgb(0x5f, 0x8f, 0xc4), 12f,
                _rx, _bigY, 420f, 126f, OpenPinballSite);
            Font small = s.F(ArcadeFonts.Pixel, 27f, true);
            Color green = Color.FromArgb(0xf3, 0xf0, 0xff);
            Color greenShadow = Color.FromArgb(0xa9, 0xa3, 0xd6);
            s.Button(g, "copy", LabelFor("copy", "내용 복사"), small, green, ArcadeColors.Background, greenShadow, 8f, _rx, _smallY, 201f, 78f, CopyPinballText);
            s.Button(g, "save", LabelFor("save", "메모장 저장"), small, green, ArcadeColors.Background, greenShadow, 8f, _rx + 219f, _smallY, 201f, 78f, SavePinballText);
        }

        // ───────── 단추 글자 잠깐 바꾸기, 움직임 ─────────

        private string LabelFor(string key, string normal)
        {
            KeyValuePair<string, DateTime> flash;
            if (_labelFlashes.TryGetValue(key, out flash) && flash.Value > DateTime.Now)
            {
                return flash.Key;
            }

            return normal;
        }

        private void FlashLabel(string key, string text, int milliseconds)
        {
            _labelFlashes[key] = new KeyValuePair<string, DateTime>(text, DateTime.Now.AddMilliseconds(milliseconds));
            StartAnimation();
            _stage.Invalidate();
        }

        private void StartAnimation()
        {
            if (!_animationTimer.Enabled && IsHandleCreated)
            {
                _animationTimer.Start();
            }
        }

        private void AnimationTick()
        {
            DateTime now = DateTime.Now;
            bool keepRunning = false;
            bool repaint = false;

            if (_flashUntil > now)
            {
                keepRunning = true;
                repaint = true;
            }
            else if (_flashUntil != DateTime.MinValue)
            {
                _flashUntil = DateTime.MinValue;
                repaint = true;
            }

            if (_labelFlashes.Count > 0)
            {
                var expired = new List<string>();
                foreach (KeyValuePair<string, KeyValuePair<string, DateTime>> pair in _labelFlashes)
                {
                    if (pair.Value.Value <= now)
                    {
                        expired.Add(pair.Key);
                    }
                }
                foreach (string key in expired)
                {
                    _labelFlashes.Remove(key);
                    repaint = true;
                }
                keepRunning |= _labelFlashes.Count > 0;
            }

            if (_connectionState == ConnectionState.Connecting)
            {
                keepRunning = true;
                int phase = (Environment.TickCount / 350) & 1;
                if (phase != _blinkPhase)
                {
                    _blinkPhase = phase;
                    repaint = true;
                }
            }

            if (_expiredGifts.Count > 0)
            {
                for (int i = _expiredGifts.Count - 1; i >= 0; i--)
                {
                    if ((now - _expiredGifts[i].Value).TotalMilliseconds >= 1300)
                    {
                        _expiredGifts.RemoveAt(i);
                    }
                }
                keepRunning |= _expiredGifts.Count > 0;
                if (_waitPopup.Visible)
                {
                    LayoutWaitPopup();
                    _waitPopup.Invalidate();
                }
            }

            if (repaint)
            {
                _stage.Invalidate();
            }
            if (!keepRunning)
            {
                _animationTimer.Stop();
            }
        }

        // ───────── 수집 대기 중 목록 ─────────

        private float WaitListHeight()
        {
            int count = _pending.Count + _expiredGifts.Count;
            if (count == 0)
            {
                return 83f;
            }

            return 12f + (count * 48f) + ((count - 1) * 12f) + 18f;
        }

        private void LayoutWaitPopup()
        {
            if (_waitPopup == null)
            {
                return;
            }

            float boxH = Math.Min(720f, 93f + WaitListHeight());
            _waitPopup.Bounds = _stage.Rect(_waitBoxRight - 660f - 6f, 163f, 672f, boxH + 12f);
        }

        private void OpenWaitPopup()
        {
            PrunePendingGifts();
            _waitScroll = 0f;
            CloseAddPopup();
            LayoutWaitPopup();
            _waitPopup.Visible = true;
            _waitPopup.BringToFront();
            if (_pending.Count > 0)
            {
                _pendingTimer.Start();
            }
        }

        private void CloseWaitPopup()
        {
            if (_waitPopup != null && _waitPopup.Visible)
            {
                _waitPopup.Visible = false;
            }
        }

        private void InvalidatePending()
        {
            if (_stage != null)
            {
                _stage.Invalidate();
            }
            if (_waitPopup != null && _waitPopup.Visible)
            {
                LayoutWaitPopup();
                _waitPopup.Invalidate();
            }
        }

        private void DrawWaitPopup(ArcadeSurface s, Graphics g)
        {
            float w = 660f;
            float h = (s.Height / s.Zoom) - 12f;
            s.Fill(g, ArcadeColors.Background, 0f, 0f, w + 12f, h + 12f);
            s.Fill(g, ArcadeColors.Header, 6f, 6f, w, h);
            s.DotBorder(g, ArcadeColors.Rose, 6f, 6f, w, h, 6f);

            Font bold = s.F(ArcadeFonts.Body, 24f, true);
            Font body = s.F(ArcadeFonts.Body, 24f, false);
            s.Write(g, "채팅 기다리는 중 " + _pending.Count.ToString("#,0") + "명", bold, ArcadeColors.Text, 30f, 24f, 440f, 54f, 0);
            float closeW = s.TextWidth("닫기", ArcadeFonts.Body, 24f, true) + 36f;
            s.OutlineButton(g, "wait-close", "닫기", bold, ArcadeColors.Text, ArcadeColors.Header, 6f + w - 24f - closeW, 24f, closeW, 54f, CloseWaitPopup);
            s.Fill(g, Color.FromArgb(109, 103, 158), 6f, 96f, w, 3f);

            float viewY = 99f;
            float viewH = Math.Max(0f, h + 6f - viewY);
            float content = WaitListHeight();
            float max = Math.Max(0f, content - viewH);
            _waitScroll = Math.Max(0f, Math.Min(max, _waitScroll));

            int total = _pending.Count + _expiredGifts.Count;
            if (total == 0)
            {
                s.Write(g, "지금 기다리는 사람이 없습니다.", body, ArcadeColors.Text, 30f, viewY + 24f, w - 60f, 35f, 0);
                return;
            }

            g.SetClip(s.Rect(6f, viewY, w - 22f, viewH));
            DateTime now = DateTime.Now;
            float itemW = w - 24f - 6f - 22f;
            for (int i = 0; i < total; i++)
            {
                bool expired = i >= _pending.Count;
                PendingGift gift = expired ? _expiredGifts[i - _pending.Count].Key : _pending[i];
                float y = viewY + 12f + (i * 60f) - _waitScroll;
                if (y + 48f < viewY || y > viewY + viewH)
                {
                    continue;
                }

                float fade = 0f;
                if (expired)
                {
                    double elapsed = (now - _expiredGifts[i - _pending.Count].Value).TotalMilliseconds;
                    fade = (float)Math.Max(0.0, Math.Min(1.0, (elapsed - 660.0) / 540.0));
                }
                Color ink = ArcadeColors.Blend(ArcadeColors.Text, ArcadeColors.Header, fade);
                Color soft = ArcadeColors.Blend(ArcadeColors.Soft, ArcadeColors.Header, fade);
                s.DashedFrame(g, ArcadeColors.Blend(ArcadeColors.Line, ArcadeColors.Header, fade), 30f, y, itemW, 48f, 2f);
                s.Swatch(g, ArcadeColors.Blend(ArcadeColors.ForSource(gift.Source), ArcadeColors.Header, fade), 48f, y, 48f, 15f, 0f);

                string left;
                if (expired)
                {
                    left = "시간 지남";
                }
                else
                {
                    double remaining = (PendingMinutes * 60000.0) - (now - gift.CreatedAt).TotalMilliseconds;
                    int minutes = Math.Min(PendingMinutes - 1, (int)Math.Floor(remaining / 60000.0));
                    left = minutes >= 1 ? minutes + "분대 남음" : "1분 미만";
                }
                string countText = gift.BalloonCount.ToString("#,0") + "개";
                float leftW = s.TextWidth(left, ArcadeFonts.Body, 24f, false);
                float countW = s.TextWidth(countText, ArcadeFonts.Body, 24f, false);
                float nickMax = itemW - 18f - 27f - 12f - countW - 12f - leftW - 18f;
                float nickW = Math.Min(nickMax, s.TextWidth(gift.Nickname ?? "", ArcadeFonts.Body, 24f, true) + 2f);
                float x = 75f;
                s.Write(g, gift.Nickname ?? "", bold, ink, x, y, nickW, 48f, 0);
                x += nickW + 12f;
                s.Write(g, countText, body, soft, x, y, countW + 4f, 48f, 0);
                x += countW + 12f;
                s.Write(g, left, body, soft, x, y, leftW + 4f, 48f, 0);
            }
            g.ResetClip();

            s.ScrollBar(
                g, "wait-scroll", 6f + w - 22f, viewY, viewH, content, viewH, _waitScroll,
                delegate(float offset)
                {
                    _waitScroll = offset;
                    _waitPopup.Invalidate();
                },
                Color.FromArgb(102, 93, 171), ArcadeColors.Line);
        }

        // ───────── 직접 추가 말풍선 ─────────

        private const float AddOriginX = 6f;
        private const float AddOriginY = 29f;

        private void LayoutAddPopup()
        {
            if (_addPopup == null)
            {
                return;
            }

            float boxX = Math.Max(48f, _cx + _cw - 225f - 705f);
            float boxY = _cy + 128f;
            _addPopup.Bounds = _stage.Rect(boxX - AddOriginX, boxY - AddOriginY, 705f + 12f, 295f + AddOriginY + 6f);

            // 말풍선 상자와 위쪽 꼬리 모양만 남긴다. 꼬리 옆의 빈 자리가 검색칸과 단추 아랫부분을 가리지 않게 한다.
            float tailX = AddOriginX + 705f - 44f;
            using (var shape = new GraphicsPath())
            {
                shape.AddRectangle(_addPopup.Rect(0f, AddOriginY - 6f, 705f + 12f, 295f + 12f));
                shape.AddPolygon(new[]
                {
                    new Point(_addPopup.Px(tailX - 25f), _addPopup.Px(AddOriginY - 6f) + 1),
                    new Point(_addPopup.Px(tailX + 25f), _addPopup.Px(AddOriginY - 6f) + 1),
                    new Point(_addPopup.Px(tailX), _addPopup.Px(AddOriginY - 31f))
                });
                shape.FillMode = FillMode.Winding;
                Region previous = _addPopup.Region;
                _addPopup.Region = new Region(shape);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }

            float ox = AddOriginX;
            float oy = AddOriginY;
            float buttonW = _addPopup.TextWidth("넣기", ArcadeFonts.Display, 27f, false) + 60f;
            float buttonX = ox + 27f + 651f - buttonW;
            float countX = buttonX - 15f - 126f;
            float kindX = countX - 15f - 210f;
            _addNameInput.Place(_addPopup, ox + 27f, oy + 65f, 651f, 78f, 22f, ArcadeFonts.Display, 33f, false);
            _addNickInput.Place(_addPopup, ox + 27f, oy + 202f, kindX - 15f - (ox + 27f), 66f, 22f, ArcadeFonts.Body, 24f, false);
            _addCountInput.Place(_addPopup, countX, oy + 202f, 126f, 66f, 22f, ArcadeFonts.Body, 24f, false);

            _kindPopup.Bounds = _stage.Rect(boxX - AddOriginX + kindX, boxY - AddOriginY + oy + 202f + 72f, 210f, 248f);
        }

        private void OpenAddPopup()
        {
            CloseWaitPopup();
            EndRowEdit();
            LayoutAddPopup();
            _kindPopup.Visible = false;
            _addPopup.Visible = true;
            _addPopup.BringToFront();
            _addNameInput.Focus();
            _stage.Invalidate();
        }

        private void CloseAddPopup()
        {
            if (_addPopup == null || !_addPopup.Visible)
            {
                return;
            }

            _kindPopup.Visible = false;
            _addPopup.Visible = false;
            _stage.Invalidate();
        }

        private void DrawAddPopup(ArcadeSurface s, Graphics g)
        {
            float ox = AddOriginX;
            float oy = AddOriginY;
            float w = 705f;
            float h = 295f;
            s.Fill(g, ArcadeColors.Panel, 0f, 0f, w + 12f, h + oy + 6f);
            s.Fill(g, ArcadeColors.Header, ox, oy, w, h);
            s.DotBorder(g, ArcadeColors.Cyan, ox, oy, w, h, 6f);

            // 위쪽 꼬리
            float tailX = ox + w - 44f;
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outer = new SolidBrush(ArcadeColors.Cyan))
            using (var inner = new SolidBrush(ArcadeColors.Header))
            {
                g.FillPolygon(outer, new[]
                {
                    new PointF(s.Px(tailX - 23f), s.Px(oy - 6f) + 1),
                    new PointF(s.Px(tailX + 23f), s.Px(oy - 6f) + 1),
                    new PointF(s.Px(tailX), s.Px(oy - 29f))
                });
                g.FillPolygon(inner, new[]
                {
                    new PointF(s.Px(tailX - 15f), s.Px(oy) + 1),
                    new PointF(s.Px(tailX + 15f), s.Px(oy) + 1),
                    new PointF(s.Px(tailX), s.Px(oy - 17f))
                });
            }
            g.SmoothingMode = previous;

            Font bold = s.F(ArcadeFonts.Body, 24f, true);
            s.Write(g, "내용", bold, ArcadeColors.Text, ox + 27f, oy + 24f, 300f, 35f, 0);
            DrawInputFrame(s, g, ox + 27f, oy + 65f, 651f, 78f, 4f);

            float buttonW = s.TextWidth("넣기", ArcadeFonts.Display, 27f, false) + 60f;
            float buttonX = ox + 27f + 651f - buttonW;
            float countX = buttonX - 15f - 126f;
            float kindX = countX - 15f - 210f;
            float nickW = kindX - 15f - (ox + 27f);
            float labelY = oy + 161f;
            float inputY = oy + 202f;

            s.Write(g, "닉네임", bold, ArcadeColors.Text, ox + 27f, labelY, nickW, 35f, 0);
            DrawInputFrame(s, g, ox + 27f, inputY, nickW, 66f, 4f);

            s.Write(g, "종류", bold, ArcadeColors.Text, kindX, labelY, 210f, 35f, 0);
            DrawInputFrame(s, g, kindX, inputY, 210f, 66f, 4f);
            s.Swatch(g, ArcadeColors.ForSource(_addKind), kindX + 16f, inputY, 66f, 18f, 2f);
            s.Write(g, GiftSourceInfo.GetLabel(_addKind), s.F(ArcadeFonts.Body, 24f, false), ArcadeColors.Text, kindX + 46f, inputY, 120f, 66f, 0);
            s.TriangleDown(g, ArcadeColors.Text, kindX + 210f - 16f - 16f, inputY + 28f, 16f, 10f);
            s.Zone("add-kind", kindX, inputY, 210f, 66f, delegate
            {
                _kindPopup.Visible = !_kindPopup.Visible;
                if (_kindPopup.Visible)
                {
                    _kindPopup.BringToFront();
                }
            });

            s.Write(g, "개수", bold, ArcadeColors.Text, countX, labelY, 126f, 35f, 0);
            DrawInputFrame(s, g, countX, inputY, 126f, 66f, 4f);

            s.Button(
                g, "add-now", "넣기", s.F(ArcadeFonts.Display, 27f, false),
                ArcadeColors.Accent, ArcadeColors.Background, Color.FromArgb(0xb8, 0x86, 0x3b), 6f,
                buttonX, inputY, buttonW, 66f, AddFromPopup);
        }

        private void DrawKindPopup(ArcadeSurface s, Graphics g)
        {
            s.Fill(g, ArcadeColors.Background, 0f, 0f, 210f, 248f);
            s.Frame(g, ArcadeColors.Line, 0f, 0f, 210f, 248f, 4f);
            GiftSource[] kinds = { GiftSource.StarBalloon, GiftSource.AdBalloon, GiftSource.ChallengeGift, GiftSource.BattleMission };
            for (int i = 0; i < kinds.Length; i++)
            {
                GiftSource kind = kinds[i];
                string key = "kind-option-" + i;
                float y = 4f + (i * 60f);
                bool selected = kind == _addKind;
                if (selected || s.IsHot(key))
                {
                    s.Fill(g, ArcadeColors.Blend(ArcadeColors.Background, Color.FromArgb(127, 127, 127), 0.3f), 4f, y, 202f, 60f);
                }
                s.Swatch(g, ArcadeColors.ForSource(kind), 16f, y, 60f, 18f, 2f);
                s.Write(g, GiftSourceInfo.GetLabel(kind), s.F(ArcadeFonts.Body, 24f, selected), ArcadeColors.Text, 46f, y, 150f, 60f, 0);
                s.Zone(key, 4f, y, 202f, 60f, delegate
                {
                    _addKind = kind;
                    _kindPopup.Visible = false;
                    _addPopup.Invalidate();
                });
            }
        }

        private void AddFromPopup()
        {
            string name = (_addNameInput.Text ?? "").Trim();
            if (name.Length == 0)
            {
                _addNameInput.Focus();
                return;
            }

            string nickname = (_addNickInput.Text ?? "").Trim();
            var entry = new CollectedEntry();
            entry.Source = _addKind;
            entry.Nickname = nickname.Length == 0 ? "직접 추가" : nickname;
            entry.BalloonCount = Math.Max(1, _addCountInput.Value);
            entry.CoinCount = CalculateCoins(entry.BalloonCount);
            entry.PinballName = name;
            entry.ReceivedAt = DateTime.Now.ToString("HH:mm:ss");
            _addNameInput.Text = "";
            _addNickInput.Text = "";
            CloseAddPopup();
            AddCollectedEntry(entry);
        }

        // ───────── 메모장 ─────────

        private void LayoutMemoPopup()
        {
            if (_memoPopup == null)
            {
                return;
            }

            _memoWidth = (int)Math.Max(420f, Math.Min(_lw, _memoWidth));
            _memoHeight = (int)Math.Max(260f, Math.Min(_lh, _memoHeight));
            _memoX = (int)Math.Max(0f, Math.Min(_lw - _memoWidth, _memoX));
            _memoY = (int)Math.Max(0f, Math.Min(_lh - 84f, _memoY));
            _memoPopup.Bounds = _stage.Rect(_memoX, _memoY, _memoWidth, _memoHeight);
            _memoText.Place(_memoPopup, 0f, 84f + 21f, _memoWidth - 12f, _memoHeight - 84f - 21f - 36f, 24f, ArcadeFonts.Body, _memoFont, false);
            _memoPopup.Invalidate();
        }

        // 메모장을 옮기거나 크기를 바꾼 뒤, 지나간 자리의 판과 입력칸을 바로 다시 그려 잔상이 남지 않게 한다.
        private void RepaintAroundMemo(Rectangle before)
        {
            Rectangle area = Rectangle.Union(before, _memoPopup.Bounds);
            area.Inflate(2, 2);
            _stage.Invalidate(area, true);
            _stage.Update();
            _memoPopup.Update();
        }

        private void ToggleMemo()
        {
            _memoOpen = !_memoOpen;
            LayoutMemoPopup();
            _memoPopup.Visible = _memoOpen;
            if (_memoOpen)
            {
                _memoPopup.BringToFront();
                _memoText.Focus();
            }
            _stage.Invalidate();
            QueueSave();
        }

        private void ChangeMemoFont(int delta)
        {
            _memoFont = Math.Max(12, Math.Min(72, _memoFont + delta));
            LayoutMemoPopup();
            QueueSave();
        }

        private void DrawMemoPopup(ArcadeSurface s, Graphics g)
        {
            float w = _memoWidth;
            float h = _memoHeight;
            Color ink = Color.FromArgb(0x17, 0x17, 0x1c);
            s.Fill(g, Color.White, 0f, 0f, w, h);
            s.Fill(g, Color.FromArgb(0xe6, 0xe6, 0xec), 0f, 0f, w, 84f);

            ArcadeZone bar = s.Zone("memo-bar", 0f, 0f, w, 84f, null);
            bar.Cursor = Cursors.SizeAll;
            bar.DragStart = delegate
            {
                _memoDragX = _memoX;
                _memoDragY = _memoY;
            };
            bar.Drag = delegate(float dx, float dy)
            {
                _memoX = (int)Math.Round(Math.Max(0f, Math.Min(_lw - _memoWidth, _memoDragX + dx)));
                _memoY = (int)Math.Round(Math.Max(0f, Math.Min(_lh - 84f, _memoDragY + dy)));
                Rectangle before = _memoPopup.Bounds;
                _memoPopup.Location = _stage.Rect(_memoX, _memoY, 1f, 1f).Location;
                RepaintAroundMemo(before);
            };
            bar.DragEnd = QueueSave;

            Font pixel = s.F(ArcadeFonts.Pixel, 24f, false);
            s.Write(g, "메모장", s.F(ArcadeFonts.Pixel, 32f, false), ink, 24f, 9f, 200f, 66f, 0);

            float closeW = s.TextWidth("닫기", ArcadeFonts.Pixel, 24f, false) + 42f;
            float x = w - 9f - closeW;
            s.Button(g, "memo-close", "닫기", pixel, ink, Color.White, ink, 0f, x, 9f, closeW, 66f, ToggleMemo);
            x -= 15f + 54f;
            s.Button(g, "memo-up", "+", s.F(ArcadeFonts.Body, 24f, true), ink, Color.White, ink, 0f, x, 9f, 54f, 66f, delegate { ChangeMemoFont(2); });
            x -= 15f + 54f;
            s.Button(g, "memo-down", "-", pixel, ink, Color.White, ink, 0f, x, 9f, 54f, 66f, delegate { ChangeMemoFont(-2); });
            string fontText = "글씨 " + _memoFont + "px";
            float fontW = s.TextWidth(fontText, ArcadeFonts.Pixel, 24f, false) + 4f;
            x -= 15f + fontW;
            if (x > 150f)
            {
                s.Write(g, fontText, pixel, ink, x, 9f, fontW, 66f, 0);
            }

            // 오른쪽 아래 크기 조절 손잡이
            Rectangle grip = s.Rect(w - 36f, h - 36f, 36f, 36f);
            using (var pen = new Pen(Color.FromArgb(140, 28, 26, 16), Math.Max(1f, 3f * s.Zoom)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 1; i <= 3; i++)
                {
                    float offset = grip.Width * (0.25f * i);
                    g.DrawLine(pen, grip.Right - offset, grip.Bottom - 2, grip.Right - 2, grip.Bottom - offset);
                }
                g.SmoothingMode = SmoothingMode.None;
            }
            ArcadeZone size = s.Zone("memo-size", w - 36f, h - 36f, 36f, 36f, null);
            size.Cursor = Cursors.SizeNWSE;
            size.DragStart = delegate
            {
                _memoDragX = _memoWidth;
                _memoDragY = _memoHeight;
            };
            size.Drag = delegate(float dx, float dy)
            {
                _memoWidth = (int)Math.Round(Math.Min(_lw - _memoX, Math.Max(420f, _memoDragX + dx)));
                _memoHeight = (int)Math.Round(Math.Min(_lh - _memoY, Math.Max(260f, _memoDragY + dy)));
                Rectangle before = _memoPopup.Bounds;
                LayoutMemoPopup();
                RepaintAroundMemo(before);
            };
            size.DragEnd = QueueSave;

            s.Frame(g, ink, 0f, 0f, w, h, 3f);
        }

        // ───────── 줄 고치기 ─────────

        private void BeginRowEdit(CollectedEntry entry, bool coin)
        {
            if (entry == null || !_entries.Contains(entry))
            {
                return;
            }

            _editEntry = entry;
            _editCoin = coin;
            _updatingRowEditor = true;
            try
            {
                _rowEditor.TextAlign = coin ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                _rowEditor.ForeColor = coin ? ArcadeColors.Accent : Color.White;
                _rowEditor.MaxLength = coin ? 6 : 32767;
                _rowEditor.Text = coin ? entry.CoinCount.ToString() : (entry.PinballName ?? "");
            }
            finally
            {
                _updatingRowEditor = false;
            }

            PlaceRowEditor();
            if (_editEntry == null)
            {
                return;
            }

            _rowEditor.Visible = true;
            _rowEditor.BringToFront();
            _rowEditor.Focus();
            _rowEditor.SelectionStart = _rowEditor.TextLength;
            _stage.Invalidate();
        }

        private void PlaceRowEditor()
        {
            if (_editEntry == null || _rowEditor == null)
            {
                return;
            }

            int index = _visibleEntries.IndexOf(_editEntry);
            if (index < 0)
            {
                EndRowEdit();
                return;
            }

            int display = _visibleEntries.Count - 1 - index;
            float y = _rowsY + (display * RowHeight) - _listScroll;
            if (y + 12f < _rowsY || y + 78f > _rowsY + _rowsH)
            {
                EndRowEdit();
                return;
            }

            if (_editCoin)
            {
                _rowEditor.Place(_stage, _coinX, y + 15f, 148f, 60f, 8f, ArcadeFonts.Digits, 32f, false);
            }
            else
            {
                _rowEditor.Place(_stage, _nameX - 12f, y + 12f, _nameW + 12f, 66f, 10f, ArcadeFonts.Display, 45f, false);
            }
        }

        private void ApplyRowEditorText()
        {
            if (_updatingRowEditor || _editEntry == null)
            {
                return;
            }

            if (_editCoin)
            {
                string digits = Regex.Replace(_rowEditor.Text ?? "", "[^0-9]", "");
                int coins;
                if (!Int32.TryParse(digits, out coins))
                {
                    coins = 0;
                }
                _editEntry.CoinCount = coins;
                _editEntry.CoinEdited = true;
            }
            else
            {
                _editEntry.PinballName = _rowEditor.Text ?? "";
            }

            // 목록 순서와 스크롤은 그대로 두고 문자열과 합계만 다시 만든다.
            RefreshPinballText(false);
            QueueSave();
        }

        private void EndRowEdit()
        {
            if (_editEntry == null)
            {
                return;
            }

            _editEntry = null;
            if (_rowEditor != null && _rowEditor.Visible)
            {
                _rowEditor.Visible = false;
            }
            if (_stage != null)
            {
                _stage.Invalidate();
            }
        }

        private bool IsEditingEntryList()
        {
            return _editEntry != null;
        }

        private void DeleteEntry(CollectedEntry entry)
        {
            int entryIndex = _entries.IndexOf(entry);
            if (entryIndex < 0)
            {
                return;
            }

            if (Object.ReferenceEquals(_editEntry, entry))
            {
                EndRowEdit();
            }
            _entries.RemoveAt(entryIndex);
            RefreshPinballText(false);
            RefreshCounts();
            QueueSave();
        }

        // ───────── 마우스 휠 ─────────

        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg != 0x020A || IsDisposed || _stage == null)
            {
                return false;
            }

            Control target = Control.FromChildHandle(message.HWnd);
            if (target == null || target.FindForm() != this)
            {
                return false;
            }

            // 휠 메시지의 좌표는 화면 기준이다.
            long position = message.LParam.ToInt64();
            Point mouse = _stage.PointToClient(new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff)));
            if (!_stage.ClientRectangle.Contains(mouse))
            {
                return false;
            }

            int delta = (short)((message.WParam.ToInt64() >> 16) & 0xffff);
            float step = -(delta / 120f) * 138f;
            if (_memoPopup.Visible && _memoPopup.Bounds.Contains(mouse))
            {
                return false;
            }

            if (_waitPopup.Visible && _waitPopup.Bounds.Contains(mouse))
            {
                _waitScroll += step;
                _waitPopup.Invalidate();
                return true;
            }

            if (_stage.Rect(_cx, _listY, _cw, _listH).Contains(mouse))
            {
                EndRowEdit();
                _listScroll += step;
                ClampListScroll();
                _stage.Invalidate();
                return true;
            }

            if (_stage.Rect(_rx, _tokY, 420f, _tokH).Contains(mouse))
            {
                SetTokenScroll(_tokenScroll + step);
                return true;
            }

            return false;
        }

        // ───────── 설정 바꾸기 ─────────

        private void SetExactMode(bool exact)
        {
            _exactMode = exact;
            _stage.Invalidate();
            QueueSave();
        }

        private void SetBonus(bool enabled)
        {
            if (_bonusPlusOne == enabled)
            {
                return;
            }

            _bonusPlusOne = enabled;
            RecalculateCoins();
            RefreshPinballText(false);
            QueueSave();
        }

        private void ToggleGiftSource(GiftSource source)
        {
            if (source == GiftSource.AdBalloon)
            {
                _collectAdBalloon = !_collectAdBalloon;
            }
            else if (source == GiftSource.ChallengeGift)
            {
                _collectChallengeGift = !_collectChallengeGift;
            }
            else if (source == GiftSource.BattleMission)
            {
                _collectBattleMission = !_collectBattleMission;
            }
            else
            {
                _collectStarBalloon = !_collectStarBalloon;
            }

            _stage.Invalidate();
            QueueSave();
        }

        private void SetNicknameMode(bool nickname)
        {
            if (_nicknamePinballMode != nickname)
            {
                // 반영 방식을 바꾸면 기다리던 후원도 지운다.
                _pending.Clear();
                _expiredGifts.Clear();
            }
            _nicknamePinballMode = nickname;
            InvalidatePending();
            QueueSave();
        }

        // ───────── 연결 ─────────

        private void SetStatus(ConnectionState state, string text, string tip)
        {
            _connectionState = state;
            _statusText = text ?? "";
            _statusTip = tip ?? "";
            if (state == ConnectionState.Connecting)
            {
                StartAnimation();
            }
            if (_stage != null)
            {
                _stage.Invalidate();
            }
        }

        private void ToggleConnection()
        {
            if (_connectionState != ConnectionState.Off || _connected || _connecting || _reconnectTimer.Enabled)
            {
                Disconnect(true);
            }
            else
            {
                StartConnection();
            }
        }

        private void StartConnection()
        {
            StartConnection(false);
        }

        private async void StartConnection(bool retry)
        {
            string nextStreamerId = ExtractStreamerId(_streamInput.Text.Trim());
            if (nextStreamerId.Length == 0)
            {
                SetStatus(ConnectionState.Off, "ID 확인 필요", "");
                return;
            }

            _manualDisconnect = false;
            _connected = false;
            _connecting = true;
            _reconnectTimer.Stop();
            _connectTimer.Stop();
            CloseChatClient();
            if (!retry)
            {
                _reconnectAttempt = 0;
                _joinedOnce = false;
            }
            if (!String.Equals(_streamerId, nextStreamerId, StringComparison.OrdinalIgnoreCase))
            {
                // 방송을 바꾸면 기다리던 후원은 지운다.
                _pending.Clear();
                _expiredGifts.Clear();
                InvalidatePending();
            }
            _streamerId = nextStreamerId;
            _streamerName = ResolveDisplayName(_streamerId);
            SetStatus(ConnectionState.Connecting, retry ? "다시 연결 중" : "연결 중", "");

            var client = new SoopLiveChatClient();
            _chatClient = client;
            AttachChatClient(client);

            try
            {
                await client.ConnectAsync(_streamerId, "");
            }
            catch (Exception ex)
            {
                if (_chatClient != client)
                {
                    return;
                }

                HandleConnectFailure(ex);
            }
        }

        private void HandleConnectFailure(Exception ex)
        {
            CloseChatClient();
            _connected = false;
            _connecting = false;

            var notLive = ex as SoopNotLiveException;
            if (notLive != null)
            {
                // 방송이 꺼져 있거나 들어갈 수 없는 방송이면 다시 시도하지 않고 멈춘다.
                _manualDisconnect = true;
                _reconnectTimer.Stop();
                if (notLive.Result == SoopNotLiveException.AdultOnly)
                {
                    SetStatus(ConnectionState.Off, "입장 제한(19세)", "입장 제한 방송(19세): 로그인이 필요한 방송이라 수집할 수 없습니다.");
                }
                else
                {
                    SetStatus(ConnectionState.Off, _joinedOnce ? "방송 종료" : "방송 중 아님", "");
                }
                return;
            }

            if (!_manualDisconnect)
            {
                ScheduleReconnect(ex == null ? "" : ex.Message);
            }
        }

        private void Disconnect(bool manual)
        {
            _manualDisconnect = manual;
            _connected = false;
            _connecting = false;
            _connectTimer.Stop();
            _reconnectTimer.Stop();
            CloseChatClient();
            SetStatus(ConnectionState.Off, "연결 안 됨", "");
        }

        private void ScheduleReconnect(string reason)
        {
            if (_manualDisconnect)
            {
                return;
            }

            _reconnectAttempt++;
            int wait = Math.Min(12000, 1000 + (_reconnectAttempt * 1500));
            _reconnectTimer.Interval = wait;
            SetStatus(ConnectionState.Connecting, "다시 연결 중", reason);
            _reconnectTimer.Start();
        }

        private void AttachChatClient(SoopLiveChatClient client)
        {
            client.BroadcastResolved += delegate(SoopLiveInfo info)
            {
                RunOnUiThread(delegate
                {
                    if (_chatClient != client)
                    {
                        return;
                    }

                    _streamerName = info.StreamerName;
                });
            };

            client.Joined += delegate
            {
                RunOnUiThread(delegate
                {
                    if (_chatClient != client)
                    {
                        return;
                    }

                    _connected = true;
                    _connecting = false;
                    _joinedOnce = true;
                    _reconnectAttempt = 0;
                    SetStatus(ConnectionState.On, _streamerName + " 연결됨", "");
                });
            };

            client.BalloonReceived += delegate(GiftSource source, string nickname, int count)
            {
                RunOnUiThread(delegate
                {
                    if (_chatClient == client)
                    {
                        HandleBalloonGift(source, nickname, count);
                    }
                });
            };

            client.ChatReceived += delegate(string nickname, string message)
            {
                RunOnUiThread(delegate
                {
                    if (_chatClient == client)
                    {
                        HandleChatMessage(nickname, message);
                    }
                });
            };

            client.Disconnected += delegate(string reason)
            {
                RunOnUiThread(delegate
                {
                    if (_chatClient != client)
                    {
                        return;
                    }

                    CloseChatClient();
                    _connected = false;
                    _connecting = false;
                    if (!_manualDisconnect)
                    {
                        ScheduleReconnect(reason);
                    }
                });
            };
        }

        private void CloseChatClient()
        {
            SoopLiveChatClient client = _chatClient;
            _chatClient = null;
            if (client != null)
            {
                client.Dispose();
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (IsDisposed || action == null)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch
                {
                }
                return;
            }

            action();
        }

        // ───────── 후원·채팅 처리 ─────────

        private void AddManualTestEntry()
        {
            var entry = new CollectedEntry();
            entry.Source = GiftSource.StarBalloon;
            entry.Nickname = "직접 추가";
            entry.BalloonCount = _thresholdInput.Value;
            entry.CoinCount = CalculateCoins(entry.BalloonCount);
            entry.PinballName = "종겜핀볼 테스트";
            entry.ReceivedAt = DateTime.Now.ToString("HH:mm:ss");
            AddCollectedEntry(entry);
        }

        private void HandleBalloonGift(GiftSource source, string nickname, int count)
        {
            if (!IsGiftSourceEnabled(source))
            {
                return;
            }

            if (nickname.Length == 0)
            {
                nickname = "익명";
            }

            int threshold = _thresholdInput.Value;
            bool passed = _exactMode
                ? count >= threshold && count % threshold == 0
                : count >= threshold;
            if (!passed)
            {
                return;
            }

            if (_nicknamePinballMode)
            {
                var entry = new CollectedEntry();
                entry.Source = source;
                entry.Nickname = nickname;
                entry.BalloonCount = count;
                entry.CoinCount = CalculateCoins(count);
                entry.PinballName = nickname;
                entry.ReceivedAt = DateTime.Now.ToString("HH:mm:ss");
                AddCollectedEntry(entry);
                return;
            }

            var gift = new PendingGift();
            gift.Source = source;
            gift.Nickname = nickname;
            gift.BalloonCount = count;
            gift.CoinCount = CalculateCoins(count);
            gift.CreatedAt = DateTime.Now;
            _pending.Add(gift);
            PrunePendingGifts();
            if (IsHandleCreated)
            {
                _pendingTimer.Start();
            }
            InvalidatePending();
            QueueSave();
        }

        private void HandleChatMessage(string nickname, string message)
        {
            if (nickname.Length == 0)
            {
                nickname = "익명";
            }

            if (message.Length == 0)
            {
                message = nickname;
            }

            PrunePendingGifts();
            PendingGift matched = null;
            // 같은 닉네임의 대기 후원은 수신 순서대로 채팅 1회씩 소비한다.
            for (int i = 0; i < _pending.Count; i++)
            {
                if (String.Equals(_pending[i].Nickname, nickname, StringComparison.OrdinalIgnoreCase))
                {
                    matched = _pending[i];
                    _pending.RemoveAt(i);
                    break;
                }
            }

            if (matched == null)
            {
                return;
            }

            var entry = new CollectedEntry();
            entry.Source = matched.Source;
            entry.Nickname = matched.Nickname;
            entry.BalloonCount = matched.BalloonCount;
            entry.CoinCount = matched.CoinCount;
            // 대기 목록에는 채팅 내용 모드에서 받은 후원만 들어온다.
            entry.PinballName = message;
            entry.ReceivedAt = DateTime.Now.ToString("HH:mm:ss");
            AddCollectedEntry(entry);
            InvalidatePending();
        }

        private void AddCollectedEntry(CollectedEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            entry.AddedAt = DateTime.Now;
            _entries.Add(entry);
            AppendPinballText(entry);
            RefreshEntryListAfterAppend(entry);
            _flashUntil = entry.AddedAt.AddMilliseconds(1150);
            StartAnimation();
            QueueSave();
        }

        private void PrunePendingGifts()
        {
            // 오래된 후원이 이후의 무관한 채팅에 연결되지 않도록 대기 시간을 제한한다.
            DateTime now = DateTime.Now;
            DateTime cutoff = now.AddMinutes(-PendingMinutes);
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].CreatedAt < cutoff)
                {
                    // 목록이 열려 있으면 "시간 지남"으로 잠깐 보여 준 뒤 뺀다. 명단에는 오르지 않는다.
                    if (_waitPopup != null && _waitPopup.Visible)
                    {
                        _expiredGifts.Insert(0, new KeyValuePair<PendingGift, DateTime>(_pending[i], now));
                        StartAnimation();
                    }
                    _pending.RemoveAt(i);
                }
            }
        }

        private int CalculateCoins(int balloonCount)
        {
            int unit = GetCoinUnit();
            // "보너스 +1"이 켜져 있으면 한 번의 후원에서 기준 단위 10회마다 보너스 1코인을 더한다.
            int baseCoins = balloonCount / unit;
            int bonusCoins = _bonusPlusOne ? baseCoins / 10 : 0;
            return Math.Max(1, baseCoins + bonusCoins);
        }

        private int GetCoinUnit()
        {
            if (_thresholdInput == null)
            {
                return 100;
            }

            return Math.Max(1, _thresholdInput.Value);
        }

        private void RecalculateCoins()
        {
            foreach (CollectedEntry entry in _entries)
            {
                // 코인을 직접 고친 줄은 그대로 둔다.
                if (entry != null && !entry.CoinEdited)
                {
                    entry.CoinCount = CalculateCoins(entry.BalloonCount);
                }
            }

            foreach (PendingGift gift in _pending)
            {
                if (gift != null)
                {
                    gift.CoinCount = CalculateCoins(gift.BalloonCount);
                }
            }
        }

        // ───────── 핀볼 문자열과 목록 ─────────

        private void RefreshPinballText()
        {
            RefreshPinballText(false);
        }

        private void RefreshPinballText(bool scrollToBottom)
        {
            _generatedPinballText.Clear();
            _generatedPinballCoinTotal = 0;
            foreach (CollectedEntry entry in _entries)
            {
                string part = BuildPinballEntryText(entry);
                if (part.Length > 0)
                {
                    if (_generatedPinballText.Length > 0)
                    {
                        _generatedPinballText.Append(',');
                    }
                    _generatedPinballText.Append(part);
                    _generatedPinballCoinTotal += entry.CoinCount;
                }
            }

            if (scrollToBottom)
            {
                _tokenStickToBottom = true;
            }
            _tokensDirty = true;
            if (_stage != null)
            {
                _stage.Invalidate();
            }
        }

        private string BuildPinballEntryText(CollectedEntry entry)
        {
            if (entry == null || entry.CoinCount <= 0)
            {
                return "";
            }

            string name = SanitizePinballName(entry.PinballName);
            if (name.Length == 0)
            {
                name = SanitizePinballName(entry.Nickname);
            }

            return name.Length == 0 ? "" : name + "*" + entry.CoinCount;
        }

        private void AppendPinballText(CollectedEntry entry)
        {
            string part = BuildPinballEntryText(entry);
            if (part.Length > 0)
            {
                // 전체를 다시 만들지 않고 새 조각만 끝에 붙인다.
                _generatedPinballText.Append((_generatedPinballText.Length > 0 ? "," : "") + part);
                _generatedPinballCoinTotal += entry.CoinCount;
            }

            _tokensDirty = true;
        }

        private void RefreshCounts()
        {
            RebuildVisibleEntries();
            ClampListScroll();
            PlaceRowEditor();
            if (_stage != null)
            {
                _stage.Invalidate();
            }
        }

        private void RefreshEntryListAfterAppend(CollectedEntry entry)
        {
            string query = GetSearchText();
            if (!String.Equals(query, _visibleEntriesSearchText, StringComparison.Ordinal))
            {
                RefreshCounts();
                return;
            }

            if (query.Length == 0 || EntryMatchesSearch(entry, query))
            {
                _visibleEntries.Add(entry);
                // 새 줄은 맨 위에 들어간다. 줄을 고치는 중이거나 아래쪽을 보고 있었다면 보던 자리를 그대로 둔다.
                if (_editEntry != null || _listScroll > 0.5f)
                {
                    _listScroll += RowHeight;
                }
                ClampListScroll();
                PlaceRowEditor();
            }

            if (_stage != null)
            {
                _stage.Invalidate();
            }
        }

        private void RebuildVisibleEntries()
        {
            _visibleEntries.Clear();
            string query = GetSearchText();
            _visibleEntriesSearchText = query;
            foreach (CollectedEntry entry in _entries)
            {
                if (query.Length == 0 || EntryMatchesSearch(entry, query))
                {
                    _visibleEntries.Add(entry);
                }
            }
        }

        private string GetSearchText()
        {
            return _searchInput == null ? "" : (_searchInput.Text ?? "").Trim();
        }

        private bool EntryMatchesSearch(CollectedEntry entry, string query)
        {
            if (entry == null)
            {
                return false;
            }

            // 내용과 닉네임에서 찾는다.
            return ContainsSearch(entry.Nickname, query) || ContainsSearch(entry.PinballName, query);
        }

        private static bool ContainsSearch(string value, string query)
        {
            return (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsGiftSourceEnabled(GiftSource source)
        {
            if (source == GiftSource.AdBalloon)
            {
                return _collectAdBalloon;
            }

            if (source == GiftSource.ChallengeGift)
            {
                return _collectChallengeGift;
            }

            if (source == GiftSource.BattleMission)
            {
                return _collectBattleMission;
            }

            return _collectStarBalloon;
        }

        // ───────── 내보내기 ─────────

        private void CopyPinballText()
        {
            string text = _generatedPinballText.ToString().Trim();
            if (text.Length == 0)
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
                FlashLabel("copy", "복사됨", 1200);
            }
            catch
            {
                FlashLabel("copy", "복사 실패", 1200);
            }
        }

        private void SavePinballText()
        {
            string text = _generatedPinballText.ToString().Trim();
            if (text.Length == 0)
            {
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "메모장 저장";
                dialog.Filter = "Text file (*.txt)|*.txt";
                dialog.FileName = "결희_핀볼_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".txt";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
                }
            }
        }

        private void OpenPinballSite()
        {
            try
            {
                string names = NormalizePinballNames(_generatedPinballText.ToString());
                if (names.Length == 0)
                {
                    Process.Start(PinballUrl);
                    return;
                }

                string url = BuildPinballSiteUrl(names);
                if (url.Length <= DirectPinballUrlLimit)
                {
                    Process.Start(url);
                    return;
                }

                // 주소에 다 붙일 수 없는 긴 목록은 복사해 두고 사이트에서 붙여 넣게 한다.
                Clipboard.SetText(names);
                Process.Start(PinballUrl);
                FlashLabel("open", "복사됨, 붙여넣기", 8000);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "사이트 열기 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string BuildPinballSiteUrl()
        {
            return BuildPinballSiteUrl(NormalizePinballNames(_generatedPinballText.ToString()));
        }

        private string BuildPinballSiteUrl(string names)
        {
            if (names.Length == 0)
            {
                return PinballUrl;
            }

            return PinballUrl + "?names=" + Uri.EscapeDataString(names);
        }

        private string NormalizePinballNames(string value)
        {
            if (value == null)
            {
                return "";
            }

            string[] rawParts = value.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var parts = new List<string>();
            foreach (string rawPart in rawParts)
            {
                string part = rawPart.Trim();
                if (part.Length > 0)
                {
                    parts.Add(part);
                }
            }

            return String.Join(",", parts.ToArray());
        }

        private void ClearEntries()
        {
            if (_entries.Count == 0 && _pending.Count == 0)
            {
                return;
            }

            // 화면 안의 확인 창에서 "확인"을 눌러야 지운다.
            EndRowEdit();
            CloseAddPopup();
            CloseWaitPopup();
            LayoutConfirmPopup();
            _confirmPopup.Visible = true;
            _confirmPopup.BringToFront();
        }

        private void LayoutConfirmPopup()
        {
            if (_confirmPopup == null)
            {
                return;
            }

            _confirmPopup.Bounds = _stage.Rect((_lw - 792f) / 2f, (_lh - 342f) / 2f, 792f, 342f);
        }

        private void CloseConfirmPopup()
        {
            if (_confirmPopup != null && _confirmPopup.Visible)
            {
                _confirmPopup.Visible = false;
            }
        }

        private void DrawConfirmPopup(ArcadeSurface s, Graphics g)
        {
            float w = 780f;
            float h = 330f;
            s.Fill(g, ArcadeColors.Panel, 0f, 0f, w + 12f, h + 12f);
            s.Fill(g, ArcadeColors.Header, 6f, 6f, w, h);
            s.DotBorder(g, ArcadeColors.Accent, 6f, 6f, w, h, 6f);
            s.Write(g, "모두 지우기", s.F(ArcadeFonts.Pixel, 36f, false), ArcadeColors.Accent, 42f, 36f, w - 72f, 54f, 0);
            s.Fill(g, Color.FromArgb(109, 103, 158), 6f, 108f, w, 3f);
            s.Write(g, "명단을 모두 지울까요? 되돌릴 수 없습니다.", s.F(ArcadeFonts.Body, 24f, false), ArcadeColors.Text, 42f, 132f, w - 72f, 60f, 0);
            Font buttonFont = s.F(ArcadeFonts.Pixel, 26f, false);
            float buttonY = 6f + h - 36f - 72f;
            s.Button(g, "confirm-cancel", "취소", buttonFont, Color.FromArgb(0x5d, 0x4b, 0xb0), ArcadeColors.Text, ArcadeColors.Ink, 6f, 6f + w - 36f - 190f, buttonY, 190f, 66f, CloseConfirmPopup);
            s.Button(g, "confirm-ok", "확인", buttonFont, ArcadeColors.Rose, ArcadeColors.Text, Color.FromArgb(0x5d, 0x4b, 0xb0), 6f, 6f + w - 36f - 190f - 18f - 190f, buttonY, 190f, 66f, ClearEntriesConfirmed);
        }

        private void ClearEntriesConfirmed()
        {
            CloseConfirmPopup();
            EndRowEdit();
            _entries.Clear();
            _pending.Clear();
            _expiredGifts.Clear();
            _listScroll = 0f;
            _tokenStickToBottom = true;
            RefreshPinballText();
            RefreshCounts();
            InvalidatePending();
            QueueSave();
        }

        private string ExtractStreamerId(string raw)
        {
            string trimmed = (raw ?? "").Trim();
            if (Regex.IsMatch(trimmed, "^[A-Za-z0-9_\\-]+$"))
            {
                return trimmed;
            }

            string address = trimmed;
            if (!Regex.IsMatch(address, "^[A-Za-z][A-Za-z0-9+\\-.]*://"))
            {
                address = "https://" + address;
            }

            Uri uri;
            if (Uri.TryCreate(address, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                string[] parts = uri.AbsolutePath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                int startIndex = parts.Length > 0 &&
                    String.Equals(parts[0], "station", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                for (int i = startIndex; i < parts.Length; i++)
                {
                    string part = Uri.UnescapeDataString(parts[i]).Trim();
                    if (Regex.IsMatch(part, "^[A-Za-z0-9_\\-]+$") && !Regex.IsMatch(part, "^\\d+$"))
                    {
                        return part;
                    }
                }

                return "";
            }

            return Regex.Replace(trimmed, "[^A-Za-z0-9_\\-]", "");
        }

        private string ResolveDisplayName(string streamerId)
        {
            if (streamerId == null || streamerId.Length == 0)
            {
                return "알 수 없음";
            }

            string key = streamerId.ToLowerInvariant();
            if (key == "mingyeolhee")
            {
                return "민결희";
            }

            return streamerId;
        }

        private string SanitizePinballName(string value)
        {
            if (value == null)
            {
                return "";
            }

            string cleaned = value.Trim();
            cleaned = cleaned.Replace(",", " ");
            cleaned = cleaned.Replace("*", "x");
            cleaned = Regex.Replace(cleaned, "\\s+", " ");
            return cleaned;
        }

        // ───────── 자동 저장과 복원 ─────────
        // 설정, 명단, 기다리는 후원, 메모장을 %LOCALAPPDATA%\Gyeona\GyeolheePinball\State\state.json 에 둔다.
        // 업데이트 캐시(Updates)와는 다른 폴더다.

        private string ResolveStatePath()
        {
            if (HasArgument("--no-state"))
            {
                return null;
            }

            string directory = GetArgumentValue("--state-dir=");
            if (directory.Length == 0)
            {
                directory = Environment.GetEnvironmentVariable("GYEOLHEE_PINBALL_STATE_DIR") ?? "";
            }
            if (directory.Length == 0)
            {
                // 검사 도구가 EXE를 불러다 쓰는 경우와 미리보기 실행에서는 사용자의 저장 내용을 건드리지 않는다.
                bool ownProcess = Object.ReferenceEquals(Assembly.GetEntryAssembly(), Assembly.GetExecutingAssembly());
                bool preview = HasArgument("--preview") || HasArgument("--preview-many") ||
                    GetArgumentValue("--preview-count=").Length > 0 || GetArgumentValue("--stress-count=").Length > 0;
                if (!ownProcess || preview)
                {
                    return null;
                }

                directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Gyeona",
                    "GyeolheePinball",
                    "State");
            }

            return Path.Combine(directory, "state.json");
        }

        private void QueueSave()
        {
            if (_statePath == null || _loadingState)
            {
                return;
            }

            if (!IsHandleCreated)
            {
                SaveStateNow();
                return;
            }

            // 글자를 칠 때마다 쓰지 않도록 잠깐 모았다가 한 번에 쓴다(수집 처리와는 무관하다).
            if (!_saveTimer.Enabled)
            {
                _saveTimer.Start();
            }
        }

        private void SaveStateNow()
        {
            _saveTimer.Stop();
            if (_statePath == null || _loadingState)
            {
                return;
            }

            try
            {
                var rows = new List<object>();
                foreach (CollectedEntry entry in _entries)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    var row = new Dictionary<string, object>();
                    row["name"] = entry.PinballName ?? "";
                    row["nick"] = entry.Nickname ?? "";
                    row["kind"] = (int)entry.Source;
                    row["count"] = entry.BalloonCount;
                    row["coin"] = entry.CoinCount;
                    row["edited"] = entry.CoinEdited;
                    row["time"] = entry.ReceivedAt ?? "";
                    rows.Add(row);
                }

                var waiting = new List<object>();
                foreach (PendingGift gift in _pending)
                {
                    if (gift == null)
                    {
                        continue;
                    }

                    var item = new Dictionary<string, object>();
                    item["nick"] = gift.Nickname ?? "";
                    item["kind"] = (int)gift.Source;
                    item["count"] = gift.BalloonCount;
                    item["coin"] = gift.CoinCount;
                    item["at"] = gift.CreatedAt.ToUniversalTime().Ticks.ToString();
                    waiting.Add(item);
                }

                var settings = new Dictionary<string, object>();
                settings["n"] = _thresholdInput.Value;
                settings["exact"] = _exactMode;
                settings["bonus"] = _bonusPlusOne;
                settings["nickname"] = _nicknamePinballMode;
                settings["star"] = _collectStarBalloon;
                settings["ad"] = _collectAdBalloon;
                settings["challenge"] = _collectChallengeGift;
                settings["battle"] = _collectBattleMission;

                var memo = new Dictionary<string, object>();
                memo["open"] = _memoOpen;
                memo["text"] = _memoText.Text ?? "";
                memo["x"] = _memoX;
                memo["y"] = _memoY;
                memo["w"] = _memoWidth;
                memo["h"] = _memoHeight;
                memo["font"] = _memoFont;

                var root = new Dictionary<string, object>();
                root["version"] = 1;
                root["bj"] = _streamInput.Text ?? "";
                root["liveId"] = _streamerId ?? "";
                root["set"] = settings;
                root["rows"] = rows;
                root["waiting"] = waiting;
                root["memo"] = memo;

                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = Int32.MaxValue;
                string json = serializer.Serialize(root);

                Directory.CreateDirectory(Path.GetDirectoryName(_statePath));
                string temporary = _statePath + ".tmp";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(_statePath))
                {
                    File.Replace(temporary, _statePath, null);
                }
                else
                {
                    File.Move(temporary, _statePath);
                }
            }
            catch
            {
                // 저장에 실패해도 수집은 계속한다.
            }
        }

        private void LoadState()
        {
            if (_statePath == null || !File.Exists(_statePath))
            {
                return;
            }

            _loadingState = true;
            try
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = Int32.MaxValue;
                var root = serializer.DeserializeObject(File.ReadAllText(_statePath, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null)
                {
                    return;
                }

                _streamInput.Text = StateString(root, "bj", "");
                _streamerId = StateString(root, "liveId", "");

                var settings = StateObject(root, "set");
                if (settings != null)
                {
                    _thresholdInput.Value = StateInt(settings, "n", 100);
                    _exactMode = StateBool(settings, "exact", false);
                    _bonusPlusOne = StateBool(settings, "bonus", true);
                    _nicknamePinballMode = StateBool(settings, "nickname", false);
                    _collectStarBalloon = StateBool(settings, "star", true);
                    _collectAdBalloon = StateBool(settings, "ad", true);
                    _collectChallengeGift = StateBool(settings, "challenge", true);
                    _collectBattleMission = StateBool(settings, "battle", true);
                }

                _entries.Clear();
                foreach (object item in StateList(root, "rows"))
                {
                    var row = item as Dictionary<string, object>;
                    if (row == null)
                    {
                        continue;
                    }

                    var entry = new CollectedEntry();
                    entry.PinballName = StateString(row, "name", "");
                    entry.Nickname = StateString(row, "nick", "");
                    entry.Source = StateSource(row);
                    entry.BalloonCount = StateInt(row, "count", 0);
                    entry.CoinCount = StateInt(row, "coin", 0);
                    entry.CoinEdited = StateBool(row, "edited", false);
                    entry.ReceivedAt = StateString(row, "time", "");
                    _entries.Add(entry);
                }

                _pending.Clear();
                DateTime cutoff = DateTime.Now.AddMinutes(-PendingMinutes);
                foreach (object item in StateList(root, "waiting"))
                {
                    var waiting = item as Dictionary<string, object>;
                    if (waiting == null)
                    {
                        continue;
                    }

                    long ticks;
                    if (!Int64.TryParse(StateString(waiting, "at", ""), out ticks))
                    {
                        continue;
                    }

                    DateTime createdAt = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
                    if (createdAt < cutoff || createdAt > DateTime.Now.AddMinutes(1))
                    {
                        continue;
                    }

                    var gift = new PendingGift();
                    gift.Nickname = StateString(waiting, "nick", "");
                    gift.Source = StateSource(waiting);
                    gift.BalloonCount = StateInt(waiting, "count", 0);
                    gift.CoinCount = StateInt(waiting, "coin", 1);
                    gift.CreatedAt = createdAt;
                    _pending.Add(gift);
                }

                var memo = StateObject(root, "memo");
                if (memo != null)
                {
                    _memoText.Text = StateString(memo, "text", "");
                    _memoX = StateInt(memo, "x", MemoDefaultX);
                    _memoY = StateInt(memo, "y", MemoDefaultY);
                    _memoWidth = StateInt(memo, "w", MemoDefaultWidth);
                    _memoHeight = StateInt(memo, "h", MemoDefaultHeight);
                    _memoFont = Math.Max(12, Math.Min(72, StateInt(memo, "font", MemoDefaultFont)));
                    _memoOpen = StateBool(memo, "open", false);
                    _memoPopup.Visible = _memoOpen;
                    if (_memoOpen)
                    {
                        _memoPopup.BringToFront();
                    }
                }

                if (_pending.Count > 0)
                {
                    Shown += delegate { _pendingTimer.Start(); };
                }
            }
            catch
            {
                // 저장 파일이 깨졌으면 빈 상태로 시작한다.
            }
            finally
            {
                _loadingState = false;
            }
        }

        private static Dictionary<string, object> StateObject(Dictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static IEnumerable StateList(Dictionary<string, object> source, string key)
        {
            object value;
            if (source.TryGetValue(key, out value) && value is IEnumerable && !(value is string))
            {
                return (IEnumerable)value;
            }

            return new object[0];
        }

        private static string StateString(Dictionary<string, object> source, string key, string fallback)
        {
            object value;
            return source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }

        private static int StateInt(Dictionary<string, object> source, string key, int fallback)
        {
            int parsed;
            return Int32.TryParse(StateString(source, key, ""), out parsed) ? parsed : fallback;
        }

        private static bool StateBool(Dictionary<string, object> source, string key, bool fallback)
        {
            object value;
            if (source.TryGetValue(key, out value) && value is bool)
            {
                return (bool)value;
            }

            return fallback;
        }

        private static GiftSource StateSource(Dictionary<string, object> source)
        {
            int kind = StateInt(source, "kind", 0);
            return Enum.IsDefined(typeof(GiftSource), kind) ? (GiftSource)kind : GiftSource.StarBalloon;
        }

        // ───────── 실행 인자, 미리보기 ─────────

        private bool HasArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetArgumentValue(string prefix)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i].Substring(prefix.Length).Trim();
                }
            }

            return "";
        }

        private int GetArgumentInt(string prefix, int fallback, int max)
        {
            string value = GetArgumentValue(prefix);
            int parsed;
            if (!Int32.TryParse(value, out parsed))
            {
                return fallback;
            }

            return Math.Max(0, Math.Min(max, parsed));
        }

        // 업데이트 창이 어떻게 뜨는지 보기만 하는 미리보기. 실제로 내려받거나 교체하지 않는다.
        private void ShowUpdatePreview()
        {
            Version current = Assembly.GetExecutingAssembly().GetName().Version;
            var next = new Version(current.Major, current.Minor, current.Build, current.Revision + 1);
            DialogResult answer;
            using (var prompt = new UpdatePromptDialog(next, "(미리보기) 여기에 업데이트 내용이 표시됩니다.\r\n실제로 업데이트하지 않습니다."))
            {
                answer = prompt.ShowDialog(this);
            }
            if (answer != DialogResult.OK)
            {
                return;
            }

            var progress = new UpdateProgressDialog();
            progress.Show(this);
            int value = 0;
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 40;
            timer.Tick += delegate
            {
                value += 2;
                progress.SetProgress(value);
                if (value >= 100)
                {
                    timer.Stop();
                    timer.Dispose();
                    progress.Close();
                    progress.Dispose();
                }
            };
            timer.Start();
        }

        private void SeedPreviewRows()
        {
            _entries.Add(new CollectedEntry { Source = GiftSource.StarBalloon, Nickname = "테스트", BalloonCount = 100, CoinCount = 1, PinballName = "테스트", ReceivedAt = DateTime.Now.ToString("HH:mm:ss") });
            _entries.Add(new CollectedEntry { Source = GiftSource.AdBalloon, Nickname = "결희", BalloonCount = 1017, CoinCount = 11, PinballName = "결희 종겜핀볼", ReceivedAt = DateTime.Now.ToString("HH:mm:ss") });
            _entries.Add(new CollectedEntry { Source = GiftSource.ChallengeGift, Nickname = "시소즈", BalloonCount = 200, CoinCount = 2, PinballName = "테스트2", ReceivedAt = DateTime.Now.ToString("HH:mm:ss") });
            _entries.Add(new CollectedEntry { Source = GiftSource.BattleMission, Nickname = "대결", BalloonCount = 5000, CoinCount = 55, PinballName = "대결미션 테스트", ReceivedAt = DateTime.Now.ToString("HH:mm:ss") });
        }

        private void SeedManyPreviewRows(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                int balloons = i == 1 ? 100 : i * 100;
                _entries.Add(new CollectedEntry
                {
                    Source = (GiftSource)((i - 1) % 4),
                    Nickname = "테스트" + i,
                    BalloonCount = balloons,
                    CoinCount = CalculateCoins(balloons),
                    PinballName = "종겜핀볼 테스트 " + i,
                    ReceivedAt = DateTime.Now.ToString("HH:mm:ss")
                });
            }
        }

        private void SeedPendingPreview()
        {
            _pending.Add(new PendingGift { Source = GiftSource.StarBalloon, Nickname = "기다리는사람", BalloonCount = 1000, CoinCount = 11, CreatedAt = DateTime.Now.AddMinutes(-2) });
            _pending.Add(new PendingGift { Source = GiftSource.BattleMission, Nickname = "대결후원", BalloonCount = 300, CoinCount = 3, CreatedAt = DateTime.Now.AddSeconds(-570) });
            _pending.Add(new PendingGift { Source = GiftSource.AdBalloon, Nickname = "곧지남", BalloonCount = 100, CoinCount = 1, CreatedAt = DateTime.Now.AddSeconds(-597) });
            InvalidatePending();
        }

        private void AddStressEntries(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                int balloons = i == 1 ? 100 : i * 100;
                var entry = new CollectedEntry();
                entry.Source = (GiftSource)((i - 1) % 4);
                entry.Nickname = "테스트" + i;
                entry.BalloonCount = balloons;
                entry.CoinCount = CalculateCoins(balloons);
                entry.PinballName = "종겜핀볼 스트레스 " + i;
                entry.ReceivedAt = DateTime.Now.ToString("HH:mm:ss");
                AddCollectedEntry(entry);
            }
        }
    }
}
