
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
    public sealed class MainForm : Form
    {
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

        // â”€â”€ theme colours â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static readonly Color BG            = Color.FromArgb(20,  20,  20);
        private static readonly Color PANEL         = Color.FromArgb(32,  32,  32);
        private static readonly Color PANEL_ALT     = Color.FromArgb(26,  26,  26);
        private static readonly Color ACCENT        = Color.FromArgb(0,   120, 212);
        private static readonly Color GREEN         = Color.FromArgb(72,  199, 116);
        private static readonly Color RED           = Color.FromArgb(232, 80,  68);
        private static readonly Color YELLOW        = Color.FromArgb(255, 185, 0);
        private static readonly Color TXT           = Color.FromArgb(240, 240, 240);
        private static readonly Color TXT_DIM       = Color.FromArgb(148, 148, 148);
        private static readonly Color BORDER        = Color.FromArgb(52,  52,  52);
        private static readonly Color BTN_PRIMARY   = Color.FromArgb(0,   102, 204);
        private static readonly Color BTN_STOP      = Color.FromArgb(180, 30,  30);
        private static readonly Color BTN_SECONDARY = Color.FromArgb(50,  50,  50);
        private static readonly Color STICK_BG      = Color.FromArgb(22,  22,  22);
        private static readonly Color INACTIVE_BTN  = Color.FromArgb(44,  44,  44);
        private static readonly Font  FONT_LG       = new("Segoe UI", 11f, FontStyle.Regular);
        private static readonly Font  FONT_MD       = new("Segoe UI", 9f,  FontStyle.Regular);
        private static readonly Font  FONT_SM       = new("Segoe UI", 8f,  FontStyle.Regular);
        private static readonly Font  FONT_BOLD     = new("Segoe UI", 9f,  FontStyle.Bold);

        // â”€â”€ runtime state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private JoyconState _lastState = new();
        private bool _running   = false;
        private CancellationTokenSource? _cts;

        private Joycon2PC.ViGEm.ViGEmBridge? _bridge;
        private JoyconParser _parser = new();
        private BLEScanner? _scanner;  // active scanner â€” used by Reconnect button
        private bool _powerEventsSubscribed;
        private LogMode _logMode = LogMode.User;
        private readonly List<LogEntry> _logEntries = new();
        private const int MAX_LOG_ENTRIES = 500;
        private const int MAX_LOG_LINES = 300;

        // â”€â”€ controls â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private Label  _lblVigemStatus  = null!;
        private Label  _lblJoyconStatus = null!;
        private Button _btnStart        = null!;
        private Button _btnReconnect    = null!;
        private Button _btnTestSound    = null!;
        private Button _btnApplyLed     = null!;
        private CheckBox _chkConnectSound = null!;
        private CheckBox _chkMouseMode = null!;
        private CheckBox _chkShowDevLogs = null!;
        private Button _btnDiagCapture = null!;
        private Button _btnExportLog = null!;
        private Button _btnCopyLogSummary = null!;
        private int _copySummaryFeedbackVersion;
        private Label _lblLinkHealth = null!;
        private Label _lblConnectModeStatus = null!;
        private Label _lblStageScan = null!;
        private Label _lblStageInit = null!;
        private Label _lblStageReady = null!;
        private ProgressBar _prgConnectStage = null!;
        private ComboBox _cmbMouseStabilizer = null!;
        private ComboBox _cmbMouseSpeed = null!;
        private ComboBox _cmbDeviceTarget = null!;
        private ComboBox _cmbSoundPreset = null!;
        private ComboBox _cmbLedPattern = null!;
        private RichTextBox _log        = null!;

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
        private readonly object _outputStateLock = new();
        private JoyconState _latestMergedState = new();
        private bool _latestMergedStateAvailable;
        private CancellationTokenSource? _outputPumpCts;
        private Task? _outputPumpTask;
        private const int OUTPUT_PUMP_HZ = 120;
        private static readonly TimeSpan OUTPUT_PUMP_INTERVAL = TimeSpan.FromMilliseconds(1000.0 / OUTPUT_PUMP_HZ);
        private static readonly TimeSpan RECONNECT_MIN_INTERVAL = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan RECONNECT_SETTLE_WIN10 = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan RECONNECT_SETTLE_WIN11 = TimeSpan.FromMilliseconds(380);

        private JoyConVisualizerPanel _joyconViz = null!;

        // Button indicator labels keyed by name
        private Dictionary<string, Panel> _btnIndicators = new();

        public MainForm()
        {
            InitUI();
            _parser.StateChanged += OnStateChanged;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerEventsSubscribed = true;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  UI CONSTRUCTION
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        private void InitUI()
        {
            Text            = "Joycon2PC";
            ClientSize      = new Size(996, 720);
            MinimumSize     = new Size(980, 720);
            BackColor       = BG;
            ForeColor       = TXT;
            Font            = FONT_MD;
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            // â”€â”€ title bar area â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var lblTitle = MakeLabel("Joycon2PC", 21, new Point(18, 12), bold: true, color: ACCENT);
            lblTitle.AutoSize = true;
            Controls.Add(lblTitle);

            var lblSubtitle = MakeLabel("Joy-Con 2 to virtual Xbox controller bridge", 9, new Point(20, lblTitle.Bottom + 4), color: TXT_DIM);
            lblSubtitle.AutoSize = true;
            Controls.Add(lblSubtitle);

            // â”€â”€ status row â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var statusPanel = new Panel
            {
                BackColor = PANEL,
                Bounds    = new Rectangle(14, lblSubtitle.Bottom + 8, 968, 124),
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(statusPanel);

            statusPanel.Controls.Add(MakeLabel("System Status", 10, new Point(12, 8), bold: true, color: ACCENT));

            var leftStatusCard = new Panel
            {
                BackColor = PANEL_ALT,
                Bounds = new Rectangle(12, 30, 468, 50),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                BorderStyle = BorderStyle.FixedSingle,
            };
            statusPanel.Controls.Add(leftStatusCard);

            leftStatusCard.Controls.Add(MakeLabel("ViGEm Driver", 9, new Point(10, 6), bold: true));
            _lblVigemStatus = MakeLabel("Not checked", 9, new Point(10, 25), color: YELLOW);
            _lblVigemStatus.AutoSize = false;
            _lblVigemStatus.AutoEllipsis = true;
            _lblVigemStatus.Size = new Size(300, 18);
            leftStatusCard.Controls.Add(_lblVigemStatus);

            var rightStatusCard = new Panel
            {
                BackColor = PANEL_ALT,
                Bounds = new Rectangle(486, 30, 468, 50),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle,
            };
            statusPanel.Controls.Add(rightStatusCard);

            rightStatusCard.Controls.Add(MakeLabel("Joy-Con Link", 9, new Point(10, 6), bold: true));
            _lblJoyconStatus = MakeLabel("Not connected", 9, new Point(10, 25), color: TXT_DIM);
            _lblJoyconStatus.AutoSize = false;
            _lblJoyconStatus.AutoEllipsis = true;
            _lblJoyconStatus.Size = new Size(300, 18);
            rightStatusCard.Controls.Add(_lblJoyconStatus);

            var healthPanel = new Panel
            {
                BackColor = PANEL_ALT,
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            statusPanel.Controls.Add(healthPanel);

            healthPanel.Controls.Add(MakeLabel("Link Health", 8, new Point(10, 5), color: TXT_DIM));
            _lblLinkHealth = MakeLabel("Waiting for first input report...", 8, new Point(86, 5), color: TXT_DIM);
            _lblLinkHealth.AutoSize = false;
            _lblLinkHealth.AutoEllipsis = true;
            _lblLinkHealth.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblLinkHealth.Size = new Size(840, 16);
            healthPanel.Controls.Add(_lblLinkHealth);

            void LayoutStatusCards()
            {
                const int margin = 12;
                const int gap = 12;
                const int top = 30;
                const int height = 50;
                const int healthTop = 84;
                const int healthHeight = 28;

                int availableWidth = statusPanel.ClientSize.Width - (margin * 2) - gap;
                if (availableWidth < 2 * 100)
                {
                    // Ensure a reasonable minimum width for each card.
                    availableWidth = 2 * 100;
                }

                int cardWidth = availableWidth / 2;
                leftStatusCard.Bounds = new Rectangle(margin, top, cardWidth, height);
                rightStatusCard.Bounds = new Rectangle(margin + cardWidth + gap, top, cardWidth, height);
                _lblVigemStatus.Width = Math.Max(120, cardWidth - 20);
                _lblJoyconStatus.Width = Math.Max(120, cardWidth - 20);

                int healthWidth = Math.Max(200, statusPanel.ClientSize.Width - (margin * 2));
                healthPanel.Bounds = new Rectangle(margin, healthTop, healthWidth, healthHeight);
                _lblLinkHealth.Width = Math.Max(100, healthPanel.ClientSize.Width - 96);
            }

            statusPanel.Resize += (sender, args) => LayoutStatusCards();
            LayoutStatusCards();

            // â”€â”€ main content area â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var contentGrid = new TableLayoutPanel
            {
                Bounds = new Rectangle(14, statusPanel.Bottom + 12, 968, 336),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1,
            };
            contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44f));
            contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56f));
            contentGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(contentGrid);

            // â”€â”€ Joy-Con 2 drawn visualizer â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            _joyconViz = new JoyConVisualizerPanel
            {
                BackColor   = PANEL,
                Dock        = DockStyle.Fill,
                MinimumSize = new Size(360, 300),
            };
            contentGrid.Controls.Add(_joyconViz, 0, 0);

            // â”€â”€ log panel (right column) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var logCard = new Panel
            {
                BackColor = PANEL,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0),
                Padding = new Padding(10),
            };
            contentGrid.Controls.Add(logCard, 1, 0);

            var logHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.Transparent,
            };
            logCard.Controls.Add(logHeader);

            var logLabel = MakeLabel("Log", 10, new Point(0, 4), bold: true, color: ACCENT);
            logLabel.AutoSize = true;
            logHeader.Controls.Add(logLabel);

            _btnCopyLogSummary = new Button
            {
                Text = "Copy issues",
                Font = FONT_SM,
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(94, 24),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 3, 0, 0),
            };
            _btnCopyLogSummary.FlatAppearance.BorderSize = 1;
            _btnCopyLogSummary.FlatAppearance.BorderColor = BORDER;
            _btnCopyLogSummary.Click += async (sender, args) => await CopyRecentIssueSummaryToClipboardAsync();

            _btnExportLog = new Button
            {
                Text = "Export log",
                Font = FONT_SM,
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(88, 24),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 3, 0, 0),
            };
            _btnExportLog.FlatAppearance.BorderSize = 1;
            _btnExportLog.FlatAppearance.BorderColor = BORDER;
            _btnExportLog.Click += (sender, args) => ExportLogToFile();

            _btnDiagCapture = new Button
            {
                Text = "Diag 30s",
                Font = FONT_SM,
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(86, 24),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 3, 6, 0),
            };
            _btnDiagCapture.FlatAppearance.BorderSize = 1;
            _btnDiagCapture.FlatAppearance.BorderColor = BORDER;
            _btnDiagCapture.Click += async (sender, args) => await StartDiagnosticCaptureAsync();

            _chkShowDevLogs = new CheckBox
            {
                Text = "Dev details",
                Font = FONT_SM,
                BackColor = Color.Transparent,
                ForeColor = TXT,
                AutoSize = true,
                Margin = new Padding(0, 6, 8, 0),
            };
            _chkShowDevLogs.CheckedChanged += (sender, args) =>
            {
                _logMode = _chkShowDevLogs.Checked ? LogMode.Developer : LogMode.User;
                RebuildLogView();
            };

            var logActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            logActions.Controls.Add(_chkShowDevLogs);
            logActions.Controls.Add(_btnDiagCapture);
            logActions.Controls.Add(_btnExportLog);
            logActions.Controls.Add(_btnCopyLogSummary);
            logHeader.Controls.Add(logActions);

            _log = new RichTextBox
            {
                BackColor   = PANEL_ALT,
                ForeColor   = TXT,
                Font        = new Font("Consolas", 9f),
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                Dock        = DockStyle.Fill,
                WordWrap    = false,
            };
            logCard.Controls.Add(_log);

            // â”€â”€ action buttons â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var actionPanel = new Panel
            {
                Bounds = new Rectangle(14, 526, 968, 56),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                BackColor = Color.Transparent,
            };
            Controls.Add(actionPanel);

            _btnStart = new Button
            {
                Text      = "Connect Joy-Cons",
                BackColor = BTN_PRIMARY,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Bahnschrift", 11f, FontStyle.Bold),
                Size      = new Size(280, 60),
                Margin    = new Padding(0, 0, 10, 0),
                Cursor    = Cursors.Hand,
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += OnStartClicked;

            _btnReconnect = new Button
            {
                Text      = "Reconnect",
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Font      = FONT_MD,
                Size      = new Size(170, 60),
                Margin    = new Padding(0, 0, 0, 0),
                Cursor    = Cursors.Hand,
            };
            _btnReconnect.FlatAppearance.BorderSize = 1;
            _btnReconnect.FlatAppearance.BorderColor = BORDER;
            _btnReconnect.Click += (s, e) => OnReconnectClicked();

            var actionFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            actionFlow.Controls.Add(_btnStart);
            actionFlow.Controls.Add(_btnReconnect);
            actionPanel.Controls.Add(actionFlow);

            var modulePanel = new Panel
            {
                BackColor = PANEL,
                BorderStyle = BorderStyle.FixedSingle,
                Bounds = new Rectangle(14, 586, 968, 120),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(modulePanel);

            void LayoutVerticalSections()
            {
                int margin = Math.Max(12, (int)Math.Round(12f * DeviceDpi / 96f));
                int gap = margin;
                int width = Math.Max(320, ClientSize.Width - margin * 2);
                int actionHeight = Math.Max(60, (int)Math.Round(60f * DeviceDpi / 96f));
                int moduleHeight = Math.Max(120, (int)Math.Round(120f * DeviceDpi / 96f));

                int contentTop = statusPanel.Bottom + gap;
                int reservedBottom = gap + actionHeight + gap + moduleHeight + gap;
                int contentHeight = Math.Max(220, ClientSize.Height - contentTop - reservedBottom);

                contentGrid.Bounds = new Rectangle(margin, contentTop, width, contentHeight);
                actionPanel.Bounds = new Rectangle(margin, contentGrid.Bottom + gap, width, actionHeight);
                modulePanel.Bounds = new Rectangle(margin, actionPanel.Bottom + gap, width, moduleHeight);
            }

            Resize += (sender, args) => LayoutVerticalSections();
            LayoutVerticalSections();

            var lblModuleTitle = MakeLabel("Controls", 10, new Point(10, 8), bold: true, color: ACCENT);
            modulePanel.Controls.Add(lblModuleTitle);

            var controlsGrid = new TableLayoutPanel
            {
                Bounds = new Rectangle(10, 30, 946, 82),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                BackColor = Color.Transparent,
                ColumnCount = 3,
                RowCount = 3,
            };
            controlsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));
            controlsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            controlsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            controlsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            controlsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            controlsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            modulePanel.Controls.Add(controlsGrid);

            var deviceFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };
            deviceFlow.Controls.Add(MakeLabel("Device", 8, new Point(0, 0), color: TXT_DIM));
            _cmbDeviceTarget = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Width = 190,
                Margin = new Padding(8, 4, 0, 0),
            };
            deviceFlow.Controls.Add(_cmbDeviceTarget);
            controlsGrid.Controls.Add(deviceFlow, 0, 0);

            var feedbackFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };
            feedbackFlow.Controls.Add(MakeLabel("Sound", 8, new Point(0, 0), color: TXT_DIM));
            _cmbSoundPreset = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Width = 120,
                Margin = new Padding(8, 4, 0, 0),
            };
            feedbackFlow.Controls.Add(_cmbSoundPreset);

            _btnTestSound = new Button
            {
                Text = "Test Sound",
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Font = FONT_SM,
                Size = new Size(84, 26),
                Margin = new Padding(8, 3, 0, 0),
                Cursor = Cursors.Hand,
            };
            _btnTestSound.FlatAppearance.BorderSize = 1;
            _btnTestSound.FlatAppearance.BorderColor = BORDER;
            _btnTestSound.Click += async (sender, args) => await TriggerManualSoundTestAsync();
            feedbackFlow.Controls.Add(_btnTestSound);
            controlsGrid.Controls.Add(feedbackFlow, 1, 0);

            var rightFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };

            rightFlow.Controls.Add(MakeLabel("LED", 8, new Point(0, 0), color: TXT_DIM));
            _cmbLedPattern = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Width = 102,
                Margin = new Padding(8, 4, 0, 0),
            };
            rightFlow.Controls.Add(_cmbLedPattern);

            _btnApplyLed = new Button
            {
                Text = "Apply LED",
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Font = FONT_SM,
                Size = new Size(80, 26),
                Margin = new Padding(8, 3, 0, 0),
                Cursor = Cursors.Hand,
            };
            _btnApplyLed.FlatAppearance.BorderSize = 1;
            _btnApplyLed.FlatAppearance.BorderColor = BORDER;
            _btnApplyLed.Click += async (sender, args) => await TriggerManualLedApplyAsync();
            rightFlow.Controls.Add(_btnApplyLed);

            _chkConnectSound = new CheckBox
            {
                Text = "Auto sound",
                Checked = true,
                AutoSize = true,
                ForeColor = TXT_DIM,
                BackColor = Color.Transparent,
                Font = FONT_SM,
                Margin = new Padding(10, 6, 0, 0),
            };
            rightFlow.Controls.Add(_chkConnectSound);
            controlsGrid.Controls.Add(rightFlow, 2, 0);

            var mouseFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };

            _chkMouseMode = new CheckBox
            {
                Text = "Mouse mode",
                Checked = false,
                AutoSize = true,
                ForeColor = TXT_DIM,
                BackColor = Color.Transparent,
                Font = FONT_SM,
                Margin = new Padding(0, 6, 0, 0),
            };
            _chkMouseMode.CheckedChanged += (sender, args) =>
            {
                lock (_mouseStateLock)
                {
                    _mouseModeEnabled = _chkMouseMode.Checked;
                    ResetMouseModeState(releasePressedButtons: true);
                }
                Log($"Mouse mode {(_mouseModeEnabled ? "enabled" : "disabled") }.", ACCENT);
            };
            mouseFlow.Controls.Add(_chkMouseMode);

            _cmbMouseStabilizer = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Width = 116,
                Margin = new Padding(10, 3, 0, 0),
            };
            _cmbMouseStabilizer.Items.AddRange(new object[] { "Stab: Raw", "Stab: Stable", "Stab: VStable" });
            _cmbMouseStabilizer.SelectedIndex = 1;
            _cmbMouseStabilizer.SelectedIndexChanged += (sender, args) =>
            {
                lock (_mouseStateLock)
                {
                    _mouseStabilizerMode = _cmbMouseStabilizer.SelectedIndex switch
                    {
                        0 => MouseStabilizerMode.Raw,
                        2 => MouseStabilizerMode.VeryStable,
                        _ => MouseStabilizerMode.Stable,
                    };
                    ResetMouseModeState(releasePressedButtons: true);
                }
            };
            mouseFlow.Controls.Add(_cmbMouseStabilizer);

            _cmbMouseSpeed = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Width = 110,
                Margin = new Padding(8, 3, 0, 0),
            };
            _cmbMouseSpeed.Items.AddRange(new object[] { "Mouse: Fast", "Mouse: Normal", "Mouse: Slow" });
            _cmbMouseSpeed.SelectedIndex = 1;
            _cmbMouseSpeed.SelectedIndexChanged += (sender, args) =>
            {
                lock (_mouseStateLock)
                {
                    _mouseSpeedMode = _cmbMouseSpeed.SelectedIndex switch
                    {
                        0 => MouseSpeedMode.Fast,
                        2 => MouseSpeedMode.Slow,
                        _ => MouseSpeedMode.Normal,
                    };
                    ResetMouseModeState(releasePressedButtons: true);
                }
            };
            mouseFlow.Controls.Add(_cmbMouseSpeed);
            controlsGrid.Controls.Add(mouseFlow, 0, 1);

            _lblConnectModeStatus = MakeLabel("Pair state: Idle", 8, new Point(0, 0), color: TXT_DIM);
            _lblConnectModeStatus.AutoSize = true;
            _lblConnectModeStatus.Margin = new Padding(0, 3, 0, 0);
            controlsGrid.Controls.Add(_lblConnectModeStatus, 1, 1);
            controlsGrid.SetColumnSpan(_lblConnectModeStatus, 2);

            var stagePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 0),
            };
            stagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            stagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            stagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            stagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _lblStageScan = new Label
            {
                Text = "Scan",
                AutoSize = true,
                ForeColor = TXT_DIM,
                BackColor = PANEL_ALT,
                Font = FONT_SM,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0, 1, 6, 0),
            };
            stagePanel.Controls.Add(_lblStageScan, 0, 0);

            _lblStageInit = new Label
            {
                Text = "Init",
                AutoSize = true,
                ForeColor = TXT_DIM,
                BackColor = PANEL_ALT,
                Font = FONT_SM,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0, 1, 6, 0),
            };
            stagePanel.Controls.Add(_lblStageInit, 1, 0);

            _lblStageReady = new Label
            {
                Text = "Ready",
                AutoSize = true,
                ForeColor = TXT_DIM,
                BackColor = PANEL_ALT,
                Font = FONT_SM,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0, 1, 8, 0),
            };
            stagePanel.Controls.Add(_lblStageReady, 2, 0);

            _prgConnectStage = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Padding(0, 3, 0, 0),
            };
            stagePanel.Controls.Add(_prgConnectStage, 3, 0);

            controlsGrid.Controls.Add(stagePanel, 1, 2);
            controlsGrid.SetColumnSpan(stagePanel, 2);

            InitializeOptionControls();

            // kick off ViGEm check
            _ = Task.Run(CheckViGEmAsync);

            _healthTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _healthTimer.Tick += (sender, args) => UpdateLinkHealthSummary();
            _healthTimer.Start();
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  HELPER BUILDERS
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        private static Label MakeLabel(string text, float size, Point loc, bool bold = false, Color? color = null)
            => new()
            {
                Text      = text,
                Location  = loc,
                AutoSize  = true,
                BackColor = Color.Transparent,
                ForeColor = color ?? TXT,
                Font      = bold ? new Font("Segoe UI", size, FontStyle.Bold) : new Font("Segoe UI", size),
            };

        private static Panel MakeStickPanel(Point loc) => new()
        {
            Size      = new Size(84, 84),
            Location  = loc,
            BackColor = STICK_BG,
            BorderStyle = BorderStyle.FixedSingle,
        };

        private void UpdateConnectModeStatusLabel(string state, Color? color = null)
        {
            if (_lblConnectModeStatus == null || _lblConnectModeStatus.IsDisposed)
                return;

            _lblConnectModeStatus.Text = $"Pair state: {state}";
            _lblConnectModeStatus.ForeColor = color ?? TXT_DIM;

            int stageValue;
            if (state.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                stageValue = 3;
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

            if (stageValue >= 3)
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

            _prgConnectStage.Value = progress;
            SetStageCapsule(_lblStageScan, stageValue >= 1, activeColor);
            SetStageCapsule(_lblStageInit, stageValue >= 2, activeColor);
            SetStageCapsule(_lblStageReady, stageValue >= 3, activeColor);
        }

        private static void SetStageCapsule(Label label, bool active, Color activeColor)
        {
            if (label == null || label.IsDisposed)
                return;

            label.BackColor = active ? activeColor : PANEL_ALT;
            label.ForeColor = active ? Color.White : TXT_DIM;
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

        private void ExportLogToFile()
        {
            using var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"joycon2pc-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Title = "Export Joycon2PC logs",
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                var lines = new List<string>
                {
                    $"Joycon2PC log export @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Pair status: {_lblConnectModeStatus.Text}",
                    _lblLinkHealth != null ? $"Link health: {_lblLinkHealth.Text}" : string.Empty,
                    string.Empty,
                    "Diagnostics:",
                };

                lines.AddRange(BuildDiagnosticExportLines(preferFrozen: false));
                lines.AddRange(new []
                {
                    string.Empty,
                    "Entries:",
                });

                foreach (var entry in _logEntries)
                {
                    string audience = entry.Audience == LogAudience.Developer ? "DEV" : "USR";
                    lines.Add($"[{entry.TimeText}] [{audience}] {entry.Message}");
                }

                File.WriteAllLines(dialog.FileName, lines);
                Log($"Logs exported: {dialog.FileName}", GREEN);
            }
            catch (Exception ex)
            {
                Log($"Failed to export logs: {ex.Message}", YELLOW);
            }
        }

        private async Task StartDiagnosticCaptureAsync()
        {
            if (_diagCaptureActive)
            {
                DevLog("Diagnostic capture is already running.", TXT_DIM);
                return;
            }

            _diagCaptureActive = true;
            _diagCaptureStartUtc = DateTime.UtcNow;
            lock (_diagLock)
            {
                _lastCompletedDiagnosticSnapshot = null;
                _lastCompletedDiagCapturedLocal = DateTime.MinValue;
                _lastCompletedDiagCaptureStartUtc = DateTime.MinValue;
            }
            int version = Interlocked.Increment(ref _diagCaptureVersion);
            ResetDiagnosticMetrics();
            SetDiagCaptureButtonState(running: true);
            Log($"Diagnostic capture started ({DIAG_CAPTURE_DURATION.TotalSeconds:0}s window).", ACCENT);

            await Task.Delay(DIAG_CAPTURE_DURATION);

            if (IsDisposed || version != _diagCaptureVersion)
                return;

            _diagCaptureActive = false;
            SetDiagCaptureButtonState(running: false);

            var diag = SnapshotDiagnostics();
            lock (_diagLock)
            {
                _lastCompletedDiagnosticSnapshot = diag;
                _lastCompletedDiagCapturedLocal = DateTime.Now;
                _lastCompletedDiagCaptureStartUtc = _diagCaptureStartUtc;
            }
            Log($"Diagnostic capture complete: {FormatDiagnosticSnapshot(diag)}", GREEN);
            DevLog("Tip: use Copy issues to copy diagnostics + recent logs.", TXT_DIM);
        }

        private void SetDiagCaptureButtonState(bool running)
        {
            if (_btnDiagCapture == null || _btnDiagCapture.IsDisposed)
                return;

            _btnDiagCapture.Enabled = !running;
            _btnDiagCapture.Text = running ? "Diag running" : "Diag 30s";
            _btnDiagCapture.BackColor = running ? ACCENT : BTN_SECONDARY;
            _btnDiagCapture.ForeColor = running ? Color.White : TXT;
        }

        private sealed record DiagnosticSnapshot(
            long ReportCount,
            long DecodeFailCount,
            long InvalidSizeCount,
            double DecodeFailRatePercent,
            double IntervalP50Ms,
            double IntervalP95Ms,
            double IntervalP99Ms,
            double IntervalStdMs,
            double ProcessP95Ms,
            double ProcessP99Ms,
            IReadOnlyList<DeviceDiagnosticSnapshot> DeviceStats
        );

        private sealed record DeviceDiagnosticSnapshot(
            string DeviceId,
            string Side,
            long ReportCount,
            long DecodeFailCount,
            double DecodeFailRatePercent,
            double IntervalP95Ms,
            double IntervalP99Ms,
            double IntervalStdMs,
            int SpikeOver40Ms,
            int SpikeOver60Ms,
            int SpikeOver100Ms
        );

        private sealed class DeviceDiagnosticTracker
        {
            public readonly Queue<double> IntervalsMs = new();
            public long LastArrivalTicks;
            public long ReportCount;
            public long DecodeFailCount;
            public long InvalidSizeCount;
            public int SpikeOver40Ms;
            public int SpikeOver60Ms;
            public int SpikeOver100Ms;
        }

        private void ResetDiagnosticMetrics()
        {
            lock (_diagLock)
            {
                _diagIntervalSamplesMs.Clear();
                _diagProcessSamplesMs.Clear();
                _diagTotalReports = 0;
                _diagDecodeFailures = 0;
                _diagInvalidSizeReports = 0;
                _diagLastArrivalTicks = 0;
                _diagByDevice.Clear();
            }
        }

        private void RecordDiagnosticSample(string deviceId, long arrivalTicks, double processMs, bool decodeOk, bool sizeLooksValid)
        {
            lock (_diagLock)
            {
                _diagTotalReports++;
                if (!decodeOk)
                    _diagDecodeFailures++;
                if (!sizeLooksValid)
                    _diagInvalidSizeReports++;

                if (_diagLastArrivalTicks != 0)
                {
                    double intervalMs = (arrivalTicks - _diagLastArrivalTicks) * 1000.0 / Stopwatch.Frequency;
                    EnqueueBounded(_diagIntervalSamplesMs, intervalMs, DIAG_MAX_SAMPLES);
                }

                _diagLastArrivalTicks = arrivalTicks;
                EnqueueBounded(_diagProcessSamplesMs, processMs, DIAG_MAX_SAMPLES);

                if (!_diagByDevice.TryGetValue(deviceId, out var tracker))
                {
                    tracker = new DeviceDiagnosticTracker();
                    _diagByDevice[deviceId] = tracker;
                }

                tracker.ReportCount++;
                if (!decodeOk)
                    tracker.DecodeFailCount++;
                if (!sizeLooksValid)
                    tracker.InvalidSizeCount++;

                if (tracker.LastArrivalTicks != 0)
                {
                    double intervalMs = (arrivalTicks - tracker.LastArrivalTicks) * 1000.0 / Stopwatch.Frequency;
                    EnqueueBounded(tracker.IntervalsMs, intervalMs, DIAG_MAX_SAMPLES);
                    if (intervalMs >= DIAG_SPIKE_40_MS)
                        tracker.SpikeOver40Ms++;
                    if (intervalMs >= DIAG_SPIKE_60_MS)
                        tracker.SpikeOver60Ms++;
                    if (intervalMs >= DIAG_SPIKE_100_MS)
                        tracker.SpikeOver100Ms++;
                }

                tracker.LastArrivalTicks = arrivalTicks;
            }
        }

        private static void EnqueueBounded(Queue<double> queue, double value, int maxSamples)
        {
            queue.Enqueue(value);
            while (queue.Count > maxSamples)
                queue.Dequeue();
        }

        private DiagnosticSnapshot SnapshotDiagnostics()
        {
            lock (_diagLock)
            {
                return BuildDiagnosticSnapshotLocked();
            }
        }

        private DiagnosticSnapshot BuildDiagnosticSnapshotLocked()
        {
            var intervalArray = _diagIntervalSamplesMs.ToArray();
            var processArray = _diagProcessSamplesMs.ToArray();
            double failRate = _diagTotalReports > 0
                ? (_diagDecodeFailures * 100.0) / _diagTotalReports
                : 0;

            return new DiagnosticSnapshot(
                ReportCount: _diagTotalReports,
                DecodeFailCount: _diagDecodeFailures,
                InvalidSizeCount: _diagInvalidSizeReports,
                DecodeFailRatePercent: failRate,
                IntervalP50Ms: Percentile(intervalArray, 50),
                IntervalP95Ms: Percentile(intervalArray, 95),
                IntervalP99Ms: Percentile(intervalArray, 99),
                IntervalStdMs: StdDev(intervalArray),
                ProcessP95Ms: Percentile(processArray, 95),
                ProcessP99Ms: Percentile(processArray, 99),
                DeviceStats: BuildDeviceDiagnosticSnapshotsLocked()
            );
        }

        private (DiagnosticSnapshot Snapshot, DateTime CapturedLocal, DateTime CaptureStartUtc, bool IsFrozen) GetDiagnosticBundle(bool preferFrozen)
        {
            lock (_diagLock)
            {
                if (preferFrozen && _lastCompletedDiagnosticSnapshot != null)
                {
                    return (
                        _lastCompletedDiagnosticSnapshot,
                        _lastCompletedDiagCapturedLocal == DateTime.MinValue ? DateTime.Now : _lastCompletedDiagCapturedLocal,
                        _lastCompletedDiagCaptureStartUtc,
                        true);
                }

                return (BuildDiagnosticSnapshotLocked(), DateTime.Now, _diagCaptureStartUtc, false);
            }
        }

        private List<DeviceDiagnosticSnapshot> BuildDeviceDiagnosticSnapshotsLocked()
        {
            var list = new List<DeviceDiagnosticSnapshot>(_diagByDevice.Count);
            foreach (var pair in _diagByDevice)
            {
                string deviceId = pair.Key;
                var tracker = pair.Value;
                var intervals = tracker.IntervalsMs.ToArray();
                double failRate = tracker.ReportCount > 0
                    ? (tracker.DecodeFailCount * 100.0) / tracker.ReportCount
                    : 0;

                list.Add(new DeviceDiagnosticSnapshot(
                    DeviceId: deviceId,
                    Side: ResolveDeviceSide(deviceId),
                    ReportCount: tracker.ReportCount,
                    DecodeFailCount: tracker.DecodeFailCount,
                    DecodeFailRatePercent: failRate,
                    IntervalP95Ms: Percentile(intervals, 95),
                    IntervalP99Ms: Percentile(intervals, 99),
                    IntervalStdMs: StdDev(intervals),
                    SpikeOver40Ms: tracker.SpikeOver40Ms,
                    SpikeOver60Ms: tracker.SpikeOver60Ms,
                    SpikeOver100Ms: tracker.SpikeOver100Ms
                ));
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Side, b.Side));
            return list;
        }

        private string ResolveDeviceSide(string deviceId)
        {
            if (_leftDeviceId == deviceId && _rightDeviceId == deviceId)
                return "S";
            if (_leftDeviceId == deviceId)
                return "L";
            if (_rightDeviceId == deviceId)
                return "R";
            return "?";
        }

        private static double Percentile(double[] samples, int percentile)
        {
            if (samples.Length == 0)
                return 0;

            Array.Sort(samples);
            double rank = (percentile / 100.0) * (samples.Length - 1);
            int lowIndex = (int)Math.Floor(rank);
            int highIndex = (int)Math.Ceiling(rank);
            if (lowIndex == highIndex)
                return samples[lowIndex];

            double weight = rank - lowIndex;
            return samples[lowIndex] + ((samples[highIndex] - samples[lowIndex]) * weight);
        }

        private static double StdDev(double[] samples)
        {
            if (samples.Length == 0)
                return 0;

            double mean = 0;
            for (int i = 0; i < samples.Length; i++)
                mean += samples[i];
            mean /= samples.Length;

            double variance = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double delta = samples[i] - mean;
                variance += delta * delta;
            }

            variance /= samples.Length;
            return Math.Sqrt(variance);
        }

        private IEnumerable<string> BuildDiagnosticExportLines(bool preferFrozen)
        {
            var bundle = GetDiagnosticBundle(preferFrozen);
            var diag = bundle.Snapshot;
            yield return $"Captured at: {bundle.CapturedLocal:yyyy-MM-dd HH:mm:ss}";
            if (bundle.CaptureStartUtc != DateTime.MinValue)
                yield return $"Capture window start (UTC): {bundle.CaptureStartUtc:yyyy-MM-dd HH:mm:ss}";
            if (bundle.IsFrozen)
                yield return "Snapshot source: last completed 30s capture";
            yield return $"Reports: {diag.ReportCount}";
            yield return $"Decode failures: {diag.DecodeFailCount} ({diag.DecodeFailRatePercent:0.00}%)";
            yield return $"Unexpected report length (!=63): {diag.InvalidSizeCount}";
            yield return $"Report interval p50/p95/p99: {diag.IntervalP50Ms:0.00}/{diag.IntervalP95Ms:0.00}/{diag.IntervalP99Ms:0.00} ms";
            yield return $"Report interval jitter(stddev): {diag.IntervalStdMs:0.00} ms";
            yield return $"Pipeline process p95/p99: {diag.ProcessP95Ms:0.000}/{diag.ProcessP99Ms:0.000} ms";

            if (diag.DeviceStats.Count > 0)
            {
                yield return "Per-device:";
                foreach (var d in diag.DeviceStats)
                {
                    string shortId = d.DeviceId.Length > 8 ? d.DeviceId[..8] : d.DeviceId;
                    yield return $"  [{d.Side}] {shortId}: reports={d.ReportCount}, fail={d.DecodeFailCount} ({d.DecodeFailRatePercent:0.00}%), p95/p99={d.IntervalP95Ms:0.00}/{d.IntervalP99Ms:0.00} ms, jitter={d.IntervalStdMs:0.00} ms, spikes40/60/100={d.SpikeOver40Ms}/{d.SpikeOver60Ms}/{d.SpikeOver100Ms}";
                }
            }
        }

        private IEnumerable<string> BuildDiagnosticSummaryLines()
            => BuildDiagnosticExportLines(preferFrozen: true);

        private static string FormatDiagnosticSnapshot(DiagnosticSnapshot diag)
            => $"reports={diag.ReportCount}, fail={diag.DecodeFailCount}({diag.DecodeFailRatePercent:0.0}%), len!=63={diag.InvalidSizeCount}, interval p95/p99={diag.IntervalP95Ms:0.0}/{diag.IntervalP99Ms:0.0}ms, jitter={diag.IntervalStdMs:0.0}ms, proc p95={diag.ProcessP95Ms:0.00}ms";

        private static string FormatDeviceDiagnosticSnapshot(DeviceDiagnosticSnapshot d)
            => $"{d.Side} p95/p99={d.IntervalP95Ms:0.0}/{d.IntervalP99Ms:0.0}ms j={d.IntervalStdMs:0.0} spikes60={d.SpikeOver60Ms}";

        private async Task CopyRecentIssueSummaryToClipboardAsync()
        {
            if (_logEntries.Count == 0)
            {
                Log("No logs available to summarize yet.", TXT_DIM);
                await FlashCopySummaryButtonAsync("No logs", YELLOW, Color.Black, 900);
                return;
            }

            bool IsIssueEntry(LogEntry e)
            {
                var msg = e.Message;
                return msg.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("retry", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("silent", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("not installed", StringComparison.OrdinalIgnoreCase);
            }

            var selected = new List<LogEntry>();
            for (int i = _logEntries.Count - 1; i >= 0 && selected.Count < 12; i--)
            {
                var entry = _logEntries[i];
                if (IsIssueEntry(entry))
                    selected.Add(entry);
            }

            if (selected.Count == 0)
            {
                for (int i = Math.Max(0, _logEntries.Count - 8); i < _logEntries.Count; i++)
                    selected.Add(_logEntries[i]);
            }

            selected.Reverse();

            var lines = new List<string>
            {
                $"Joycon2PC issue summary ({DateTime.Now:yyyy-MM-dd HH:mm:ss})",
                $"Current pair state: {_lblConnectModeStatus.Text}",
                "",
                "Diagnostics:",
            };

            foreach (var line in BuildDiagnosticSummaryLines())
                lines.Add(line);

            lines.AddRange(new[]
            {
                string.Empty,
                "Recent relevant logs:",
            });

            foreach (var entry in selected)
                lines.Add($"[{entry.TimeText}] {entry.Message}");

            string summary = string.Join(Environment.NewLine, lines);
            try
            {
                Clipboard.SetText(summary);
                Log("Issue summary copied to clipboard.", GREEN);
                await FlashCopySummaryButtonAsync("Copied", GREEN, Color.White, 1100);
            }
            catch (Exception ex)
            {
                Log($"Failed to copy issue summary: {ex.Message}", YELLOW);
                await FlashCopySummaryButtonAsync("Copy failed", RED, Color.White, 1400);
            }
        }

        private async Task FlashCopySummaryButtonAsync(string text, Color backColor, Color foreColor, int durationMs)
        {
            if (_btnCopyLogSummary == null || _btnCopyLogSummary.IsDisposed)
                return;

            int version = Interlocked.Increment(ref _copySummaryFeedbackVersion);
            string oldText = _btnCopyLogSummary.Text;
            Color oldBack = _btnCopyLogSummary.BackColor;
            Color oldFore = _btnCopyLogSummary.ForeColor;

            _btnCopyLogSummary.Text = text;
            _btnCopyLogSummary.BackColor = backColor;
            _btnCopyLogSummary.ForeColor = foreColor;

            await Task.Delay(Math.Max(150, durationMs));

            if (_btnCopyLogSummary.IsDisposed || version != _copySummaryFeedbackVersion)
                return;

            _btnCopyLogSummary.Text = oldText;
            _btnCopyLogSummary.BackColor = oldBack;
            _btnCopyLogSummary.ForeColor = oldFore;
        }

        private void AddButtonIndicator(Control parent, string name, Point loc, Color? onColor = null)
        {
            var p = new Panel
            {
                Size      = new Size(20, 20),
                Location  = loc,
                BackColor = INACTIVE_BTN,
                BorderStyle = BorderStyle.FixedSingle,
            };
            var lbl = new Label
            {
                Text      = name.Length <= 2 ? name : name[..2],
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 6.5f, FontStyle.Bold),
                ForeColor = TXT_DIM,
                BackColor = Color.Transparent,
            };
            p.Controls.Add(lbl);
            parent.Controls.Add(p);
            _btnIndicators[name] = p;
            _btnIndicators[name].Tag = onColor ?? GREEN;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  VIGEM CHECK
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        private async Task CheckViGEmAsync()
        {
            await Task.Delay(200); // let window paint first
            try
            {
                _bridge = new Joycon2PC.ViGEm.ViGEmBridge();
                _bridge.Connect();

                    Invoke(() =>
                {
                    _lblVigemStatus.Text      = "Driver found and ready";
                    _lblVigemStatus.ForeColor = GREEN;
                    Log("ViGEmBus driver found. Virtual Xbox 360 controller is ready.", GREEN);
                });
            }
            catch
            {
                    Invoke(() =>
                {
                    _lblVigemStatus.Text      = "ViGEmBus not installed";
                    _lblVigemStatus.ForeColor = RED;
                    Log("ViGEmBus driver not found! Install it from:", RED);
                    Log("  https://github.com/nefarius/ViGEmBus/releases", ACCENT);
                    Log("Virtual controller will not appear in Windows without ViGEmBus.", YELLOW);
                });
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  BUTTON HANDLERS
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        // Reconnect: force-clear stale BLE device state (Windows GattServerDisconnected
        // is unreliable and often never fires), then restart the scan loop immediately.
        private void OnReconnectClicked()
        {
            _ = PerformHardReconnectAsync("manual reconnect");
        }

        private async Task PerformHardReconnectAsync(string reason)
        {
            if (_reconnectInProgress || IsDisposed)
                return;

            var now = DateTime.UtcNow;
            if ((now - _lastReconnectRequestUtc) < RECONNECT_MIN_INTERVAL)
            {
                Log("Reconnect ignored: request too frequent.", TXT_DIM);
                return;
            }
            _lastReconnectRequestUtc = now;
            _reconnectAttemptCount++;
            UpdateLinkHealthSummary();

            _reconnectInProgress = true;
            try
            {
                Log($"[reconnect] Reconnect ({reason}) - fully restarting BLE loop...", YELLOW);
                _lblJoyconStatus.Text = "Reconnecting...";
                _lblJoyconStatus.ForeColor = YELLOW;
                UpdateConnectModeStatusLabel("Recycling BLE session", YELLOW);

                _scanner?.DisconnectAll();
                _deviceStates.Clear();
                _leftDeviceId = null;
                _rightDeviceId = null;
                _deviceLStickSentinel.Clear();
                _deviceRStickSentinel.Clear();

                bool wasRunning = _running;
                if (wasRunning)
                    StopAll();

                var settleDelay = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                    ? RECONNECT_SETTLE_WIN11
                    : RECONNECT_SETTLE_WIN10;
                await Task.Delay(settleDelay);

                if (!IsDisposed)
                    StartAll();
            }
            finally
            {
                _reconnectInProgress = false;
            }
        }

        private void OnStartClicked(object? s, EventArgs e)
        {
            if (_running)
            {
                StopAll();
            }
            else
            {
                StartAll();
            }
        }

        private void StartAll()
        {
            if (_running && _cts is { IsCancellationRequested: false })
            {
                DevLog("Start ignored: BLE loop already running.", TXT_DIM);
                return;
            }

            _running = true;
            _lastInputReportUtc = DateTime.MinValue;
            ResetDiagnosticMetrics();
            StartOutputPump();
            _cts     = new CancellationTokenSource();
            _btnStart.Text      = "Stop";
            _btnStart.BackColor = BTN_STOP;
            UpdateLinkHealthSummary();

            // Attach parser â†’ bridge
            _parser.StateChanged -= OnStateChanged;
            _parser               = new JoyconParser();
            _parser.StateChanged += OnStateChanged;

#if INTHEHAND
            Log("Searching for Joy-Con over Bluetooth LE...", ACCENT);
            DevLog("Connection mode: Auto pair (L + R)", TXT_DIM);
        var os = Environment.OSVersion.Version;
        bool isWin11OrNewer = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        DevLog($"OS {os.Major}.{os.Minor}.{os.Build} ({(isWin11OrNewer ? "Win11+" : "Win10 compatibility mode")})", TXT_DIM);
            _lblJoyconStatus.Text      = "Scanning...";
            _lblJoyconStatus.ForeColor = YELLOW;
                UpdateConnectModeStatusLabel("Scanning", YELLOW);
            _ = RunRealAsync(_cts.Token);
#endif
        }

        private void StopAll()
        {
            _cts?.Cancel();
            StopOutputPump();
            if (_diagCaptureActive)
            {
                _diagCaptureActive = false;
                Interlocked.Increment(ref _diagCaptureVersion);
                SetDiagCaptureButtonState(running: false);
                DevLog("Diagnostic capture cancelled: stopped by user.", TXT_DIM);
            }
            _running = false;
            _btnStart.Text      = "Connect Joy-Cons";
            _btnStart.BackColor = BTN_PRIMARY;
            _btnReconnect.BackColor = BTN_SECONDARY;
            _lblJoyconStatus.Text      = "Stopped";
            _lblJoyconStatus.ForeColor = TXT_DIM;
            UpdateConnectModeStatusLabel("Stopped", TXT_DIM);
            UpdateLinkHealthSummary();
            Log("Stopped.", TXT_DIM);
        }

        private void StartOutputPump()
        {
            StopOutputPump();

            _outputPumpCts = new CancellationTokenSource();
            _outputPumpTask = Task.Run(() => OutputPumpLoopAsync(_outputPumpCts.Token));
        }

        private void StopOutputPump()
        {
            var cts = _outputPumpCts;
            _outputPumpCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }

            lock (_outputStateLock)
            {
                _latestMergedStateAvailable = false;
            }
        }

        private async Task OutputPumpLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                JoyconState? state = null;
                lock (_outputStateLock)
                {
                    if (_latestMergedStateAvailable)
                        state = CloneState(_latestMergedState);
                }

                if (state != null)
                {
                    try { _bridge?.UpdateFromState(state); } catch { }
                }

                try { await Task.Delay(OUTPUT_PUMP_INTERVAL, ct); } catch { break; }
            }
        }

        private static JoyconState CloneState(JoyconState source)
            => new()
            {
                Buttons = source.Buttons,
                LeftStickX = source.LeftStickX,
                LeftStickY = source.LeftStickY,
                RightStickX = source.RightStickX,
                RightStickY = source.RightStickY,
            };

#if INTHEHAND
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  REAL JOYCON LOOP  (dual Joy-Con merge + keep-alive + reconnect)
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        // Per-device state tracking for dual Joy-Con merge
        private readonly Dictionary<string, JoyconState> _deviceStates = new();
        private string? _leftDeviceId;
        private string? _rightDeviceId;
        // Raw sentinel flags: true = that stick slot is unused (sentinel 2047) for this device
        private readonly Dictionary<string, bool> _deviceLStickSentinel = new();
        private readonly Dictionary<string, bool> _deviceRStickSentinel = new();

        private async Task RunRealAsync(CancellationToken ct)
        {
            // â”€â”€ outer reconnect loop â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            while (!ct.IsCancellationRequested)
            {
                DateTime lastReportUtc = DateTime.UtcNow;
                DateTime lastInputModeRecoveryUtc = DateTime.MinValue;
                var lastReportByDevice = new Dictionary<string, DateTime>();
                var deviceReportLock = new object();
                var scanner = new BLEScanner();
                _scanner = scanner;
                _deviceStates.Clear();
                _leftDeviceId = null;
                _rightDeviceId = null;
                _deviceLStickSentinel.Clear();
                _deviceRStickSentinel.Clear();

                // â”€â”€ hex-dump and change-tracking â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                var dumpCounts          = new Dictionary<string, int>();
                var stickLoggedDevices  = new HashSet<string>();   // prevent dc==3 flood
                var lastButtons         = new Dictionary<string, uint>();
                var lastStickLog        = new Dictionary<string, (int lx, int ly, int rx, int ry)>();
                var lastRawBytes        = new Dictionary<string, byte[]>(); // for byte-diff logger
                JoyconState?            lastMerged = null;

                // Warmup gate: Joy-Con 2 controllers send a burst of garbage button data
                // (L+ZL bits asserted) during their BLE init sequence (~200-400 ms).
                // We suppress all button output for the first WARMUP_REPORTS reports per
                // device. Stick data and sentinel detection are unaffected â€” they read
                // from raw bytes, not from state.Buttons.
                const int WARMUP_REPORTS = 30;
                var warmupReports = new Dictionary<string, int>();

                // Keep hot-path logs off by default; high-frequency console writes can
                // amplify lag and jitter when report rate is high. Enable diagnostics
                // at runtime using JOYCON2PC_RAW_BYTE_DIFF_LOG / JOYCON2PC_VERBOSE_INPUT_LOG.
                bool enableRawByteDiffLog = IsEnabledByEnv("JOYCON2PC_RAW_BYTE_DIFF_LOG");
                bool enableVerboseInputLog = IsEnabledByEnv("JOYCON2PC_VERBOSE_INPUT_LOG");
                bool enableHardcoreDiag = IsEnabledByEnv("JOYCON2PC_HARDCORE_DIAG");

                if (enableHardcoreDiag)
                {
                    scanner.DiagnosticTrace += (deviceId, line) =>
                    {
                        try { BeginInvoke(() => DevLog($"TXDBG {line}", Color.FromArgb(255, 170, 70))); } catch { }
                    };
                    try { BeginInvoke(() => DevLog("Hardcore diagnostic mode: ON (JOYCON2PC_HARDCORE_DIAG=1)", Color.FromArgb(255, 170, 70))); } catch { }
                }

                // Require the same button word in N consecutive reports before applying.
                // This filters short glitches without adding noticeable latency.
                const int BUTTON_DEBOUNCE_REPORTS = 2;
                var buttonCandidate = new Dictionary<string, uint>();
                var buttonStableCounts = new Dictionary<string, int>();
                var buttonDebounced = new Dictionary<string, uint>();

                scanner.RawReportReceived += (deviceId, data) =>
                {
                    long diagArrivalTicks = Stopwatch.GetTimestamp();
                    bool decodeOk = false;
                    bool sizeLooksValid = data.Length == 63;
                    lastReportUtc = DateTime.UtcNow;
                    _lastInputReportUtc = lastReportUtc;
                    lock (deviceReportLock)
                        lastReportByDevice[deviceId] = lastReportUtc;
                    string shortId = deviceId.Length > 8 ? deviceId[..8] : deviceId;
                    try
                    {
                        // Hex dump first 3 reports per device
                        if (!dumpCounts.ContainsKey(deviceId)) dumpCounts[deviceId] = 0;
                        if (dumpCounts[deviceId] < 3)
                        {
                            dumpCounts[deviceId]++;
                            string hex = BitConverter.ToString(data, 0, Math.Min(data.Length, 20));
                            try { BeginInvoke(() => DevLog($"RAW[{shortId}] len={data.Length}: {hex}", Color.FromArgb(255, 200, 80))); } catch { }
                        }

                        // â”€â”€ Byte-diff logger: scan ALL bytes (skip [0]=rolling counter) â”€â”€
                        if (enableRawByteDiffLog)
                        {
                            if (lastRawBytes.TryGetValue(deviceId, out var prevRaw) && prevRaw.Length == data.Length)
                            {
                                var diff = new System.Text.StringBuilder();
                                for (int i = 1; i < data.Length; i++)
                                {
                                    if (data[i] != prevRaw[i])
                                        diff.Append($" [{i}]:{prevRaw[i]:X2}->{data[i]:X2}");
                                }
                                if (diff.Length > 0)
                                    Console.WriteLine($"DIFF[{shortId}]{diff}");
                            }
                            lastRawBytes[deviceId] = (byte[])data.Clone();
                        }

                        if (!NS2InputReportDecoder.TryDecode(data, out var decoded))
                            return;

                        decodeOk = true;

                        var state = new JoyconState
                        {
                            Buttons = decoded.Buttons,
                            LeftStickX = decoded.LeftStickX,
                            LeftStickY = decoded.LeftStickY,
                            RightStickX = decoded.RightStickX,
                            RightStickY = decoded.RightStickY,
                        };

                        // Detect L vs R from sentinel (runs every report â€” unconditional).
                        // Joy-Con L: its right-stick slot is always 2047 (unused).
                        // Joy-Con R: its left-stick slot is always 2047 (unused).
                        bool lxSentinel = decoded.RawLeftStickX == NS2InputReportDecoder.SentinelStickValue
                                       && decoded.RawLeftStickY == NS2InputReportDecoder.SentinelStickValue;
                        bool rxSentinel = decoded.RawRightStickX == NS2InputReportDecoder.SentinelStickValue
                                       && decoded.RawRightStickY == NS2InputReportDecoder.SentinelStickValue;

                        // Persist raw sentinel flags so AssignDeviceIds Pass 3 can use them.
                        // Use OR-assignment: once we've seen a sentinel it stays true.
                        _deviceLStickSentinel[deviceId] = _deviceLStickSentinel.TryGetValue(deviceId, out var prevL) && prevL || lxSentinel;
                        _deviceRStickSentinel[deviceId] = _deviceRStickSentinel.TryGetValue(deviceId, out var prevR) && prevR || rxSentinel;

                        // Keep side assignment stable once both sides are known.
                        // Without this lock, occasional glitch frames can flip L/R ownership
                        // and cause visible LS/RS source jitter.
                        bool sideMappingLocked = _leftDeviceId != null
                            && _rightDeviceId != null
                            && !string.Equals(_leftDeviceId, _rightDeviceId, StringComparison.OrdinalIgnoreCase);

                        if (!sideMappingLocked)
                        {
                            if (rxSentinel)
                                _leftDeviceId = deviceId;   // R slot unused -> L Joy-Con
                            else if (lxSentinel)
                                _rightDeviceId = deviceId;  // L slot unused -> R Joy-Con
                        }

                        // Log ONCE per device - exactly when dump count reaches 3
                        dumpCounts.TryGetValue(deviceId, out int dc);
                        if (dc == 3 && !stickLoggedDevices.Contains(deviceId)) // fire exactly once
                        {
                            string sideTag = deviceId == _leftDeviceId && deviceId == _rightDeviceId ? "S"
                                : deviceId == _leftDeviceId ? "L"
                                : deviceId == _rightDeviceId ? "R"
                                : "?";
                            int lxC = state.LeftStickX, lyC = state.LeftStickY;
                            int rxC = state.RightStickX, ryC = state.RightStickY;
                            try { BeginInvoke(() => DevLog(
                                $"STICK[{shortId}]({sideTag}) LX={lxC} LY={lyC} RX={rxC} RY={ryC}",
                                Color.FromArgb(100, 200, 255))); } catch { }
                            stickLoggedDevices.Add(deviceId);  // never fire again for this device
                        }

                        // â”€â”€ Warmup gate: suppress buttons until controller has settled â”€â”€
                        warmupReports.TryGetValue(deviceId, out int wc);
                        warmupReports[deviceId] = wc + 1;
                        if (wc < WARMUP_REPORTS)
                            state.Buttons = 0;  // discard init-burst garbage

                        // Per-device button debounce (word-level): accept a new state only
                        // after BUTTON_DEBOUNCE_REPORTS consecutive identical reports to
                        // reduce phantom press/release spikes. For a newly seen device
                        // (or just after warmup), the debounced state starts at 0; a held
                        // button therefore takes one additional report to be adopted.
                        if (!buttonCandidate.TryGetValue(deviceId, out uint candidate) || candidate != state.Buttons)
                        {
                            buttonCandidate[deviceId] = state.Buttons;
                            buttonStableCounts[deviceId] = 1;
                        }
                        else
                        {
                            int count = buttonStableCounts.TryGetValue(deviceId, out var prevCount) ? prevCount : 1;
                            if (count < BUTTON_DEBOUNCE_REPORTS)
                                count++;
                            buttonStableCounts[deviceId] = count;
                        }

                        if (!buttonDebounced.TryGetValue(deviceId, out uint stableButtons))
                            stableButtons = 0;

                        if (buttonStableCounts.TryGetValue(deviceId, out int stableCount) && stableCount >= BUTTON_DEBOUNCE_REPORTS)
                        {
                            stableButtons = buttonCandidate[deviceId];
                            buttonDebounced[deviceId] = stableButtons;
                        }

                        state.Buttons = stableButtons;

                        _deviceStates[deviceId] = state;

                        ApplyMouseModeFromDevice(deviceId, data, state);

                        // â”€â”€ Debug: log only when buttons change or stick moves >80 counts â”€â”€
                        // (Never BeginInvoke on every report â€” that floods the UI thread queue)
                        if (enableVerboseInputLog && IsHandleCreated)
                        {
                        lastButtons.TryGetValue(deviceId, out uint prevBtn);
                        lastStickLog.TryGetValue(deviceId, out var prevStick);
                        int lxN = state.LeftStickX, lyN = state.LeftStickY;
                        int rxN = state.RightStickX, ryN = state.RightStickY;
                        bool btnChanged   = state.Buttons != prevBtn;
                        bool stickChanged = Math.Abs(lxN - prevStick.lx) > 80
                                         || Math.Abs(lyN - prevStick.ly) > 80
                                         || Math.Abs(rxN - prevStick.rx) > 80
                                         || Math.Abs(ryN - prevStick.ry) > 80;

                        if (btnChanged || stickChanged)
                        {
                            lastButtons[deviceId]  = state.Buttons;
                            lastStickLog[deviceId] = (lxN, lyN, rxN, ryN);

                            try
                            {
                                string sid   = deviceId.Length > 8 ? deviceId[..8] : deviceId;
                                string side2 = deviceId == _leftDeviceId && deviceId == _rightDeviceId ? "S"
                                             : deviceId == _leftDeviceId  ? "L"
                                             : deviceId == _rightDeviceId ? "R" : "?";

                                var btns = new System.Text.StringBuilder();
                                void B(string n, SW2Button bit) { if (state.IsPressed(bit)) { if (btns.Length > 0) btns.Append(' '); btns.Append(n); } }
                                B("Up",SW2Button.Up); B("Dn",SW2Button.Down);
                                B("Lt",SW2Button.Left); B("Rt",SW2Button.Right);
                                B("A",SW2Button.A); B("B",SW2Button.B);
                                B("X",SW2Button.X); B("Y",SW2Button.Y);
                                B("L",SW2Button.L); B("ZL",SW2Button.ZL);
                                B("R",SW2Button.R); B("ZR",SW2Button.ZR);
                                B("+",SW2Button.Plus); B("-",SW2Button.Minus);
                                B("Home",SW2Button.Home); B("Cap",SW2Button.Capture);
                                B("C",SW2Button.C);
                                B("LS",SW2Button.LStick); B("RS",SW2Button.RStick);
                                string btnStr = btns.Length > 0 ? btns.ToString() : ".";

                                static string SD(int v, bool isY) {
                                    int d = v - 1998;
                                    if (Math.Abs(d) < 200) return $"ctr";
                                    if (isY) return d > 0 ? $"UP({v})" : $"DN({v})";
                                    return d > 0 ? $"RT({v})" : $"LT({v})";
                                }
                                string lxS = side2=="R" ? "---" : SD(lxN, false);
                                string lyS = side2=="R" ? "---" : SD(lyN, true);
                                string rxS = side2=="L" ? "---" : SD(rxN, false);
                                string ryS = side2=="L" ? "---" : SD(ryN, true);

                                string line = $"[{sid}]({side2}) [{btnStr}]  L:{lxS}/{lyS}  R:{rxS}/{ryS}";
                                BeginInvoke(() => DevLog(line, btnChanged
                                    ? Color.FromArgb(120, 255, 120)
                                    : Color.FromArgb(180, 230, 180)));
                            }
                            catch { }
                        }
                        }

                        // â”€â”€ ViGEm â€” direct call, NOT via BeginInvoke â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                        var merged = MergeDeviceStates();
                        lock (_outputStateLock)
                        {
                            _latestMergedState = CloneState(merged);
                            _latestMergedStateAvailable = true;
                        }
                        _lastState = merged;

                        // â”€â”€ UI update â€” only when merged state actually changed â”€
                        if (IsHandleCreated)
                        {
                        bool mergedChanged = lastMerged == null
                            || merged.Buttons    != lastMerged.Buttons
                            || Math.Abs(merged.LeftStickX  - lastMerged.LeftStickX)  > 5
                            || Math.Abs(merged.LeftStickY  - lastMerged.LeftStickY)  > 5
                            || Math.Abs(merged.RightStickX - lastMerged.RightStickX) > 5
                            || Math.Abs(merged.RightStickY - lastMerged.RightStickY) > 5;
                        if (mergedChanged)
                        {
                            lastMerged = merged;
                            try { BeginInvoke(() => UpdateInputDisplay(merged)); } catch { }
                        }
                        }
                    }
                    finally
                    {
                        double processMs = (Stopwatch.GetTimestamp() - diagArrivalTicks) * 1000.0 / Stopwatch.Frequency;
                        RecordDiagnosticSample(deviceId, diagArrivalTicks, processMs, decodeOk, sizeLooksValid);
                    }
                };

                using var scanTimeout = new CancellationTokenSource(30_000);
                var scanReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // â”€â”€ Immediate status update when a device connects â”€â”€â”€â”€â”€â”€â”€â”€
                // DeviceConnected fires during ScanAsync (not after), so the UI
                // shows "Connected" the moment GATT subscription succeeds.
                scanner.DeviceConnected += (deviceId, devName) =>
                {
                    lock (deviceReportLock)
                        lastReportByDevice[deviceId] = DateTime.UtcNow;

                    try
                    {
                        BeginInvoke(() =>
                        {
                            string shortName = devName.Length > 0 ? devName : deviceId[..Math.Min(12, deviceId.Length)];
                            _lblJoyconStatus.Text      = $"Connected: {shortName}";
                            _lblJoyconStatus.ForeColor = GREEN;
                            Log($"[OK] Connected: {shortName}", GREEN);
                            RefreshDeviceTargetOptions(scanner);

                            int knownCount = scanner.GetKnownDeviceIds().Length;
                            if (knownCount >= 2)
                            {
                                UpdateConnectModeStatusLabel("Pair ready (2/2)", GREEN);
                                scanReadySignal.TrySetResult(true);
                            }
                            else
                            {
                                UpdateConnectModeStatusLabel($"Pairing ({knownCount}/2)", YELLOW);
                            }
                        });
                    }
                    catch { }
                };

                // â”€â”€ Scan â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                Invoke(() =>
                {
                    _lblJoyconStatus.Text      = "Scanning...";
                    _lblJoyconStatus.ForeColor = YELLOW;
                    UpdateConnectModeStatusLabel("Scanning", YELLOW);
                    Log("Checking paired devices + scanning for advertising ones...", ACCENT);
                    Log("Tip: Joy-Con 2 already paired in Windows? - it will connect automatically.", TXT_DIM);
                });

                using var linkedScan  = CancellationTokenSource.CreateLinkedTokenSource(ct, scanTimeout.Token);
                var scanTask = scanner.ScanAsync(linkedScan.Token);
                try
                {
                    var completed = await Task.WhenAny(scanTask, scanReadySignal.Task);
                    if (completed == scanReadySignal.Task)
                    {
                        scanTimeout.Cancel();
                        await Task.Delay(120, ct);
                    }
                    else
                    {
                        await scanTask;
                    }
                }
                catch
                {
                    // scan cancelled or timed out
                }

                if (ct.IsCancellationRequested) break;

                var ids = scanner.GetKnownDeviceIds();
                if (ids.Length == 0)
                {
                    Invoke(() =>
                    {
                        Log("No controllers found - retrying in 3 s...", YELLOW);
                        Log("Make sure Joy-Con 2 is paired in Windows Bluetooth Settings.", TXT_DIM);
                        _lblJoyconStatus.Text      = "Retrying...";
                        _lblJoyconStatus.ForeColor = YELLOW;
                        UpdateConnectModeStatusLabel("No controller, retrying", YELLOW);
                    });
                    try { await Task.Delay(3_000, ct); } catch { break; }
                    continue;
                }
                // â”€â”€ Assign L / R IDs from PnP (definitive, overrides early guess) â”€
                AssignDeviceIds(scanner, ids);

                Invoke(() =>
                {
                    bool dual = _leftDeviceId != null && _rightDeviceId != null && _leftDeviceId != _rightDeviceId;
                    bool single = !dual && (_leftDeviceId != null || _rightDeviceId != null);
                    string lId = _leftDeviceId?[..Math.Min(8, _leftDeviceId.Length)] ?? "?";
                    string rId = _rightDeviceId?[..Math.Min(8, _rightDeviceId.Length)] ?? "?";
                    string msg = dual
                        ? $"Joy-Con pair connected! L={lId}  R={rId}"
                        : $"Joy-Con 2 connected ({ids[0][..Math.Min(12, ids[0].Length)]})";
                    Log(msg, GREEN);
                    _lblJoyconStatus.Text = dual ? "Joy-Con Pair Connected [OK]" : "Joy-Con Single Connected [OK]";
                    _lblJoyconStatus.ForeColor = GREEN;
                    UpdateConnectModeStatusLabel(single ? "Single ready" : "Pair ready", GREEN);
                    RefreshDeviceTargetOptions(scanner);
                });

                var sortedIds = SortDeviceIdsForPlayerOrder(scanner, ids);
                DevLog("Connect flow: joycon2cpp-style init -> input mode -> LED -> sound", TXT_DIM);

                // â”€â”€ Post-connect init: switch to continuous full-rate reporting â”€
                // Win10 typically needs a longer settle delay and more retries than Win11.
                var inputModeInit = GetInputModeInitProfile();
                try { await Task.Delay(inputModeInit.InitialDelayMs, ct); } catch { break; }
                Invoke(() => UpdateConnectModeStatusLabel("Initializing command channel", ACCENT));
                await InitializeCommandChannelAsync(scanner, sortedIds, ct);
                Invoke(() => UpdateConnectModeStatusLabel("Initializing input stream", ACCENT));
                await ConfigureContinuousInputModeAsync(scanner, sortedIds, inputModeInit, ct);
                Invoke(() => UpdateConnectModeStatusLabel("Initializing feedback", ACCENT));
                await ApplyConnectFeedbackAsync(scanner, sortedIds, ct);
                Invoke(() => UpdateConnectModeStatusLabel("Ready", GREEN));

                // â”€â”€ Wait: stay here until all devices disconnect or user stops â”€
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(250, ct); } catch { break; }

                    var knownIds = scanner.GetKnownDeviceIds();
                    int knownCount = knownIds.Length;
                    if (knownCount <= 0)
                        break;

                    bool dualExpected = _leftDeviceId != null
                        && _rightDeviceId != null
                        && _leftDeviceId != _rightDeviceId;

                    if (dualExpected)
                    {
                        var now = DateTime.UtcNow;
                        var staleTargets = new List<string>();
                        string[] criticalIds = new[] { _leftDeviceId!, _rightDeviceId! };

                        lock (deviceReportLock)
                        {
                            foreach (var id in criticalIds.Distinct())
                            {
                                if (!knownIds.Contains(id))
                                    continue;

                                DateTime seen = lastReportByDevice.TryGetValue(id, out var t) ? t : lastReportUtc;
                                if ((now - seen) >= TimeSpan.FromMilliseconds(2800))
                                    staleTargets.Add(id);
                            }
                        }

                        if (staleTargets.Count > 0 && (now - lastInputModeRecoveryUtc) >= TimeSpan.FromSeconds(4))
                        {
                            lastInputModeRecoveryUtc = now;
                            string staleText = string.Join(",", staleTargets.Select(id => id[..Math.Min(8, id.Length)]));
                            Invoke(() => DevLog($"Stale input detected ({staleText}) -> reapplying input mode", YELLOW));

                            try
                            {
                                await ConfigureContinuousInputModeAsync(scanner, staleTargets.ToArray(), GetInputModeRecoveryProfile(), ct);
                            }
                            catch
                            {
                                // Recovery write path is best-effort; fall through to silence checks.
                            }
                        }
                    }

                    var silence = DateTime.UtcNow - lastReportUtc;
                    if (silence >= TimeSpan.FromSeconds(6))
                    {
                        Invoke(() =>
                        {
                            Log($"BLE link silent for {silence.TotalSeconds:F1}s with {knownCount} known device(s) - forcing reconnect.", YELLOW);
                            _lblJoyconStatus.Text      = "Link silent - reconnecting...";
                            _lblJoyconStatus.ForeColor = YELLOW;
                            UpdateConnectModeStatusLabel("Link silent, reconnecting", YELLOW);
                        });
                        scanner.DisconnectAll();
                        break;
                    }
                }

                if (ct.IsCancellationRequested) break;

                // All devices disconnected - attempt reconnect
                Invoke(() =>
                {
                    Log("Joy-Con disconnected - reconnecting...", YELLOW);
                    _lblJoyconStatus.Text      = "Reconnecting...";
                    _lblJoyconStatus.ForeColor = YELLOW;
                    UpdateConnectModeStatusLabel("Disconnected, reconnecting", YELLOW);
                });
                try { await Task.Delay(2_000, ct); } catch { break; }
            }

            Invoke(() =>
            {
                _lblJoyconStatus.Text      = "Stopped";
                _lblJoyconStatus.ForeColor = TXT_DIM;
                UpdateConnectModeStatusLabel("Stopped", TXT_DIM);
            });
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
#if INTHEHAND
            if (e.Mode != PowerModes.Resume || !_running || IsDisposed)
                return;

            try
            {
                BeginInvoke(() =>
                {
                    Log("System resume detected - auto reconnecting BLE...", YELLOW);
                    OnReconnectClicked();
                });
            }
            catch
            {
                // Ignore UI-thread race on shutdown.
            }
