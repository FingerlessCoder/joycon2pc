
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Joycon2PC.App.Bluetooth;
using Joycon2PC.Core;

namespace Joycon2PC.App
{
    public sealed partial class MainForm : Form
    {
        // ======================================================================================================================================
        //  SCALE  (issue #16)
        //
        //  Every box in this window used to be a hardcoded pixel while the fonts were in points,
        //  so at 150% the text grew and the layout did not. There is now exactly one scale
        //  source - _uiScale - and every design number below is pushed through S() before it
        //  reaches a control. Fonts stay in points because those already follow the monitor DPI.
        // ======================================================================================================================================
        private float _uiScale = 1f;
        private bool _metricsDirty = true;
        private UiTheme? _theme;

        /// <summary>Non-null view of the theme. It is created first thing in the constructor and disposed last.</summary>
        private UiTheme T => _theme!;

        private int S(int designPx) => (int)Math.Round(designPx * _uiScale, MidpointRounding.AwayFromZero);

        private static Color Hex(int rgb) => UiTheme.Hex(rgb);

        // ------ design constants, all in 96-dpi design pixels -----------------------------------------------------------------------
        private const int DESIGN_W = 1040;
        private const int DESIGN_H = 780;
        private const int MIN_W = 880;
        private const int MIN_H = 604;

        private const int MARGIN = 20;       // window margin
        private const int GAP = 12;          // band gap
        private const int HEAD_H = 52;       // header band
        private const int CARD_H = 60;       // status card height
        private const int STAGE_H = 30;      // connect stage row
        private const int ACT_H = 46;        // primary action row
        private const int OPT_H = 110;       // options card
        private const int BAR_H = 24;        // bottom telemetry bar
        private const int CONTENT_MIN_H = 168;

        // ======================================================================================================================================
        //  PALETTE
        //  Kept under the historical names because MainForm.Pipeline.cs logs with them.
        // ======================================================================================================================================
        private static readonly Color BG            = Hex(0x0C0E12);
        private static readonly Color PANEL         = Hex(0x14171D);
        private static readonly Color PANEL_ALT     = Hex(0x0E1116);
        private static readonly Color ACCENT        = Hex(0x2ED9C3);
        private static readonly Color GREEN         = Hex(0x3FD99C);
        private static readonly Color RED           = Hex(0xFF6B6B);
        private static readonly Color YELLOW        = Hex(0xF5B942);
        private static readonly Color TXT           = Hex(0xE9ECF3);
        private static readonly Color TXT_DIM       = Hex(0x7C8697);
        private static readonly Color BORDER        = Hex(0x252B36);
        private static readonly Color BTN_PRIMARY   = Hex(0x12A594);
        private static readonly Color BTN_STOP      = Hex(0xD6453F);
        private static readonly Color BTN_SECONDARY = Hex(0x1C212A);
        private static readonly Color STICK_BG      = Hex(0x0B0D11);

        private enum LogMode
        {
            User,
            Developer,
        }

        private enum LogAudience
        {
            User,
            Developer,
        }

        private sealed class LogEntry
        {
            public required string TimeText { get; init; }
            public required string Message { get; init; }
            public required Color Color { get; init; }
            public required LogAudience Audience { get; init; }
        }

        private sealed class ByteOption
        {
            public required string Label { get; init; }
            public required byte Value { get; init; }

            public override string ToString() => Label;
        }

        private sealed class DeviceTargetOption
        {
            public required string Label { get; init; }
            public string? DeviceId { get; init; }

            public override string ToString() => Label;
        }

        // ------ runtime state -----------------------------------------------------------------------------------------------------------
        private JoyconState _lastState = new();
        private bool _running   = false;
        private CancellationTokenSource? _cts;

        private Joycon2PC.ViGEm.ViGEmBridge? _bridge;
        private JoyconParser _parser = new();
        private BLEScanner? _scanner;  // active scanner - used by Reconnect button
        private bool _powerEventsSubscribed;
        private LogMode _logMode = LogMode.User;
        private readonly List<LogEntry> _logEntries = new();
        private const int MAX_LOG_ENTRIES = 500;
        private const int MAX_LOG_LINES = 300;

        // ------ controls --------------------------------------------------------------------------------------------------------------
        private Label  _lblVigemStatus  = null!;
        private Label  _lblJoyconStatus = null!;
        private ThemedButton _btnStart        = null!;
        private ThemedButton _btnReconnect    = null!;
        private ThemedButton _btnTestSound    = null!;
        private ThemedButton _btnApplyLed     = null!;
        private ThemedCheckBox _chkConnectSound = null!;
        private ThemedToggle _chkMouseMode = null!;
        private ThemedCheckBox _chkShowDevLogs = null!;
        private ThemedButton _btnDiagCapture = null!;
        private ThemedButton _btnExportLog = null!;
        private ThemedButton _btnCopyLogSummary = null!;
        private int _copySummaryFeedbackVersion;
        private Label _lblLinkHealth = null!;
        private Label _lblConnectModeStatus = null!;
        private StageChip _chipStageScan = null!;
        private StageChip _chipStageInit = null!;
        private StageChip _chipStageReady = null!;
        private ThemedProgressBar _prgConnectStage = null!;
        private ThemedDropDown _cmbMouseStabilizer = null!;
        private ThemedDropDown _cmbMouseSpeed = null!;
        private ThemedDropDown _cmbDeviceTarget = null!;
        private ThemedDropDown _cmbSoundPreset = null!;
        private ThemedDropDown _cmbLedPattern = null!;
        private RichTextBox _log = null!;
        private ToolTip _tips = null!;