#endif
        }

        /// <summary>
        /// After scan, confirm _leftDeviceId / _rightDeviceId.
        /// Priority: (1) PnP product ID, (2) sentinel-based detection already done in
        /// RawReportReceived, (3) arbitrary fallback for single device.
        /// </summary>
        private void AssignDeviceIds(BLEScanner scanner, string[] ids)
        {
            // Pass 0: device name â€” most reliable (Windows names: "Joy-Con 2 (L)" / "Joy-Con 2 (R)")
            string? newLeft = null, newRight = null;
            foreach (var id in ids)
            {
                string n = scanner.GetDeviceName(id);
                Console.WriteLine($"[Assign] id={id[..Math.Min(8,id.Length)]} name='{n}' pid=0x{scanner.GetProductId(id):X4}");
                if (n.Contains("(L)", StringComparison.OrdinalIgnoreCase)) newLeft  = id;
                else if (n.Contains("(R)", StringComparison.OrdinalIgnoreCase)) newRight = id;
            }

            // Pass 1: PnP IDs (sometimes 0x0000 for NS2 over BLE, but try anyway)
            foreach (var id in ids)
            {
                ushort pid = scanner.GetProductId(id);
                if (pid == BLEScanner.PID_JOYCON_L && newLeft  == null) newLeft  = id;
                else if (pid == BLEScanner.PID_JOYCON_R && newRight == null) newRight = id;
            }

            // Pass 2: use sentinel-based IDs set during RawReportReceived.
            // Ignore invalid transient state where both slots point to same device.
            bool existingDistinct = _leftDeviceId != null
                && _rightDeviceId != null
                && !string.Equals(_leftDeviceId, _rightDeviceId, StringComparison.OrdinalIgnoreCase);
            if (existingDistinct)
            {
                if (newLeft == null) newLeft = _leftDeviceId;
                if (newRight == null) newRight = _rightDeviceId;
            }

            // Pass 3: use persisted raw sentinel flags (set during RawReportReceived).
            // These are reliable because they use the raw lx/rx values before neutralisation.
            // Joy-Con L: its right-stick slot is always sentinel (rxSentinel=true)
            // Joy-Con R: its left-stick slot is always sentinel (lxSentinel=true)
            if ((newLeft == null || newRight == null) && ids.Length >= 2)
            {
                foreach (var id in ids)
                {
                    bool lSentinel = _deviceLStickSentinel.TryGetValue(id, out var ls) && ls;
                    bool rSentinel = _deviceRStickSentinel.TryGetValue(id, out var rs) && rs;
                    // R slot sentinel â†’ this is Joy-Con L
                    if (rSentinel && !lSentinel && newLeft  == null) newLeft  = id;
                    // L slot sentinel â†’ this is Joy-Con R
                    if (lSentinel && !rSentinel && newRight == null) newRight = id;
                }
            }

            // Pass 4: single Joy-Con - keep its real side to avoid mapping R stick as L stick.
            if (ids.Length == 1)
            {
                // Only one controller - infer side from name/PnP/sentinel and keep the other side null.
                string solo = ids[0];
                string soloName = scanner.GetDeviceName(solo);

                bool soloIsRight = soloName.Contains("(R)", StringComparison.OrdinalIgnoreCase)
                    || scanner.GetProductId(solo) == BLEScanner.PID_JOYCON_R
                    || (_deviceLStickSentinel.TryGetValue(solo, out var lSent) && lSent);

                if (soloIsRight)
                {
                    newRight = solo;
                    newLeft = null;
                }
                else
                {
                    newLeft = solo;
                    newRight = null;
                }
                Console.WriteLine($"[Assign] Single Joy-Con mode: {soloName} (L={newLeft?[..Math.Min(8, newLeft.Length)] ?? "-"}, R={newRight?[..Math.Min(8, newRight.Length)] ?? "-"})");
            }
            else
            {
                // Two or more: leave unresolved slots as null.
                // RawReportReceived sentinel detection fills them in from the first reports.
                // DO NOT assign same ID to both slots (isSingleDevice=true causes RS jitter).
                if (newLeft == null && newRight == null && ids.Length >= 2)
                    Console.WriteLine("[Assign] Both IDs unresolved; sentinel detection will complete assignment.");

            }
            _leftDeviceId  = newLeft;
            _rightDeviceId = newRight;
        }

        private readonly struct InputModeInitProfile
        {
            public InputModeInitProfile(int initialDelayMs, int attempts, int retryDelayMs)
            {
                InitialDelayMs = initialDelayMs;
                Attempts = attempts;
                RetryDelayMs = retryDelayMs;
            }

            public int InitialDelayMs { get; }
            public int Attempts { get; }
            public int RetryDelayMs { get; }
        }

        private static InputModeInitProfile GetInputModeInitProfile()
        {
            // Win11 BLE stack is generally faster to accept 0x03/0x3F input-mode command.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return new InputModeInitProfile(initialDelayMs: 320, attempts: 3, retryDelayMs: 120);

            // Win10 often ignores early mode-switch writes; favor reliability over startup speed.
            return new InputModeInitProfile(initialDelayMs: 700, attempts: 6, retryDelayMs: 180);
        }

        private static InputModeInitProfile GetInputModeRecoveryProfile()
        {
            // Recovery path runs while streaming; use short retries to avoid visible stalls.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return new InputModeInitProfile(initialDelayMs: 0, attempts: 2, retryDelayMs: 90);

            return new InputModeInitProfile(initialDelayMs: 0, attempts: 3, retryDelayMs: 120);
        }

        private async Task ConfigureContinuousInputModeAsync(BLEScanner scanner, string[] deviceIds, InputModeInitProfile profile, CancellationToken ct)
        {
            const byte mode = 0x3F;
            int attempts = Math.Max(1, profile.Attempts);
            int retryDelayMs = Math.Max(50, profile.RetryDelayMs);

            foreach (var id in deviceIds)
            {
                bool success = false;
                for (int attempt = 1; attempt <= attempts && !ct.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        var modeCmd = Joycon2PC.Core.SubcommandBuilder.BuildNS2SetInputModeCompat(mode);
                        success = await scanner.SendSubcommandAsync(id, modeCmd, "set-input-mode-3F-cpp", ct);
                    }
                    catch
                    {
                        success = false;
                    }

                    if (success)
                    {
                        string shortId = id[..Math.Min(8, id.Length)];
                        int a = attempt;
                        Invoke(() => DevLog($"  Input mode 0x{mode:X2} -> {shortId} (attempt {a}/{attempts})", ACCENT));
                        break;
                    }

                    try { await Task.Delay(retryDelayMs, ct); }
                    catch { return; }
                }

                if (!success)
                {
                    string shortId = id[..Math.Min(8, id.Length)];
                    Invoke(() => DevLog($"  Input mode 0x{mode:X2} failed for {shortId}; fallback to default mode", YELLOW));
                }
            }
        }

        private async Task InitializeCommandChannelAsync(BLEScanner scanner, string[] deviceIds, CancellationToken ct)
        {
            var initCommands = Joycon2PC.Core.SubcommandBuilder.BuildNS2CustomInitCommands();

            foreach (var id in deviceIds)
            {
                for (int index = 0; index < initCommands.Length; index++)
                {
                    bool sent = await scanner.SendSubcommandAsync(id, initCommands[index], $"custom-init-{index + 1}", ct);
                    if (!sent)
                        break;

                    if (index < initCommands.Length - 1)
                    {
                        try { await Task.Delay(500, ct); }
                        catch { return; }
                    }
                }
            }
        }

        private async Task ApplyConnectFeedbackAsync(BLEScanner scanner, string[] sortedIds, CancellationToken ct)
        {
            var soundSettings = GetConnectSoundSettings();
            int feedbackAttempts = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 2 : 4;

            async Task<bool> SendWithRetryAsync(string deviceId, byte[] payload, string tag)
            {
                for (int attempt = 1; attempt <= feedbackAttempts && !ct.IsCancellationRequested; attempt++)
                {
                    bool ok;
                    try
                    {
                        ok = await scanner.SendSubcommandAsync(deviceId, payload, tag, ct);
                    }
                    catch
                    {
                        ok = false;
                    }

                    if (ok)
                        return true;

                    if (attempt < feedbackAttempts)
                    {
                        try { await Task.Delay(80 * attempt, ct); }
                        catch { return false; }
                    }
                }

                return false;
            }

            for (int p = 0; p < sortedIds.Length; p++)
            {
                string id = sortedIds[p];
                string side = ResolveDeviceSideLabel(scanner, id);
                int playerNum = CONNECT_FEEDBACK_PLAYER_NUM;

                try
                {
                    // joycon2cpp-exact LED command path.
                    bool ledOk = await SendWithRetryAsync(id, Joycon2PC.Core.SubcommandBuilder.BuildNS2PlayerLedCompat(playerNum), $"led-cpp-p{playerNum}");
                    if (ledOk)
                        Invoke(() => Log($"  Joy-Con {side} LED confirmed (shared P{playerNum})", ACCENT));
                    else
                        Invoke(() => Log($"  Joy-Con {side} LED send failed after retries", YELLOW));

                    if (soundSettings.Enabled)
                    {
                        await Task.Delay(25, ct);
                        bool soundOk = await SendWithRetryAsync(id, Joycon2PC.Core.SubcommandBuilder.BuildNS2SoundCompat(soundSettings.Preset), $"sound-cpp-0x{soundSettings.Preset:X2}");
                        byte preset = soundSettings.Preset;
                        if (soundOk)
                            Invoke(() => Log($"  Joy-Con {side} connect sound sent (0x{preset:X2})", ACCENT));
                        else
                            Invoke(() => Log($"  Joy-Con {side} connect sound failed (0x{preset:X2})", YELLOW));
                    }
                }
                catch (Exception ex)
                {
                    Invoke(() => Log($"Connect feedback failed for Joy-Con {side}: {ex.Message}", YELLOW));
                }
            }
        }

        private string[] SortDeviceIdsForPlayerOrder(BLEScanner scanner, string[] ids)
            => ids.OrderBy(id => GetSideSortKey(scanner, id)).ThenBy(id => id).ToArray();

        private int GetSideSortKey(BLEScanner scanner, string deviceId)
        {
            string side = ResolveDeviceSideLabel(scanner, deviceId);
            return side switch
            {
                "L" => 0,
                "R" => 1,
                _ => 2,
            };
        }

        private string ResolveDeviceSideLabel(BLEScanner scanner, string deviceId)
        {
            string name = scanner.GetDeviceName(deviceId);
            if (name.Contains("(L)", StringComparison.OrdinalIgnoreCase)) return "L";
            if (name.Contains("(R)", StringComparison.OrdinalIgnoreCase)) return "R";

            ushort pid = scanner.GetProductId(deviceId);
            if (pid == BLEScanner.PID_JOYCON_L) return "L";
            if (pid == BLEScanner.PID_JOYCON_R) return "R";

            if (string.Equals(deviceId, _leftDeviceId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(deviceId, _rightDeviceId, StringComparison.OrdinalIgnoreCase))
                return "L";

            if (string.Equals(deviceId, _rightDeviceId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(deviceId, _leftDeviceId, StringComparison.OrdinalIgnoreCase))
                return "R";

            return "Unknown";
        }

        /// <summary>
        /// Merge states from all connected devices into one combined JoyconState.
        ///
        /// Buttons  â€” OR'd from every device (each Joy-Con carries its own half of the buttons).
        /// L Stick  â€” from the device identified as Joy-Con L (bytes [10..12] of its report).
        /// R Stick  â€” from the device identified as Joy-Con R.
        ///             Joy-Con R sends its physical stick at the RIGHT-stick bytes [13..15];
        ///             if those are at centre (1998) we also try the left-stick bytes [10..12]
        ///             as a fallback in case the protocol puts it there on some firmware.
        /// </summary>
        private JoyconState MergeDeviceStates()
        {
            // Default sticks to centre so the visualiser and ViGEm start neutral
            var merged = new JoyconState
            {
                LeftStickX  = 1998, LeftStickY  = 1998,
                RightStickX = 1998, RightStickY = 1998,
            };

            if (_deviceStates.Count == 0) return merged;

            // â”€â”€ Dual / single device detection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            bool isSingleDevice = _leftDeviceId != null
                                && _leftDeviceId == _rightDeviceId;
            bool dualMode = !isSingleDevice
                          && _leftDeviceId != null
                          && _rightDeviceId != null;

            // â”€â”€ Buttons â€” side-masked OR in dual mode â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // L Joy-Con owns: ZL/L/D-pad/Minus/LStick/Capture/GripLeft/LSL/LSR
            // R Joy-Con owns: ZR/R/C/ABXY/Plus/Home/RStick/GripRight/RSL/RSR
            // Masking prevents start-up ghost presses on one side from contaminating
            // the other (e.g. R Joy-Con briefly asserts L+ZL during its BLE init burst,
            // which without masking would forward phantom button presses to ViGEm and
            // trigger the Windows alert SFX / unwanted in-game actions).
            const uint L_MASK = (uint)(
                SW2Button.ZL | SW2Button.L  | SW2Button.LSL | SW2Button.LSR |
                SW2Button.Minus | SW2Button.LStick | SW2Button.Capture | SW2Button.GripLeft |
                SW2Button.Down  | SW2Button.Up | SW2Button.Right | SW2Button.Left);
            const uint R_MASK = (uint)(
                SW2Button.ZR | SW2Button.R   | SW2Button.RSL | SW2Button.RSR |
                SW2Button.Plus | SW2Button.Home | SW2Button.C | SW2Button.RStick | SW2Button.GripRight |
                SW2Button.Y  | SW2Button.X   | SW2Button.B  | SW2Button.A);

            if (dualMode)
            {
                // Each side only contributes buttons it physically owns.
                uint lB = _deviceStates.TryGetValue(_leftDeviceId!,  out var lsBtn) ? lsBtn.Buttons & L_MASK : 0u;
                uint rB = _deviceStates.TryGetValue(_rightDeviceId!, out var rsBtn) ? rsBtn.Buttons & R_MASK : 0u;
                merged.Buttons = lB | rB;
            }
            else if (isSingleDevice)
            {
                // Single mode should only consume the selected device's buttons.
                if (_leftDeviceId != null && _deviceStates.TryGetValue(_leftDeviceId, out var singleState))
                    merged.Buttons = singleState.Buttons;
            }
            else
            {
                // Single controller or IDs not yet assigned â€” OR all buttons as-is.
                foreach (var s in _deviceStates.Values)
                    merged.Buttons |= s.Buttons;
            }

            if (isSingleDevice)
            {
                // Single controller â€” use whichever stick bytes are NOT at sentinel (1998).
                // Joy-Con L: real data on LeftStick bytes [10..12], RightStick = 1998
                // Joy-Con R: real data on RightStick bytes [13..15], LeftStick = 1998
                if (_deviceStates.TryGetValue(_leftDeviceId!, out var s))
                {
                    bool lReal = Math.Abs(s.LeftStickX  - 1998) > 50 || Math.Abs(s.LeftStickY  - 1998) > 50;
                    bool rReal = Math.Abs(s.RightStickX - 1998) > 50 || Math.Abs(s.RightStickY - 1998) > 50;

                    if (rReal && !lReal)
                    {
                        // Joy-Con R solo: physical stick â†’ LeftStick output (primary axis for games)
                        merged.LeftStickX  = s.RightStickX;
                        merged.LeftStickY  = s.RightStickY;
                    }
                    else
                    {
                        // Joy-Con L solo or unknown: physical stick â†’ LeftStick output
                        merged.LeftStickX  = s.LeftStickX;
                        merged.LeftStickY  = s.LeftStickY;
                    }
                }
            }
            else if (dualMode)
            {
                // Joy-Con L: its physical stick is always at the LEFT bytes [10..12].
                if (_deviceStates.TryGetValue(_leftDeviceId!, out var ls))
                {
                    merged.LeftStickX = ls.LeftStickX;
                    merged.LeftStickY = ls.LeftStickY;
                }

                // Joy-Con R: its physical stick is at the RIGHT bytes [13..15].
                // When active it reads ~1894-2100 (not exactly 1998).
                // If still at 1998 (sentinel was neutralised in the report handler),
                // the R controller hasn't sent a real value yet â€” leave merged at 1998.
                if (_deviceStates.TryGetValue(_rightDeviceId!, out var rs))
                {
                    // RightStick field is authoritative for Joy-Con R.
                    // LeftStick field on the R device = 1998 (sentinel was neutralised) â€” ignore it.
                    merged.RightStickX = rs.RightStickX;
                    merged.RightStickY = rs.RightStickY;
                }
            }
            else
            {
                // IDs not yet fully assigned â€” output what we have but do NOT clobber IDs here.
                // The report handler will assign the correct IDs when the sentinel is seen.
                // Just pass through whatever data we have so the UI isn't frozen.
                if (_leftDeviceId != null && _rightDeviceId == null && _deviceStates.TryGetValue(_leftDeviceId, out var onlyL))
                {
                    // Single L: keep normal mapping on left stick output.
                    merged.LeftStickX = onlyL.LeftStickX;
                    merged.LeftStickY = onlyL.LeftStickY;
                }
                else if (_rightDeviceId != null && _leftDeviceId == null && _deviceStates.TryGetValue(_rightDeviceId, out var onlyR))
                {
                    // Single R: map its physical stick to LeftStick so most games remain playable.
                    merged.LeftStickX = onlyR.RightStickX;
                    merged.LeftStickY = onlyR.RightStickY;
                }
                else
                {
                    if (_leftDeviceId != null && _deviceStates.TryGetValue(_leftDeviceId, out var partL))
                    {
                        merged.LeftStickX = partL.LeftStickX;
                        merged.LeftStickY = partL.LeftStickY;
                    }
                    if (_rightDeviceId != null && _deviceStates.TryGetValue(_rightDeviceId, out var partR))
                    {
                        merged.RightStickX = partR.RightStickX;
                        merged.RightStickY = partR.RightStickY;
                    }
                }
            }

            return merged;
        }
#endif

        private static bool IsEnabledByEnv(string variableName)
        {
            string? raw = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)) return true;
            return bool.TryParse(raw, out bool enabled) && enabled;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InputUnion
        {
            public MouseInput mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            public uint type;
            public InputUnion U;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, NativeInput[] pInputs, int cbSize);

        private static short ReadInt16LE(byte lo, byte hi)
            => (short)(lo | (hi << 8));

        private float GetMouseSensitivity()
            => _mouseSpeedMode switch
            {
                MouseSpeedMode.Fast => 1.0f,
                MouseSpeedMode.Slow => 0.3f,
                _ => 0.6f,
            };

        private (int deadzone, int clamp, int dispatchIntervalMs, double smoothingAlpha, double minStep) GetMouseStabilizerProfile()
            => _mouseStabilizerMode switch
            {
                MouseStabilizerMode.Raw => (0, 220, 1, 1.00, 0.0),
                MouseStabilizerMode.VeryStable => (4, 48, 10, 0.22, 0.9),
                _ => (2, 96, 6, 0.45, 0.35),
            };

        private void SendMouseButton(uint downFlag, uint upFlag, bool pressed, ref bool previous)
        {
            if (pressed == previous)
                return;

            if (SendMouseInput(0, 0, pressed ? downFlag : upFlag))
                previous = pressed;
        }

        private bool SendMouseInput(int dx, int dy, uint flags)
        {
            var input = new NativeInput
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MouseInput
                    {
                        dx = dx,
                        dy = dy,
                        mouseData = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    }
                }
            };

            return SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>()) != 0;
        }

        private void SendMouseWheel(int wheelDelta)
        {
            var input = new NativeInput
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MouseInput
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = unchecked((uint)wheelDelta),
                        dwFlags = MOUSEEVENTF_WHEEL,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    }
                }
            };

            _ = SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>());
        }

        private void ResetMouseModeState(bool releasePressedButtons)
        {
            _mouseFirstOpticalRead = true;
            _mouseLastOpticalX = 0;
            _mouseLastOpticalY = 0;
            _mousePendingMoveX = 0;
            _mousePendingMoveY = 0;
            _mouseFilteredDx = 0;
            _mouseFilteredDy = 0;
            _mouseScrollAccumulator = 0;
            _mouseLastOpticalReportUtc = DateTime.MinValue;
            _mouseLastMoveUtc = DateTime.MinValue;
            _mouseMiddleStableCount = 0;
            _mouseMiddleDebouncedPressed = false;

            if (releasePressedButtons)
            {
                SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, false, ref _mouseLeftPressed);
                SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, false, ref _mouseRightPressed);
                SendMouseButton(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, false, ref _mouseMiddlePressed);
            }
            else
            {
                _mouseLeftPressed = false;
                _mouseRightPressed = false;
                _mouseMiddlePressed = false;
            }
        }

        private void ApplyMouseModeFromDevice(string deviceId, byte[] data, JoyconState state)
        {
            lock (_mouseStateLock)
            {
                if (!_mouseModeEnabled)
                    return;

                if (string.IsNullOrWhiteSpace(_rightDeviceId))
                    return;

                // Dual-device mode: hard-lock mouse source to the assigned right Joy-Con.
                if (!string.Equals(deviceId, _rightDeviceId, StringComparison.OrdinalIgnoreCase))
                    return;

                int off = data.Length > 0 && data[0] == 0xA1 ? 1 : 0;
                if (data.Length < off + 0x14)
                    return;

                short rawX = ReadInt16LE(data[off + 0x10], data[off + 0x11]);
                short rawY = ReadInt16LE(data[off + 0x12], data[off + 0x13]);
                var now = DateTime.UtcNow;

                if (_mouseFirstOpticalRead)
                {
                    _mouseLastOpticalX = rawX;
                    _mouseLastOpticalY = rawY;
                    _mouseLastOpticalReportUtc = now;
                    _mouseFirstOpticalRead = false;
                    return;
                }

                double reportGapMs = _mouseLastOpticalReportUtc == DateTime.MinValue
                    ? 0
                    : (now - _mouseLastOpticalReportUtc).TotalMilliseconds;
                _mouseLastOpticalReportUtc = now;

                int dx = rawX - _mouseLastOpticalX;
                int dy = rawY - _mouseLastOpticalY;
                _mouseLastOpticalX = rawX;
                _mouseLastOpticalY = rawY;

                var profile = GetMouseStabilizerProfile();

                // Reject tiny optical noise and clamp spikes from occasional sensor bursts.
                if (Math.Abs(dx) <= profile.deadzone)
                    dx = 0;
                if (Math.Abs(dy) <= profile.deadzone)
                    dy = 0;

                dx = Math.Clamp(dx, -profile.clamp, profile.clamp);
                dy = Math.Clamp(dy, -profile.clamp, profile.clamp);

                // Exponential smoothing gives a visible difference across stabilizer levels.
                // For medium/fast motion, boost alpha to reduce perceived trailing.
                int opticalMagnitude = Math.Abs(dx) + Math.Abs(dy);
                double dynamicAlpha = profile.smoothingAlpha;
                if (opticalMagnitude >= 18)
                    dynamicAlpha = Math.Min(1.0, dynamicAlpha + 0.20);
                if (opticalMagnitude >= 36)
                    dynamicAlpha = Math.Min(1.0, dynamicAlpha + 0.20);

                _mouseFilteredDx += (dx - _mouseFilteredDx) * dynamicAlpha;
                _mouseFilteredDy += (dy - _mouseFilteredDy) * dynamicAlpha;

                float sensitivity = GetMouseSensitivity();
                _mousePendingMoveX += _mouseFilteredDx * sensitivity;
                _mousePendingMoveY += _mouseFilteredDy * sensitivity;

                if ((now - _mouseLastMoveUtc).TotalMilliseconds >= profile.dispatchIntervalMs)
                {
                    int moveX = (int)Math.Truncate(_mousePendingMoveX);
                    int moveY = (int)Math.Truncate(_mousePendingMoveY);

                    if (Math.Abs(_mousePendingMoveX) < profile.minStep) moveX = 0;
                    if (Math.Abs(_mousePendingMoveY) < profile.minStep) moveY = 0;

                    if (moveX != 0 || moveY != 0)
                    {
                        _mousePendingMoveX -= moveX;
                        _mousePendingMoveY -= moveY;
                        DispatchMouseMoveInterpolated(moveX, moveY, reportGapMs);
                    }
                    _mouseLastMoveUtc = now;
                }

                // cpp mapping: R=left click, ZR=right click, RStick=middle click.
                SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, state.IsPressed(SW2Button.R), ref _mouseLeftPressed);
                SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, state.IsPressed(SW2Button.ZR), ref _mouseRightPressed);

                // RS click can chatter on some devices; require 3 stable reports before toggling middle click.
                bool middleRawPressed = state.IsPressed(SW2Button.RStick);
                if (middleRawPressed == _mouseMiddleDebouncedPressed)
                {
                    _mouseMiddleStableCount = 0;
                }
                else
                {
                    _mouseMiddleStableCount++;
                    if (_mouseMiddleStableCount >= 3)
                    {
                        _mouseMiddleDebouncedPressed = middleRawPressed;
                        _mouseMiddleStableCount = 0;
                    }
                }

                SendMouseButton(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, _mouseMiddleDebouncedPressed, ref _mouseMiddlePressed);

                // Right stick vertical axis -> mouse wheel scrolling.
                int stickY = state.RightStickY - NS2InputReportDecoder.NeutralStickValue;
                if (Math.Abs(stickY) < MOUSE_SCROLL_STICK_DEADZONE)
                    stickY = 0;

                if (stickY != 0)
                {
                    // Up on stick (higher raw Y) â†’ positive wheel delta = scroll up in Windows.
                    _mouseScrollAccumulator += stickY * MOUSE_SCROLL_GAIN;

                    int clicks = (int)Math.Truncate(_mouseScrollAccumulator);
                    if (clicks != 0)
                    {
                        _mouseScrollAccumulator -= clicks;
                        SendMouseWheel(clicks * MOUSE_WHEEL_DELTA);
                    }
                }
            }
        }

        private void DispatchMouseMoveInterpolated(int moveX, int moveY, double reportGapMs)
        {
            int steps = 1;

            // BLE optical reports can be sparse; split one big jump into micro-steps
            // so cursor motion appears less stuttery to the eye.
            if (reportGapMs >= 30)
                steps = 3;
            else if (reportGapMs >= 20)
                steps = 2;

            if (steps == 1 && (Math.Abs(moveX) + Math.Abs(moveY) >= 18))
                steps = 2;

            int remainingX = moveX;
            int remainingY = moveY;
            for (int i = 0; i < steps; i++)
            {
                int slots = steps - i;
                int stepX = (int)Math.Round(remainingX / (double)slots);
                int stepY = (int)Math.Round(remainingY / (double)slots);

                remainingX -= stepX;
                remainingY -= stepY;

                if (stepX != 0 || stepY != 0)
                    SendMouseInput(stepX, stepY, MOUSEEVENTF_MOVE);
            }
        }

        private void InitializeOptionControls()
        {
            UpdateConnectModeStatusLabel("Idle", TXT_DIM);

            _cmbDeviceTarget.Items.Clear();
            _cmbDeviceTarget.Items.Add(new DeviceTargetOption { Label = "Connected pair", DeviceId = null });
            _cmbDeviceTarget.SelectedIndex = 0;

            _cmbSoundPreset.Items.AddRange(new object[]
            {
                new ByteOption { Label = "0x04 Connect (default)", Value = 0x04 },
                new ByteOption { Label = "0x01 Click / tap",        Value = 0x01 },
                new ByteOption { Label = "0x02 Low battery alert",  Value = 0x02 },
                new ByteOption { Label = "0x03 Reconnected",         Value = 0x03 },
                                new ByteOption { Label = "0x05 Reconnected (alt)",   Value = 0x05 },
                                new ByteOption { Label = "0x06 Short high beep A",    Value = 0x06 },
                                new ByteOption { Label = "0x07 Short high beep B",    Value = 0x07 },
                                new ByteOption { Label = "0x08 Experimental",         Value = 0x08 },
                                new ByteOption { Label = "0x09 Experimental",         Value = 0x09 },
            });
            _cmbSoundPreset.SelectedIndex = 0;

            _cmbLedPattern.Items.AddRange(new object[]
            {
                new ByteOption { Label = "0x00 Off", Value = 0x00 },
                new ByteOption { Label = "0x01 Player 1", Value = 0x01 },
                new ByteOption { Label = "0x02 Player 2", Value = 0x02 },
                new ByteOption { Label = "0x04 Player 3", Value = 0x04 },
                new ByteOption { Label = "0x08 Player 4", Value = 0x08 },
                new ByteOption { Label = "0x03 1+2", Value = 0x03 },
                new ByteOption { Label = "0x06 2+3", Value = 0x06 },
                new ByteOption { Label = "0x0C 3+4", Value = 0x0C },
                new ByteOption { Label = "0x0F All", Value = 0x0F },
            });
            _cmbLedPattern.SelectedIndex = 1;

        }

        private void RefreshDeviceTargetOptions(BLEScanner? scanner)
        {
            if (_cmbDeviceTarget == null || _cmbDeviceTarget.IsDisposed)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(() => RefreshDeviceTargetOptions(scanner));
                return;
            }

            string? previousId = (_cmbDeviceTarget.SelectedItem as DeviceTargetOption)?.DeviceId;
            _cmbDeviceTarget.Items.Clear();
            _cmbDeviceTarget.Items.Add(new DeviceTargetOption { Label = "Connected setup (pair/single)", DeviceId = null });

            if (scanner != null)
            {
                var ids = SortDeviceIdsForPlayerOrder(scanner, scanner.GetKnownDeviceIds());
                foreach (var id in ids)
                {
                    string side = ResolveDeviceSideLabel(scanner, id);
                    string name = scanner.GetDeviceName(id);
                    string label = string.IsNullOrWhiteSpace(name)
                        ? $"Joy-Con {side}"
                        : $"Joy-Con {side} - {name}";
                    _cmbDeviceTarget.Items.Add(new DeviceTargetOption { Label = label, DeviceId = id });
                }
            }

            int selectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(previousId))
            {
                for (int index = 0; index < _cmbDeviceTarget.Items.Count; index++)
                {
                    if ((_cmbDeviceTarget.Items[index] as DeviceTargetOption)?.DeviceId == previousId)
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }
            _cmbDeviceTarget.SelectedIndex = Math.Min(selectedIndex, _cmbDeviceTarget.Items.Count - 1);
        }

        private string[] GetSelectedTargetDeviceIds(BLEScanner scanner)
        {
            var selected = _cmbDeviceTarget?.SelectedItem as DeviceTargetOption;
            if (selected?.DeviceId == null)
                return SortDeviceIdsForPlayerOrder(scanner, scanner.GetKnownDeviceIds());

            return new[] { selected.DeviceId };
        }

        private byte GetSelectedSoundPreset()
            => (_cmbSoundPreset?.SelectedItem as ByteOption)?.Value ?? 0x04;

        private byte GetSelectedLedPattern()
            => (_cmbLedPattern?.SelectedItem as ByteOption)?.Value ?? 0x01;

        private (bool Enabled, byte Preset) GetConnectSoundSettings()
        {
            if (InvokeRequired)
            {
                return (ValueTuple<bool, byte>)Invoke(new Func<(bool, byte)>(GetConnectSoundSettings));
            }

            bool enabled = _chkConnectSound?.Checked ?? false;
            byte preset = GetSelectedSoundPreset();
            return (enabled, preset);
        }

        private async Task TriggerManualSoundTestAsync()
        {
            var scanner = _scanner;
            if (scanner == null)
            {
                Log("No active BLE session. Connect Joy-Con first.", YELLOW);
                return;
            }

            var targetIds = GetSelectedTargetDeviceIds(scanner);
            if (targetIds.Length == 0)
            {
                Log("No connected Joy-Con available for sound test.", YELLOW);
                return;
            }

            byte preset = GetSelectedSoundPreset();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await InitializeCommandChannelAsync(scanner, targetIds, cts.Token);

            foreach (var id in targetIds)
            {
                string side = ResolveDeviceSideLabel(scanner, id);
                bool sent = await scanner.SendSubcommandAsync(
                    id,
                    Joycon2PC.Core.SubcommandBuilder.BuildNS2SoundCompat(preset),
                    $"manual-sound-0x{preset:X2}",
                    cts.Token);

                if (sent)
                    Log($"Manual sound test sent to Joy-Con {side} (0x{preset:X2}).", ACCENT);
            }
        }

        private async Task TriggerManualLedApplyAsync()
        {
            var scanner = _scanner;
            if (scanner == null)
            {
                Log("No active BLE session. Connect Joy-Con first.", YELLOW);
                return;
            }

            var targetIds = GetSelectedTargetDeviceIds(scanner);
            if (targetIds.Length == 0)
            {
                Log("No connected Joy-Con available for LED test.", YELLOW);
                return;
            }

            byte pattern = GetSelectedLedPattern();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await InitializeCommandChannelAsync(scanner, targetIds, cts.Token);

            foreach (var id in targetIds)
            {
                string side = ResolveDeviceSideLabel(scanner, id);
                bool sent = await scanner.SendSubcommandAsync(
                    id,
                    Joycon2PC.Core.SubcommandBuilder.BuildNS2PlayerLedCompatRaw(pattern),
                    $"manual-led-0x{pattern:X2}",
                    cts.Token);

                if (sent)
                    Log($"Manual LED applied to Joy-Con {side} (0x{pattern:X2}).", ACCENT);
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  PARSER CALLBACK  (called from background thread)
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        private void OnStateChanged(JoyconState state)
        {
            // forward to ViGEm
            try { _bridge?.UpdateFromState(state); } catch { }

            _lastState = state;

            // update UI on the UI thread
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(() => UpdateInputDisplay(state));
            }
            catch { }
        }

        private void UpdateInputDisplay(JoyconState state)
        {
            // â”€â”€ Stick visualisers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            _joyconViz?.SetSticks(state.LeftStickX, state.LeftStickY, state.RightStickX, state.RightStickY);

            // â”€â”€ Button indicators â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            SetBtn("A",    state.IsPressed(SW2Button.A));
            SetBtn("B",    state.IsPressed(SW2Button.B));
            SetBtn("X",    state.IsPressed(SW2Button.X));
            SetBtn("Y",    state.IsPressed(SW2Button.Y));
            SetBtn("R",    state.IsPressed(SW2Button.R));
            SetBtn("ZR",   state.IsPressed(SW2Button.ZR));
            SetBtn("L",    state.IsPressed(SW2Button.L));
            SetBtn("ZL",   state.IsPressed(SW2Button.ZL));
            SetBtn("+",    state.IsPressed(SW2Button.Plus));
            SetBtn("-",    state.IsPressed(SW2Button.Minus));
            SetBtn("Home", state.IsPressed(SW2Button.Home));
            SetBtn("Cap",  state.IsPressed(SW2Button.Capture));
            SetBtn("C",    state.IsPressed(SW2Button.C));
            SetBtn("LS",   state.IsPressed(SW2Button.LStick));
            SetBtn("RS",   state.IsPressed(SW2Button.RStick));
            SetBtn("Up",   state.IsPressed(SW2Button.Up));
            SetBtn("Dn",   state.IsPressed(SW2Button.Down));
            SetBtn("Lt",   state.IsPressed(SW2Button.Left));
            SetBtn("Rt",   state.IsPressed(SW2Button.Right));
        }

        private void SetBtn(string name, bool pressed)
            => _joyconViz?.SetButton(name, pressed);

        private void DrawStick(Panel panel, int rawX, int rawY)
        {
            // NS2 12-bit raw â†’ normalised -1..1  (factory centre = 1998)
            const float ns2Centre = 1998f;
            const float ns2Range  = 1251f;  // â‰ˆ half of 3249-746, used for symmetry
            float nx = Math.Clamp((rawX - ns2Centre) / ns2Range, -1f, 1f);
            float ny = Math.Clamp((rawY - ns2Centre) / ns2Range, -1f, 1f);

            var bmp = new Bitmap(panel.Width, panel.Height);
            using var g   = Graphics.FromImage(bmp);
            g.Clear(STICK_BG);

            // outer ring
            g.DrawEllipse(new Pen(Color.FromArgb(80, 90, 110), 1),
                1, 1, panel.Width - 3, panel.Height - 3);

            // centre crosshair
            g.DrawLine(new Pen(Color.FromArgb(201, 211, 222), 1),
                panel.Width / 2, 0, panel.Width / 2, panel.Height);
            g.DrawLine(new Pen(Color.FromArgb(201, 211, 222), 1),
                0, panel.Height / 2, panel.Width, panel.Height / 2);

            // dot
            const int r = 7;
            float cx = panel.Width  / 2f + nx * (panel.Width  / 2f - r - 2);
            float cy = panel.Height / 2f - ny * (panel.Height / 2f - r - 2); // Y is inverted in screen coords
            g.FillEllipse(new SolidBrush(ACCENT), cx - r, cy - r, r * 2, r * 2);

            var old = panel.BackgroundImage;
            panel.BackgroundImage = bmp;
            panel.BackgroundImageLayout = ImageLayout.None;
            old?.Dispose();
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  LOGGING
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        private void Log(string text, Color? color = null, LogAudience audience = LogAudience.User)
        {
            if (_log.InvokeRequired) { Invoke(() => Log(text, color, audience)); return; }

            var entry = new LogEntry
            {
                TimeText = DateTime.Now.ToString("HH:mm:ss"),
                Message = text,
                Color = color ?? TXT,
                Audience = audience,
            };

            _logEntries.Add(entry);
            if (_logEntries.Count > MAX_LOG_ENTRIES)
                _logEntries.RemoveRange(0, _logEntries.Count - MAX_LOG_ENTRIES);

            if (!ShouldDisplay(entry.Audience))
                return;

            AppendLogEntry(entry);
        }

        private void DevLog(string text, Color? color = null)
            => Log(text, color, LogAudience.Developer);

        private bool ShouldDisplay(LogAudience audience)
            => _logMode == LogMode.Developer || audience == LogAudience.User;

        private void RebuildLogView()
        {
            if (_log.InvokeRequired)
            {
                Invoke(RebuildLogView);
                return;
            }

            _log.Clear();
            foreach (var entry in _logEntries)
            {
                if (ShouldDisplay(entry.Audience))
                    AppendLogEntry(entry);
            }
        }

        private void AppendLogEntry(LogEntry entry)
        {
            if (_log.InvokeRequired)
            {
                Invoke(() => AppendLogEntry(entry));
                return;
            }

            _log.SelectionStart  = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor  = TXT_DIM;
            _log.AppendText($"[{entry.TimeText}] ");
            _log.SelectionColor  = entry.Color;
            _log.AppendText(entry.Message + "\n");
            _log.ScrollToCaret();
            TrimLogControlLines();
        }

        private void TrimLogControlLines()
        {
            int overflow = _log.Lines.Length - MAX_LOG_LINES;
            if (overflow <= 0)
                return;

            int removeTo = _log.GetFirstCharIndexFromLine(overflow);
            if (removeTo <= 0)
                return;

            _log.Select(0, removeTo);
            _log.SelectedText = string.Empty;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  FORM CLOSE
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
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

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  JOY-CON 2 VISUALIZER  (custom-drawn GDI+ panel)
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        private sealed class JoyConVisualizerPanel : Panel
        {
            private readonly Dictionary<string, bool> _p = new();
            private float _lx, _ly, _rx, _ry;

            // accent colour when a button is pressed
            private static readonly Dictionary<string, Color> _on = new()
            {
                ["ZL"] = Color.FromArgb(255, 185,   0),
                ["L" ] = Color.FromArgb(255, 185,   0),
                ["ZR"] = Color.FromArgb(255, 185,   0),
                ["R" ] = Color.FromArgb(255, 185,   0),
                ["-" ] = Color.FromArgb(200, 200, 210),
                ["+" ] = Color.FromArgb(200, 200, 210),
                ["LS"] = Color.FromArgb( 80, 160, 235),
                ["RS"] = Color.FromArgb( 80, 160, 235),
                ["A" ] = Color.FromArgb(214,  55,  48),
                ["B" ] = Color.FromArgb(214, 153,  35),
                ["X" ] = Color.FromArgb( 65, 148, 215),
                ["Y" ] = Color.FromArgb( 75, 185,  75),
                ["Home"] = Color.FromArgb(  0, 120, 212),
                ["Cap" ] = Color.FromArgb(145,  85, 195),
                ["C"  ] = Color.FromArgb(220, 130,  35),
                ["Up"] = Color.White, ["Dn"] = Color.White,
                ["Lt"] = Color.White, ["Rt"] = Color.White,
            };

            // drawing palette
            private static readonly Color C_BODY   = Color.FromArgb( 42,  42,  46);
            private static readonly Color C_BTN    = Color.FromArgb( 58,  58,  63);
            private static readonly Color C_LABEL  = Color.FromArgb(165, 165, 175);
            private static readonly Color C_SHLD   = Color.FromArgb( 50,  50,  56);
            private static readonly Color C_TRIM_L = Color.FromArgb( 28, 145, 215);  // neon blue
            private static readonly Color C_TRIM_R = Color.FromArgb(225,  78,  48);  // neon red
            private static readonly Color C_CROSS  = Color.FromArgb( 50,  50,  56);

            public JoyConVisualizerPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.AllPaintingInWmPaint, true);
            }

            public void SetButton(string name, bool pressed)
            {
                if (_p.TryGetValue(name, out var cur) && cur == pressed) return;
                _p[name] = pressed;
                Invalidate();
            }

            public void SetSticks(int rawLX, int rawLY, int rawRX, int rawRY)
            {
                // Apply the same deadzone math as ViGEmBridge so the visualiser dot
                // stays centred for any stick position that produces zero ViGEm output.
                // DZ constant must match AXIS_OUTPUT_DEADZONE in ViGEmBridge.cs.
                static float Apply(int raw) {
                    const float C = 1998f, R = 1251f;
                    const float DZ = 6000f, MAX = 32767f, RANGE = MAX - DZ;
                    float v = Math.Clamp((raw - C) / R, -1f, 1f);
                    float i16 = v * MAX;
                    float abs = Math.Abs(i16);
                    if (abs <= DZ) return 0f;
                    return (i16 < 0f ? -1f : 1f) * (abs - DZ) / RANGE;
                }
                float lx = Apply(rawLX);
                float ly = Apply(rawLY);
                float rx = Apply(rawRX);
                float ry = Apply(rawRY);
                if (lx == _lx && ly == _ly && rx == _rx && ry == _ry) return;
                _lx = lx; _ly = ly; _rx = rx; _ry = ry;
                Invalidate();
            }

            private bool On(string b) => _p.TryGetValue(b, out var v) && v;
            private Color Ac(string b) => _on.TryGetValue(b, out var c) ? c : Color.LightGray;

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode   = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(BackColor);
                DrawLJoyCon(g);
                DrawRJoyCon(g);
            }

            // â”€â”€ L Joy-Con 2 â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            //  Front face (topâ†’bottom): ZL/L triggers, âˆ’, L-Stick, LED, D-pad, Cap
            private void DrawLJoyCon(Graphics g)
            {
                const int bx = 8, by = 44, bw = 174, bh = 294;

                // ZL trigger (above body)
                FillRR(g, On("ZL") ? Ac("ZL") : C_SHLD, 10,  2, 92, 30, 8);
                BtnLbl(g, "ZL", 10, 2, 92, 30, On("ZL"));
                // L bumper (between trigger and body top)
                FillRR(g, On("L") ? Ac("L") : C_SHLD, 10, 30, 92, 18, 4);
                BtnLbl(g, "L",  10, 30, 92, 18, On("L"));

                // Body
                FillRR(g, C_BODY, bx, by, bw, bh, 26);
                // Blue rail strip (right edge, neon blue)
                g.FillRectangle(new SolidBrush(C_TRIM_L), bx + bw - 16, by + 22, 16, bh - 44);
                // Body outline
                DrawRR(g, new Pen(Color.FromArgb(68, 68, 76), 1.5f), bx, by, bw, bh, 26);

                // âˆ’ button (top-right of face)
                CircBtn(g, 150, 75, 10, "-", On("-"));

                // Left Stick (upper-center of face)
                StickViz(g, 62, 116, 30, _lx, _ly, C_TRIM_L, On("LS"));
                g.DrawString("LS", new Font("Segoe UI", 6.5f), new SolidBrush(C_LABEL), 94, 104);

                // LED dots (4, vertical)
                for (int i = 0; i < 4; i++)
                    g.FillEllipse(new SolidBrush(Color.FromArgb(62, 62, 72)), 172, 152 + i * 10, 5, 5);

                // D-pad
                const int dpx = 76, dpy = 236;
                // arms
                DPadArm(g, dpx - 9, dpy - 28, 18, 20, "Up", On("Up"));
                DPadArm(g, dpx - 9, dpy +  8, 18, 20, "Dn", On("Dn"));
                DPadArm(g, dpx - 28, dpy - 9, 20, 18, "Lt", On("Lt"));
                DPadArm(g, dpx +  8, dpy - 9, 20, 18, "Rt", On("Rt"));
                // center connector
                g.FillRectangle(new SolidBrush(C_CROSS), dpx - 9, dpy - 9, 18, 18);

                // Screenshot / Capture button
                SqBtn(g, 130, 258, 20, 20, "#", On("Cap"), Ac("Cap"));
            }

            // â”€â”€ R Joy-Con 2 â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            //  Front face (topâ†’bottom): ZR/R triggers, +, ABXY+Home (upper), R-Stick, C
            private void DrawRJoyCon(Graphics g)
            {
                const int bx = 252, by = 44, bw = 174, bh = 294;

                // ZR trigger
                FillRR(g, On("ZR") ? Ac("ZR") : C_SHLD, 332,  2, 92, 30, 8);
                BtnLbl(g, "ZR", 332, 2, 92, 30, On("ZR"));
                // R bumper
                FillRR(g, On("R") ? Ac("R") : C_SHLD, 332, 30, 92, 18, 4);
                BtnLbl(g, "R",  332, 30, 92, 18, On("R"));

                // Body
                FillRR(g, C_BODY, bx, by, bw, bh, 26);
                // Orange rail strip (left edge)
                g.FillRectangle(new SolidBrush(C_TRIM_R), bx, by + 22, 16, bh - 44);
                DrawRR(g, new Pen(Color.FromArgb(68, 68, 76), 1.5f), bx, by, bw, bh, 26);

                // + button (upper-left of face)
                CircBtn(g, 283, 75, 10, "+", On("+"));

                // ABXY diamond (upper-right area)
                //   X=top  Y=left  A=right  B=bottom   each r=13
                CircBtn(g, 376,  82, 13, "X", On("X"));
                CircBtn(g, 350, 108, 13, "Y", On("Y"));
                CircBtn(g, 402, 108, 13, "A", On("A"));
                CircBtn(g, 376, 134, 13, "B", On("B"));

                // Home button (between RS and C)
                CircBtn(g, 315, 248, 12, "Home", On("Home"));

                // LED dots (4, vertical)
                for (int i = 0; i < 4; i++)
                    g.FillEllipse(new SolidBrush(Color.FromArgb(62, 62, 72)), 257, 152 + i * 10, 5, 5);

                // Right Stick (lower-center, below ABXY)
                StickViz(g, 360, 208, 30, _rx, _ry, C_TRIM_R, On("RS"));
                g.DrawString("RS", new Font("Segoe UI", 6.5f), new SolidBrush(C_LABEL), 392, 196);

                // C button (new Joy-Con 2, bottom-center)
                CircBtn(g, 315, 286, 12, "C", On("C"));
            }

            // â”€â”€ drawing primitives â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            private void StickViz(Graphics g, int cx, int cy, int r,
                                  float nx, float ny, Color rim, bool pressed)
            {
                g.FillEllipse(new SolidBrush(Color.FromArgb(28, 28, 32)),
                    cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(new Pen(pressed ? Color.White : rim, pressed ? 2.5f : 1.8f),
                    cx - r, cy - r, r * 2, r * 2);
                // crosshair
                g.DrawLine(new Pen(Color.FromArgb(52, 52, 60), 1), cx - r + 4, cy, cx + r - 4, cy);
                g.DrawLine(new Pen(Color.FromArgb(52, 52, 60), 1), cx, cy - r + 4, cx, cy + r - 4);
                // dot
                const int dr = 7;
                float dx = cx + nx * (r - dr - 3);
                float dy = cy - ny * (r - dr - 3);
                g.FillEllipse(new SolidBrush(rim), dx - dr, dy - dr, dr * 2, dr * 2);
            }

            private void CircBtn(Graphics g, int cx, int cy, int r, string label, bool pressed)
            {
                g.FillEllipse(new SolidBrush(pressed ? Ac(label) : C_BTN), cx-r, cy-r, r*2, r*2);
                g.DrawEllipse(new Pen(Color.FromArgb(78, 78, 88), 1f), cx-r, cy-r, r*2, r*2);
                using var f = new Font("Segoe UI", 6.5f, FontStyle.Bold);
                var sz = g.MeasureString(label, f);
                g.DrawString(label, f,
                    new SolidBrush(pressed ? Color.White : C_LABEL),
                    cx - sz.Width / 2, cy - sz.Height / 2);
            }

            private void DPadArm(Graphics g, int x, int y, int w, int h, string btn, bool pressed)
            {
                FillRR(g, pressed ? Color.White : C_BTN, x, y, w, h, 3);
                Color fg = pressed ? Color.Black : C_LABEL;
                float cx = x + w / 2f;
                float cy = y + h / 2f;
                float s = Math.Min(w, h) * 0.32f;
                PointF[] pts = btn switch
                {
                    "Up" => new[]
                    {
                        new PointF(cx, cy - s),
                        new PointF(cx - s, cy + s),
                        new PointF(cx + s, cy + s),
                    },
                    "Dn" => new[]
                    {
                        new PointF(cx - s, cy - s),
                        new PointF(cx + s, cy - s),
                        new PointF(cx, cy + s),
                    },
                    "Lt" => new[]
                    {
                        new PointF(cx - s, cy),
                        new PointF(cx + s, cy - s),
                        new PointF(cx + s, cy + s),
                    },
                    _ => new[]
                    {
                        new PointF(cx + s, cy),
                        new PointF(cx - s, cy - s),
                        new PointF(cx - s, cy + s),
                    },
                };
                g.FillPolygon(new SolidBrush(fg), pts);
            }

            private void SqBtn(Graphics g, int x, int y, int w, int h,
                               string label, bool pressed, Color onColor)
            {
                FillRR(g, pressed ? onColor : C_BTN, x, y, w, h, 3);
                g.DrawRectangle(new Pen(Color.FromArgb(78, 78, 88), 1f), x, y, w, h);
                using var f = new Font("Segoe UI", 6.5f);
                var sz = g.MeasureString(label, f);
                g.DrawString(label, f,
                    new SolidBrush(pressed ? Color.White : C_LABEL),
                    x + w / 2f - sz.Width / 2, y + h / 2f - sz.Height / 2);
            }

            private static void BtnLbl(Graphics g, string text,
                                       int x, int y, int w, int h, bool lit)
            {
                using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
                var sz = g.MeasureString(text, f);
                g.DrawString(text, f,
                    new SolidBrush(lit ? Color.White : C_LABEL),
                    x + w / 2f - sz.Width / 2, y + h / 2f - sz.Height / 2);
            }

            private static void FillRR(Graphics g, Color c, int x, int y, int w, int h, int r)
            {
                using var path = RRPath(x, y, w, h, r);
                g.FillPath(new SolidBrush(c), path);
            }

            private static void DrawRR(Graphics g, Pen pen, int x, int y, int w, int h, int r)
            {
                using var path = RRPath(x, y, w, h, r);
                g.DrawPath(pen, path);
            }

            private static System.Drawing.Drawing2D.GraphicsPath RRPath(
                int x, int y, int w, int h, int r)
            {
                var p = new System.Drawing.Drawing2D.GraphicsPath();
                if (r <= 0) { p.AddRectangle(new Rectangle(x, y, w, h)); return p; }
                int d = r * 2;
                p.AddArc(x,         y,         d, d, 180, 90);
                p.AddArc(x + w - d, y,         d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d,   0, 90);
                p.AddArc(x,         y + h - d, d, d,  90, 90);
                p.CloseFigure();
                return p;
            }
        }
    }
}