        // ------ layout handles ---------------------------------------------------------------------------------------------------------
        private AppHeader    _header    = null!;
        private StatusCard   _cardVigem = null!;
        private StatusCard   _cardLink  = null!;
        private CardSurface  _vizCard   = null!;
        private CardSurface  _logCard   = null!;
        private CardSurface  _optCard   = null!;
        private StatusBarPanel _statusBar = null!;
        private TableLayoutPanel _optGrid = null!;
        private bool _mouseModeEnabled;
        private readonly object _mouseStateLock = new();
        private bool _mouseFirstOpticalRead = true;
        private short _mouseLastOpticalX;
        private short _mouseLastOpticalY;
        private bool _mouseLeftPressed;
        private bool _mouseRightPressed;
        private bool _mouseMiddlePressed;
        private double _mousePendingMoveX;
        private double _mousePendingMoveY;
        private double _mouseFilteredDx;
        private double _mouseFilteredDy;
        private double _mouseScrollAccumulator;
        private DateTime _mouseLastOpticalReportUtc = DateTime.MinValue;
        private DateTime _mouseLastMoveUtc = DateTime.MinValue;
        private int _mouseMiddleStableCount;
        private bool _mouseMiddleDebouncedPressed;
        private const int MOUSE_WHEEL_DELTA = 120;
        private const int MOUSE_SCROLL_STICK_DEADZONE = 220;
        private const double MOUSE_SCROLL_GAIN = 1.0 / 8000.0;

        private enum MouseSpeedMode
        {
            Fast,
            Normal,
            Slow,
        }

        private enum MouseStabilizerMode
        {
            Raw,
            Stable,
            VeryStable,
        }

        private MouseSpeedMode _mouseSpeedMode = MouseSpeedMode.Normal;
        private MouseStabilizerMode _mouseStabilizerMode = MouseStabilizerMode.Stable;
        private const int CONNECT_FEEDBACK_PLAYER_NUM = 1;
        private bool _reconnectInProgress;
        private DateTime _lastReconnectRequestUtc = DateTime.MinValue;
        private DateTime _lastInputReportUtc = DateTime.MinValue;
        private DateTime _lastReadyUtc = DateTime.MinValue;
        private int _reconnectAttemptCount;
        private System.Windows.Forms.Timer? _healthTimer;
        private readonly object _diagLock = new();
        private readonly Queue<double> _diagIntervalSamplesMs = new();
        private readonly Queue<double> _diagProcessSamplesMs = new();
        private readonly Dictionary<string, DeviceDiagnosticTracker> _diagByDevice = new();
        private DiagnosticSnapshot? _lastCompletedDiagnosticSnapshot;
        private DateTime _lastCompletedDiagCapturedLocal = DateTime.MinValue;
        private DateTime _lastCompletedDiagCaptureStartUtc = DateTime.MinValue;
        private long _diagTotalReports;
        private long _diagDecodeFailures;
        private long _diagInvalidSizeReports;
        private long _diagLastArrivalTicks;
        private bool _diagCaptureActive;
        private DateTime _diagCaptureStartUtc = DateTime.MinValue;
        private int _diagCaptureVersion;
        private static readonly TimeSpan DIAG_CAPTURE_DURATION = TimeSpan.FromSeconds(30);
        private const int DIAG_MAX_SAMPLES = 4096;
        private const double DIAG_SPIKE_40_MS = 40;
        private const double DIAG_SPIKE_60_MS = 60;
        private const double DIAG_SPIKE_100_MS = 100;
        private static readonly TimeSpan RECONNECT_MIN_INTERVAL = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan RECONNECT_SETTLE_WIN10 = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan RECONNECT_SETTLE_WIN11 = TimeSpan.FromMilliseconds(380);

        private JoyConVisualizerPanel _joyconViz = null!;

        public MainForm()
        {
            _theme = new UiTheme();
            InitUI();
            _parser.StateChanged += OnStateChanged;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerEventsSubscribed = true;
        }

        // ======================================================================================================================================
        //  UI CONSTRUCTION
        // ======================================================================================================================================
        private void InitUI()
        {
            Text            = "Joycon2PC";
            BackColor       = BG;
            ForeColor       = TXT;
            Font            = T.FBody;
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            AutoScaleMode   = AutoScaleMode.None;   // we own scaling - see S()
            KeyPreview      = true;

            _tips = new ToolTip
            {
                AutoPopDelay = 9000,
                InitialDelay = 420,
                ReshowDelay = 120,
            };

            BuildUi();
            ApplyStartupSize();

            Resize += (sender, args) =>
            {
                if (_metricsDirty)
                    ApplyMetrics();
                ApplyLayout();
            };

            ApplyMetrics();
            ApplyLayout();
            SyncHeaderState();
        }

        private void BuildUi()
        {
            // ------ header: wordmark + tagline + session pill (painted, no children) -----------
            _header = new AppHeader();
            Attach(_header);
            Controls.Add(_header);

            // ------ status band: two headline readouts + a connect stage rail ----------
            _cardVigem = new StatusCard { Caption = "Virtual controller" };
            Attach(_cardVigem);
            Controls.Add(_cardVigem);

            _cardLink = new StatusCard { Caption = "Joy-Con link" };
            Attach(_cardLink);
            Controls.Add(_cardLink);

            // The value text is painted by the card itself, so these labels stay invisible
            // children - but MainForm.Pipeline.cs keeps writing Text/ForeColor on them, and
            // ForeColor is what the card uses to colour its dot.
            _lblVigemStatus = MakeStatusValue("Checking driver...", TXT_DIM);
            _cardVigem.ValueLabel = _lblVigemStatus;
            _lblJoyconStatus = MakeStatusValue("Not connected", TXT_DIM);
            _cardLink.ValueLabel = _lblJoyconStatus;

            _chipStageScan = MakeChip("Scan");
            _chipStageInit = MakeChip("Init");
            _chipStageReady = MakeChip("Ready");

            _prgConnectStage = new ThemedProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
            Attach(_prgConnectStage);
            Controls.Add(_prgConnectStage);

            _lblConnectModeStatus = MakeBodyLabel("Pair state: Idle", TXT_DIM);
            _lblConnectModeStatus.BackColor = BG;   // it sits on the window canvas, not a card
            _lblConnectModeStatus.AutoEllipsis = true;
            Controls.Add(_lblConnectModeStatus);

            // ------ content band: visualiser left, log right ----------
            _vizCard = new CardSurface();
            Attach(_vizCard);
            Controls.Add(_vizCard);

            _joyconViz = new JoyConVisualizerPanel { Dock = DockStyle.Fill, BackColor = PANEL };
            _joyconViz.Metrics.Theme = _theme;
            _vizCard.Controls.Add(_joyconViz);

            var vizCaption = new SectionCaption { Text = "Controller", Dock = DockStyle.Top, ShowRule = false };
            Attach(vizCaption);
            _vizCard.Controls.Add(vizCaption);

            _logCard = new CardSurface();
            Attach(_logCard);
            Controls.Add(_logCard);

            _log = new RichTextBox
            {
                BackColor   = PANEL_ALT,
                ForeColor   = TXT,
                Font        = new Font("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                Dock        = DockStyle.Fill,
                WordWrap    = false,
                DetectUrls  = false,
            };
            _logCard.Controls.Add(_log);
            _tips.SetToolTip(_log, "Live event log. Timestamped and colour coded - export or copy it when reporting a problem.");

            var logActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = PANEL,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            _logCard.Controls.Add(logActions);

            var logCaption = new SectionCaption { Text = "Activity", Dock = DockStyle.Top, ShowRule = false };
            Attach(logCaption);
            _logCard.Controls.Add(logCaption);

            _chkShowDevLogs = new ThemedCheckBox { Text = "Dev details", Margin = new Padding(0, 0, 10, 0) };
            Attach(_chkShowDevLogs);
            _chkShowDevLogs.CheckedChanged += (sender, args) =>
            {
                _logMode = _chkShowDevLogs.Checked ? LogMode.Developer : LogMode.User;
                RebuildLogView();
            };
            _tips.SetToolTip(_chkShowDevLogs, "Also show developer lines: raw report dumps, stick traces, input-mode retries.");
            logActions.Controls.Add(_chkShowDevLogs);

            _btnDiagCapture = MakeButton("Diag 30s", ButtonSkin.Ghost, Glyph.Activity);
            _btnDiagCapture.Click += async (sender, args) => await StartDiagnosticCaptureAsync();
            _tips.SetToolTip(_btnDiagCapture, "Record 30 seconds of report timing, then report p50/p95/p99, jitter and drop rate.");
            logActions.Controls.Add(_btnDiagCapture);

            _btnExportLog = MakeButton("Export log", ButtonSkin.Ghost, Glyph.Download);
            _btnExportLog.Click += (sender, args) => ExportLogToFile();
            _tips.SetToolTip(_btnExportLog, "Write the full log plus the current diagnostics to a text file.");
            logActions.Controls.Add(_btnExportLog);

            _btnCopyLogSummary = MakeButton("Copy issues", ButtonSkin.Ghost, Glyph.Copy);
            _btnCopyLogSummary.Click += async (sender, args) => await CopyRecentIssueSummaryToClipboardAsync();
            _tips.SetToolTip(_btnCopyLogSummary, "Copy a diagnostics summary plus recent warnings to the clipboard - ideal for a bug report.");
            logActions.Controls.Add(_btnCopyLogSummary);

            // ------ primary action band ----------
            _btnStart = MakeButton("Connect Joy-Cons", ButtonSkin.Solid, Glyph.Link);
            _btnStart.BackColor = BTN_PRIMARY;
            _btnStart.ForeColor = Color.White;
            _btnStart.Font = T.FButtonBig;
            _btnStart.IconSize = 17;
            _btnStart.Radius = UiTheme.RadiusField;
            _btnStart.Click += OnStartClicked;
            _tips.SetToolTip(_btnStart, "Start scanning for Joy-Con 2 controllers and feed them to the virtual Xbox 360 controller.");
            Controls.Add(_btnStart);

            _btnReconnect = MakeButton("Reconnect", ButtonSkin.Soft, Glyph.Refresh);
            _btnReconnect.BackColor = BTN_SECONDARY;
            _btnReconnect.ForeColor = TXT;
            _btnReconnect.Font = T.FButton;
            _btnReconnect.Click += (s, e) => OnReconnectClicked();
            _tips.SetToolTip(_btnReconnect, "Force a full Bluetooth LE session restart - use this when a controller goes quiet.");
            Controls.Add(_btnReconnect);

            // ------ options card: four labelled field groups in a 2x2 grid ----------
            _optCard = new CardSurface();
            Attach(_optCard);
            Controls.Add(_optCard);

            _optGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = PANEL,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            _optGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _optGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _optCard.Controls.Add(_optGrid);

            var optCaption = new SectionCaption { Text = "Options", Dock = DockStyle.Top, ShowRule = false };
            Attach(optCaption);
            _optCard.Controls.Add(optCaption);

            _cmbDeviceTarget = MakeCombo();
            _optGrid.Controls.Add(Field("Target", _cmbDeviceTarget), 0, 0);
            _tips.SetToolTip(_cmbDeviceTarget, "Which controller receives sound and LED feedback. 'Connected setup' covers both halves of a pair.");

            _chkMouseMode = new ThemedToggle { Text = "Mouse mode", Checked = false };
            Attach(_chkMouseMode);
            _chkMouseMode.CheckedChanged += (sender, args) =>
            {
                lock (_mouseStateLock)
                {
                    _mouseModeEnabled = _chkMouseMode.Checked;
                    ResetMouseModeState(releasePressedButtons: true);
                }
                Log($"Mouse mode {(_mouseModeEnabled ? "enabled" : "disabled") }.", ACCENT);
            };
            _tips.SetToolTip(_chkMouseMode, "Turn the right Joy-Con into a mouse.\nR = left click    ZR = right click    RS = middle click\nRight stick up/down = scroll wheel");

            _cmbMouseStabilizer = MakeCombo();
            _cmbMouseSpeed = MakeCombo();
            _optGrid.Controls.Add(Field("Pointer", _chkMouseMode, _cmbMouseStabilizer, _cmbMouseSpeed), 0, 1);
            _tips.SetToolTip(_cmbMouseStabilizer, "How hard optical-mouse deltas get filtered. VeryStable trades a little latency for a still cursor.");
            _tips.SetToolTip(_cmbMouseSpeed, "Multiplier on optical-mouse deltas. Normal matches the reference implementation.");

            _cmbSoundPreset = MakeCombo();
            _btnTestSound = MakeButton("Test", ButtonSkin.Soft, Glyph.Speaker);
            _btnTestSound.Click += async (sender, args) => await TriggerManualSoundTestAsync();
            _tips.SetToolTip(_btnTestSound, "Send the selected sound preset to the controller as a one-off test.");
            _optGrid.Controls.Add(Field("Sound", _cmbSoundPreset, _btnTestSound), 1, 0);

            _cmbLedPattern = MakeCombo();
            _btnApplyLed = MakeButton("Apply", ButtonSkin.Soft, Glyph.Bulb);
            _btnApplyLed.Click += async (sender, args) => await TriggerManualLedApplyAsync();
            _tips.SetToolTip(_btnApplyLed, "Set the player indicator lights to the selected pattern.");
            _chkConnectSound = new ThemedCheckBox { Text = "On connect", Checked = true };
            Attach(_chkConnectSound);
            _tips.SetToolTip(_chkConnectSound, "Play the selected sound automatically once a controller is ready.");
            _optGrid.Controls.Add(Field("Player LED", _cmbLedPattern, _btnApplyLed, _chkConnectSound), 1, 1);

            // ------ bottom telemetry bar ----------
            _statusBar = new StatusBarPanel { Caption = "LINK" };
            Attach(_statusBar);
            _statusBar.BackColor = PANEL_ALT;
            Controls.Add(_statusBar);

            _lblLinkHealth = MakeBodyLabel("Waiting for first input report...", TXT_DIM);
            _lblLinkHealth.BackColor = PANEL_ALT;   // match the sunken bar it sits in
            _lblLinkHealth.AutoEllipsis = true;
            _lblLinkHealth.TextAlign = ContentAlignment.MiddleLeft;
            _statusBar.Controls.Add(_lblLinkHealth);
            _tips.SetToolTip(_statusBar, "Report freshness and timing: age of the last input report, time since the controller was ready, reconnect count, and p95/p99/jitter of the input pipeline.");

            // These labels are not in any control tree (their card draws the text), so they
            // never raise Paint. TextChanged is the reliable signal that a status update -
            // Text and ForeColor always land together - has arrived.
            _lblJoyconStatus.TextChanged += (s, e) =>
            {
                SyncHeaderState();
                _cardLink.Invalidate();
            };
            _lblVigemStatus.TextChanged += (s, e) => _cardVigem.Invalidate();
            _lblLinkHealth.TextChanged += (s, e) => _statusBar.Hue =
                _lblLinkHealth.ForeColor.ToArgb() == 0 ? TXT_DIM : _lblLinkHealth.ForeColor;

            InitializeOptionControls();

            // kick off ViGEm check
            _ = Task.Run(CheckViGEmAsync);

            _healthTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _healthTimer.Tick += (sender, args) =>
            {
                UpdateLinkHealthSummary();
                _cardLink.Invalidate();
                _cardVigem.Invalidate();
            };
            _healthTimer.Start();
        }

        // ======================================================================================================================================
        //  CONTROL FACTORIES  - one place where theme and DPI are attached
        // ======================================================================================================================================
        private void Attach(ThemedControl c)
        {
            c.Metrics.Theme = _theme;
            c.Metrics.Scale = _uiScale;
            c.BackColor = PANEL;
            c.ForeColor = TXT;
        }

        private void Attach(ThemedPanel p)
        {
            p.Metrics.Theme = _theme;
            p.Metrics.Scale = _uiScale;
            p.BackColor = PANEL;
            p.ForeColor = TXT;
        }

        private void Attach(ThemedButton b)
        {
            b.Metrics.Theme = _theme;
            b.Metrics.Scale = _uiScale;
            b.BackColor = BTN_SECONDARY;
            b.ForeColor = TXT;
        }

        private void Attach(ThemedCheckBox c)
        {
            c.Metrics.Theme = _theme;
            c.Metrics.Scale = _uiScale;
            c.BackColor = PANEL;
            c.ForeColor = TXT;
        }

        private void Attach(ThemedToggle c)
        {
            c.Metrics.Theme = _theme;
            c.Metrics.Scale = _uiScale;
            c.BackColor = PANEL;
            c.ForeColor = TXT;
        }

        private void Attach(ThemedDropDown c)
        {
            c.Metrics.Theme = _theme;
            c.Metrics.Scale = _uiScale;
            c.BackColor = PANEL;
            c.ForeColor = TXT;
        }

        private void Attach(ThemedProgressBar c)
        {
            c.Metrics.Theme = _theme;
            c.Metrics.Scale = _uiScale;
        }

        private ThemedButton MakeButton(string text, ButtonSkin skin, Glyph icon)
            => new()
            {
                Text = text,
                Skin = skin,
                Icon = icon,
                Font = T.FButton,
                Margin = new Padding(0),
                Metrics = { Theme = _theme, Scale = _uiScale },
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
            };

        private ThemedDropDown MakeCombo()
            => new()
            {
                Font = T.FSmall,
                Margin = new Padding(0),
                Metrics = { Theme = _theme, Scale = _uiScale },
                BackColor = PANEL,
                ForeColor = TXT,
            };

        private StageChip MakeChip(string text)
        {
            var chip = new StageChip
            {
                Text = text,
                Metrics = { Theme = _theme, Scale = _uiScale },
                BackColor = PANEL,
            };
            Controls.Add(chip);
            return chip;
        }

        private Label MakeBodyLabel(string text, Color color)
            => new()
            {
                Text = text,
                AutoSize = false,
                BackColor = PANEL,
                ForeColor = color,
                Font = T.FSmall,
            };

        /// <summary>A status value is drawn by its card, so the label itself never paints.</summary>
        private static Label MakeStatusValue(string text, Color color)
            => new()
            {
                Text = text,
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = color,
            };

        /// <summary>A mini caption followed by its controls, laid out left to right inside a table cell.</summary>
        private FlowLayoutPanel Field(string caption, params Control[] items)
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = PANEL,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };

            var lbl = new Label
            {
                Text = caption,
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = TXT_DIM,
                Font = T.FSmallBold,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 10, 0),
            };
            flow.Controls.Add(lbl);
            foreach (Control c in items)
            {
                c.Margin = new Padding(0, 0, 6, 0);
                flow.Controls.Add(c);
            }
            return flow;
        }

        // ======================================================================================================================================
        //  SIZING  (ApplyMetrics: sizes, run when DPI changes)
        // ======================================================================================================================================
        /// <summary>Picks the initial client size, clamped to the monitor it opens on.</summary>
        private void ApplyStartupSize()
        {
            if (IsHandleCreated)
                return;

            _uiScale = 1f;
            try
            {
                using var g = CreateGraphics();
                float dpi = g.DpiX > 0 ? g.DpiX : 96f;
                _uiScale = dpi / 96f;
            }
            catch
            {
                _uiScale = 1f;
            }

            ClientSize = new Size(S(DESIGN_W), S(DESIGN_H));
        }

        private void ApplyMetrics()
        {
            _metricsDirty = false;

            Size minClient = new(S(MIN_W), S(MIN_H));
            Rectangle working = Screen.FromControl(this).WorkingArea;

            if (ClientSize.Width < minClient.Width || ClientSize.Height < minClient.Height)
                ClientSize = minClient;
            if (ClientSize.Width > working.Width - S(24))
                ClientSize = new Size(Math.Max(minClient.Width, working.Width - S(24)), ClientSize.Height);
            if (ClientSize.Height > working.Height - S(24))
                ClientSize = new Size(ClientSize.Width, Math.Max(minClient.Height, working.Height - S(24)));

            ApplyMinimumSize(minClient);

            // push the new scale into every themed control
            foreach (Control c in Controls) Rescale(c);
            foreach (Control c in _vizCard.Controls) Rescale(c);
            foreach (Control c in _logCard.Controls) Rescale(c);
            foreach (Control c in _optCard.Controls) Rescale(c);
            foreach (Control c in _statusBar.Controls) Rescale(c);

            // header
            _header.Height = S(HEAD_H);

            // status band
            int chipW = S(62), chipH = S(22);
            _chipStageScan.Size = new Size(chipW, chipH);
            _chipStageInit.Size = new Size(chipW, chipH);
            _chipStageReady.Size = new Size(chipW, chipH);
            _lblConnectModeStatus.Font = T.FSmall;
            _lblConnectModeStatus.Size = new Size(S(210), S(18));

            // primary action
            _btnStart.Size = new Size(S(216), S(ACT_H));
            _btnReconnect.Size = new Size(S(152), S(ACT_H));

            // option fields
            int rowH = Math.Max(S(24), (S(OPT_H) - S(16) - S(20)) / 2 - S(3));
            int comboH = Math.Min(rowH, S(26));
            int smallH = Math.Min(rowH, S(24));

            _cmbDeviceTarget.Size = new Size(S(214), comboH);
            _cmbMouseStabilizer.Size = new Size(S(110), comboH);
            _cmbMouseSpeed.Size = new Size(S(100), comboH);
            _cmbSoundPreset.Size = new Size(S(170), comboH);
            _cmbLedPattern.Size = new Size(S(124), comboH);

            _btnTestSound.Size = new Size(S(66), smallH);
            _btnApplyLed.Size = new Size(S(66), smallH);
            _btnTestSound.IconSize = 12;
            _btnApplyLed.IconSize = 12;
            _btnTestSound.Font = T.FSmallBold;
            _btnApplyLed.Font = T.FSmallBold;

            _chkMouseMode.Size = new Size(S(30) + S(9) + S(66), smallH);
            _chkMouseMode.Font = T.FSmall;
            _chkConnectSound.Size = new Size(S(102), smallH);
            _chkConnectSound.Font = T.FSmall;
            _chkShowDevLogs.Size = new Size(S(80), S(24));
            _chkShowDevLogs.Font = T.FSmall;

            // option grid: keep the caption on the same optical line as its controls
            _optGrid.RowStyles.Clear();
            _optGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            _optGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            for (int c = 0; c < _optGrid.ColumnCount; c++)
                for (int r = 0; r < _optGrid.RowCount; r++)
                    if (_optGrid.GetControlFromPosition(c, r) is FlowLayoutPanel cell && cell.Controls.Count > 0
                        && cell.Controls[0] is Label cap)
                        cap.Size = new Size(S(66), comboH);

            // log header actions
            foreach (ThemedButton b in new[] { _btnDiagCapture, _btnExportLog, _btnCopyLogSummary })
            {
                b.Font = T.FSmallBold;
                b.IconSize = 11;
                b.Size = new Size(S(86), S(24));
                b.Margin = new Padding(0, 0, 8, 0);
            }
            _btnCopyLogSummary.Size = new Size(S(92), S(24));
            _chkShowDevLogs.Margin = new Padding(0, 0, 12, 0);
            if (_logCard.Controls.Count > 0)
                foreach (Control c in _logCard.Controls)
                    if (c is FlowLayoutPanel fa)
                        fa.Height = S(26);

            // telemetry bar
            _statusBar.Height = S(BAR_H);
            _statusBar.Padding = new Padding(S(26), 0, S(10), 0);
            _lblLinkHealth.Font = T.FMono;
            _lblLinkHealth.Margin = new Padding(0);

            // card padding + section captions
            _vizCard.Padding = new Padding(S(12), S(11), S(12), S(10));
            _logCard.Padding = new Padding(S(12), S(10), S(12), S(12));
            _optCard.Padding = new Padding(S(12), S(9), S(12), S(10));
            foreach (CardSurface card in new CardSurface[] { _vizCard, _logCard, _optCard })
            {
                var caption = FindCaption(card);
                if (caption == null) continue;
                caption.Height = S(16);
                caption.Margin = new Padding(0, 0, 0, S(5));
            }
        }

        private void ApplyMinimumSize(Size minClient)
        {
            // Size is the whole window, ClientSize is the client area: add the frame back on or
            // the minimum silently fights the layout.
            int frameW = Math.Max(0, Bounds.Width - ClientSize.Width);
            int frameH = Math.Max(0, Bounds.Height - ClientSize.Height);
            Size wanted = new(minClient.Width + frameW, minClient.Height + frameH);
            if (MinimumSize != wanted)
                MinimumSize = wanted;
        }

        private static SectionCaption? FindCaption(Control parent)
        {
            foreach (Control c in parent.Controls)
                if (c is SectionCaption sc)
                    return sc;
            return null;
        }

        private void Rescale(Control c)
        {
            switch (c)
            {
                case ThemedControl t: t.Metrics.Scale = _uiScale; break;
                case ThemedPanel p: p.Metrics.Scale = _uiScale; break;
                case ThemedButton b: b.Metrics.Scale = _uiScale; break;
                case ThemedCheckBox cb: cb.Metrics.Scale = _uiScale; break;
                case ThemedToggle tg: tg.Metrics.Scale = _uiScale; break;
                case ThemedProgressBar pb: pb.Metrics.Scale = _uiScale; break;
            }
        }

        // ======================================================================================================================================
        //  LAYOUT  (positions only, runs on every resize)
        // ======================================================================================================================================
        private void ApplyLayout()
        {
            int margin = S(MARGIN);
            int gap = S(GAP);
            int width = Math.Max(S(320), ClientSize.Width - margin * 2);
            int left = margin;

            // ---- header ----
            _header.Bounds = new Rectangle(left, margin, width, S(HEAD_H));
            int y = margin + S(HEAD_H) + gap;

            // ---- status band ----
            int cardW = (width - gap) / 2;
            _cardVigem.Bounds = new Rectangle(left, y, cardW, S(CARD_H));
            _cardLink.Bounds = new Rectangle(left + cardW + gap, y, cardW, S(CARD_H));

            int stageY = y + S(CARD_H) + gap;
            int chipGap = S(6);
            int chipTop = stageY + (S(STAGE_H) - _chipStageScan.Height) / 2;
            _chipStageScan.Location = new Point(left, chipTop);
            _chipStageInit.Location = new Point(left + _chipStageScan.Width + chipGap, chipTop);
            _chipStageReady.Location = new Point(left + (_chipStageScan.Width + chipGap) * 2, chipTop);

            int pairW = _lblConnectModeStatus.Width;
            int progLeft = _chipStageReady.Right + S(10);
            int progW = Math.Max(S(60), left + width - pairW - S(12) - progLeft);
            _prgConnectStage.Bounds = new Rectangle(progLeft, stageY + S(STAGE_H) / 2 - S(2), progW, S(4));
            _lblConnectModeStatus.Bounds = new Rectangle(
                left + width - pairW, stageY + (S(STAGE_H) - _lblConnectModeStatus.Height) / 2, pairW, S(18));

            y += S(CARD_H) + gap + S(STAGE_H) + gap;

            // telemetry bar pinned to the bottom edge; the label starts clear of the "LINK" tag
            _statusBar.Bounds = new Rectangle(left, ClientSize.Height - margin - S(BAR_H), width, S(BAR_H));
            _lblLinkHealth.Bounds = new Rectangle(S(74), 0, Math.Max(0, width - S(84)), S(BAR_H));

            // ---- primary action, then options, stacked up from the bar ----
            int actTop = _statusBar.Top - gap - S(ACT_H);
            _btnStart.Bounds = new Rectangle(left, actTop, _btnStart.Width, S(ACT_H));
            _btnReconnect.Bounds = new Rectangle(left + _btnStart.Width + S(8), actTop, _btnReconnect.Width, S(ACT_H));
            _optCard.Bounds = new Rectangle(left, actTop - gap - S(OPT_H), width, S(OPT_H));

            // ---- content band takes whatever vertical space is left ----
            int contentTop = y;
            int contentH = Math.Max(S(CONTENT_MIN_H), _optCard.Top - gap - contentTop);
            int vizW = (int)Math.Round(width * 0.46f);
            _vizCard.Bounds = new Rectangle(left, contentTop, vizW, contentH);
            _logCard.Bounds = new Rectangle(left + vizW + gap, contentTop, width - vizW - gap, contentH);
        }

        // ======================================================================================================================================
        //  DPI
        // ======================================================================================================================================
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RefreshScale();
            TryEnableDarkTitleBar();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            RefreshScale();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_theme != null)
                Font = T.FBody;
        }

        /// <summary>
        /// Recomputes the one scale source. Called from the handle, from DpiChangedAfterParent and
        /// from Resize: a top-level window's handle is torn down and rebuilt when it moves to a
        /// monitor with a different DPI, and a single notification is not enough to trust.
        /// </summary>
        private void RefreshScale()
        {
            int dpi = 96;
            if (IsHandleCreated)
            {
                try { dpi = DeviceDpi; }
                catch { dpi = 96; }
            }
            if (dpi <= 0) dpi = 96;

            float next = dpi / 96f;
            if (Math.Abs(next - _uiScale) < 0.0005f)
                return;

            _uiScale = next;
            _metricsDirty = true;
            if (IsHandleCreated)
            {
                ApplyMetrics();
                ApplyLayout();
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Asks DWM for a dark title bar so the native chrome matches the window body.</summary>
        private void TryEnableDarkTitleBar()
        {
            if (!IsHandleCreated) return;
            try
            {
                int enabled = 1;
                // 20 on Windows 10 2004+/11, 19 on 1803-1903
                if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
            }
            catch
            {
                // Cosmetic only - never let a missing DWM entry point break the window.
            }
        }

        // ======================================================================================================================================
        //  STATUS PRESENTATION
        // ======================================================================================================================================
        /// <summary>
        /// Feeds the header pill from the Joy-Con status label. The label's ForeColor is the
        /// app's existing state signal, so the pill inherits the same semantic palette.
        /// </summary>
        private void SyncHeaderState()
        {
            if (_header == null || _header.IsDisposed || _lblJoyconStatus == null)
                return;

            string text = _lblJoyconStatus.Text;
            Color color = _lblJoyconStatus.ForeColor.ToArgb() == 0 ? TXT_DIM : _lblJoyconStatus.ForeColor;

            if (string.IsNullOrWhiteSpace(text))
                text = _running ? "Working" : "Idle";
            else if (!_running)
                text = "Idle";

            if (text.Length > 24) text = text[..24];
            _header.SetState(text, color);
        }

        private void UpdateConnectModeStatusLabel(string state, Color? color = null)
        {
            if (_lblConnectModeStatus == null || _lblConnectModeStatus.IsDisposed)
                return;

            _lblConnectModeStatus.Text = $"Pair state: {state}";
            _lblConnectModeStatus.ForeColor = color ?? TXT_DIM;

            bool isPairReady = state.Contains("Pair ready", StringComparison.OrdinalIgnoreCase)
                || state.Contains("Single ready", StringComparison.OrdinalIgnoreCase);
            bool isFinalReady = state.Contains("Ready", StringComparison.OrdinalIgnoreCase) && !isPairReady;

            int stageValue;
            if (isFinalReady)
                stageValue = 3;
            else if (isPairReady)
                stageValue = 1;
            else if (state.Contains("Init", StringComparison.OrdinalIgnoreCase) || state.Contains("Recycling", StringComparison.OrdinalIgnoreCase))
                stageValue = 2;
            else if (state.Contains("Scan", StringComparison.OrdinalIgnoreCase)
                || state.Contains("Pair", StringComparison.OrdinalIgnoreCase)
                || state.Contains("No controller", StringComparison.OrdinalIgnoreCase)
                || state.Contains("reconnecting", StringComparison.OrdinalIgnoreCase))
                stageValue = 1;
            else
                stageValue = 0;

            bool isWarning = state.Contains("reconnecting", StringComparison.OrdinalIgnoreCase)
                || state.Contains("retry", StringComparison.OrdinalIgnoreCase)
                || state.Contains("No controller", StringComparison.OrdinalIgnoreCase)
                || state.Contains("silent", StringComparison.OrdinalIgnoreCase)
                || state.Contains("Disconnected", StringComparison.OrdinalIgnoreCase);

            bool isError = state.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || state.Contains("error", StringComparison.OrdinalIgnoreCase)
                || state.Contains("not installed", StringComparison.OrdinalIgnoreCase);

            Color activeColor = isError ? RED : isWarning ? YELLOW : ACCENT;
            ApplyConnectStageVisual(stageValue, activeColor);

            if (isFinalReady)
                _lastReadyUtc = DateTime.UtcNow;

            UpdateLinkHealthSummary();
        }

        private void ApplyConnectStageVisual(int stageValue, Color activeColor)
        {
            if (_prgConnectStage == null || _prgConnectStage.IsDisposed)
                return;

            stageValue = Math.Max(0, Math.Min(3, stageValue));
            int progress = stageValue switch
            {
                0 => 0,
                1 => 34,
                2 => 68,
                _ => 100,
            };

            _prgConnectStage.Tint = activeColor;
            _prgConnectStage.Value = progress;
            SetStageChip(_chipStageScan, stageValue >= 1, activeColor);
            SetStageChip(_chipStageInit, stageValue >= 2, activeColor);
            SetStageChip(_chipStageReady, stageValue >= 3, activeColor);
        }

        private static void SetStageChip(StageChip chip, bool active, Color activeColor)
        {
            if (chip == null || chip.IsDisposed)
                return;

            chip.Active = active;
            chip.Tint = activeColor;
            chip.Invalidate();
        }

        private void UpdateLinkHealthSummary()
        {
            if (_lblLinkHealth == null || _lblLinkHealth.IsDisposed)
                return;

            string reportText = _lastInputReportUtc == DateTime.MinValue
                ? "n/a"
                : $"{(DateTime.UtcNow - _lastInputReportUtc).TotalMilliseconds:0} ms ago";
            string readyText = _lastReadyUtc == DateTime.MinValue
                ? "n/a"
                : $"{(DateTime.UtcNow - _lastReadyUtc).TotalSeconds:0}s";

            var diag = SnapshotDiagnostics();
            string diagText = diag.ReportCount < 8
                ? "Diag: warming up"
                : $"Diag p95={diag.IntervalP95Ms:0.0}ms p99={diag.IntervalP99Ms:0.0}ms jitter={diag.IntervalStdMs:0.0}ms proc95={diag.ProcessP95Ms:0.00}ms drop={diag.DecodeFailRatePercent:0.0}%";

            if (diag.DeviceStats.Count > 0)
            {
                var sideParts = new List<string>();
                foreach (var d in diag.DeviceStats)
                    sideParts.Add(FormatDeviceDiagnosticSnapshot(d));
                diagText = $"{diagText} | {string.Join(" ; ", sideParts)}";
            }

            _lblLinkHealth.Text = $"Last report: {reportText} | Last ready: {readyText} | Reconnects: {_reconnectAttemptCount} | {diagText}";

            if (!_running)
            {
                _lblLinkHealth.ForeColor = TXT_DIM;
                return;
            }

            if (_lastInputReportUtc == DateTime.MinValue)
            {
                _lblLinkHealth.ForeColor = YELLOW;
                return;
            }

            var silence = DateTime.UtcNow - _lastInputReportUtc;
            if (silence >= TimeSpan.FromSeconds(6))
                _lblLinkHealth.ForeColor = RED;
            else if (silence >= TimeSpan.FromSeconds(2))
                _lblLinkHealth.ForeColor = YELLOW;
            else
                _lblLinkHealth.ForeColor = GREEN;
        }

        // ======================================================================================================================================
        //  LIFECYCLE
        // ======================================================================================================================================
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            StopOutputPump();
            Interlocked.Increment(ref _diagCaptureVersion);
            _diagCaptureActive = false;
            _healthTimer?.Stop();
            _healthTimer?.Dispose();
            _healthTimer = null;
            if (_powerEventsSubscribed)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _powerEventsSubscribed = false;
            }
            _bridge?.Dispose();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _healthTimer?.Dispose();
                _healthTimer = null;
                _tips?.Dispose();
                _tips = null!;
                _theme?.Dispose();
                _theme = null;
            }
            base.Dispose(disposing);
        }
    }
}
