
using System;
using System.Collections.Generic;
using System.Drawing;
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

        private sealed class RumblePresetOption
        {
            public required string Label { get; init; }
            public required byte LargeMotor { get; init; }
            public required byte SmallMotor { get; init; }
            public required int DurationMs { get; init; }

            public override string ToString() => Label;
        }

        private sealed class DeviceTargetOption
        {
            public required string Label { get; init; }
            public string? DeviceId { get; init; }

            public override string ToString() => Label;
        }

        private sealed class ConnectModeOption
        {
            public required string Label { get; init; }
            public required ConnectMode Value { get; init; }

            public override string ToString() => Label;
        }

        // ── theme colours ──────────────────────────────────────────────────
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

        // ── runtime state ──────────────────────────────────────────────────
        private JoyconState _lastState = new();
        private bool _running   = false;
        private CancellationTokenSource? _cts;

        private Joycon2PC.ViGEm.ViGEmBridge? _bridge;
        private JoyconParser _parser = new();
        private BLEScanner? _scanner;  // active scanner — used by Reconnect button
        private bool _powerEventsSubscribed;
        private LogMode _logMode = LogMode.User;
        private readonly List<LogEntry> _logEntries = new();
        private const int MAX_LOG_ENTRIES = 500;
        private const int MAX_LOG_LINES = 300;

        // ── controls ──────────────────────────────────────────────────────
        private Label  _lblVigemStatus  = null!;
        private Label  _lblJoyconStatus = null!;
        private Button _btnStart        = null!;
        private Button _btnReconnect    = null!;
        private Button _btnTestSound    = null!;
        private Button _btnApplyLed     = null!;
        private Button _btnTestRumble   = null!;
        private CheckBox _chkConnectSound = null!;
        private CheckBox _chkMouseMode = null!;
        private ComboBox _cmbConnectMode = null!;
        private ComboBox _cmbMouseStabilizer = null!;
        private ComboBox _cmbMouseSpeed = null!;
        private ComboBox _cmbLogMode = null!;
        private ComboBox _cmbDeviceTarget = null!;
        private ComboBox _cmbSoundPreset = null!;
        private ComboBox _cmbLedPattern = null!;
        private ComboBox _cmbRumblePreset = null!;
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

        private enum ConnectMode
        {
            AutoPair,
            SingleLeft,
            SingleRight,
        }

        private MouseSpeedMode _mouseSpeedMode = MouseSpeedMode.Normal;
        private MouseStabilizerMode _mouseStabilizerMode = MouseStabilizerMode.Stable;
        private ConnectMode _connectMode = ConnectMode.AutoPair;
        private const int CONNECT_FEEDBACK_PLAYER_NUM = 1;
        private bool _reconnectInProgress;
        private DateTime _lastReconnectRequestUtc = DateTime.MinValue;
        private static readonly TimeSpan RECONNECT_MIN_INTERVAL = TimeSpan.FromMilliseconds(900);

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

        // ══════════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ══════════════════════════════════════════════════════════════════
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

            // ── title bar area ────────────────────────────────────────────
            var lblTitle = MakeLabel("Joycon2PC", 21, new Point(18, 12), bold: true, color: ACCENT);
            lblTitle.AutoSize = true;
            Controls.Add(lblTitle);

            var lblSubtitle = MakeLabel("Joy-Con 2 to virtual Xbox controller bridge", 9, new Point(20, 46), color: TXT_DIM);
            lblSubtitle.AutoSize = true;
            Controls.Add(lblSubtitle);

            // ── status row ────────────────────────────────────────────────
            var statusPanel = new Panel
            {
                BackColor = PANEL,
                Bounds    = new Rectangle(14, 66, 968, 92),
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
            leftStatusCard.Controls.Add(_lblVigemStatus);
            leftStatusCard.Controls.Add(MakeLabel("Install ViGEmBus if unavailable", 8, new Point(168, 25), color: TXT_DIM));

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
            rightStatusCard.Controls.Add(_lblJoyconStatus);
            rightStatusCard.Controls.Add(MakeLabel("Pair in Windows Bluetooth settings", 8, new Point(168, 25), color: TXT_DIM));

            void LayoutStatusCards()
            {
                const int margin = 12;
                const int gap = 12;
                const int top = 30;
                const int height = 50;

                int availableWidth = statusPanel.ClientSize.Width - (margin * 2) - gap;
                if (availableWidth < 2 * 100)
                {
                    // Ensure a reasonable minimum width for each card.
                    availableWidth = 2 * 100;
                }

                int cardWidth = availableWidth / 2;
                leftStatusCard.Bounds = new Rectangle(margin, top, cardWidth, height);
                rightStatusCard.Bounds = new Rectangle(margin + cardWidth + gap, top, cardWidth, height);
            }

            statusPanel.Resize += (sender, args) => LayoutStatusCards();
            LayoutStatusCards();

            // ── main content area ─────────────────────────────────────────
            var contentGrid = new TableLayoutPanel
            {
                Bounds = new Rectangle(14, 170, 968, 344),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1,
            };
            contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 432f));
            contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            contentGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(contentGrid);

            // ── Joy-Con 2 drawn visualizer ──────────────────────────────────
            _joyconViz = new JoyConVisualizerPanel
            {
                BackColor   = PANEL,
                Dock        = DockStyle.Fill,
                MinimumSize = new Size(432, 332),
            };
            contentGrid.Controls.Add(_joyconViz, 0, 0);

            // ── log panel (right column) ──────────────────────────────────
            var logCard = new Panel
            {
                BackColor = PANEL,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0),
                Padding = new Padding(10, 34, 10, 10),
            };
            contentGrid.Controls.Add(logCard, 1, 0);

            var logLabel = MakeLabel("Log", 10, new Point(10, 8), bold: true, color: ACCENT);
            logLabel.AutoSize = true;
            logCard.Controls.Add(logLabel);

            var lblLogMode = MakeLabel("Mode", 8, new Point(388, 10), color: TXT_DIM);
            lblLogMode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            logCard.Controls.Add(lblLogMode);

            _cmbLogMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Bounds = new Rectangle(430, 6, 104, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            _cmbLogMode.Items.AddRange(new object[] { "User", "Developer" });
            _cmbLogMode.SelectedIndex = 0;
            _cmbLogMode.SelectedIndexChanged += (sender, args) =>
            {
                _logMode = _cmbLogMode.SelectedIndex <= 0 ? LogMode.User : LogMode.Developer;
                RebuildLogView();
            };
            logCard.Controls.Add(_cmbLogMode);

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

            // ── action buttons ────────────────────────────────────────────
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
                Bounds    = new Rectangle(0, 0, 250, 56),
                Anchor    = AnchorStyles.Bottom | AnchorStyles.Left,
                Cursor    = Cursors.Hand,
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += OnStartClicked;
            actionPanel.Controls.Add(_btnStart);

            _btnReconnect = new Button
            {
                Text      = "Reconnect",
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Font      = FONT_MD,
                Bounds    = new Rectangle(264, 0, 150, 56),
                Anchor    = AnchorStyles.Bottom | AnchorStyles.Left,
                Cursor    = Cursors.Hand,
            };
            _btnReconnect.FlatAppearance.BorderSize = 1;
            _btnReconnect.FlatAppearance.BorderColor = BORDER;
            _btnReconnect.Click += (s, e) => OnReconnectClicked();
            actionPanel.Controls.Add(_btnReconnect);

            var modulePanel = new Panel
            {
                BackColor = PANEL,
                BorderStyle = BorderStyle.FixedSingle,
                Bounds = new Rectangle(14, 594, 968, 112),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(modulePanel);

            var lblModuleTitle = MakeLabel("Single-Player Controls", 10, new Point(10, 8), bold: true, color: ACCENT);
            modulePanel.Controls.Add(lblModuleTitle);

            modulePanel.Controls.Add(MakeLabel("Mode", 8, new Point(12, 40), color: TXT_DIM));
            _cmbConnectMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Bounds = new Rectangle(48, 36, 164, 24),
            };
            _cmbConnectMode.SelectedIndexChanged += (sender, args) =>
            {
                _connectMode = (_cmbConnectMode.SelectedItem as ConnectModeOption)?.Value ?? ConnectMode.AutoPair;
                string modeLabel = (_cmbConnectMode.SelectedItem as ConnectModeOption)?.Label ?? "Auto pair (L + R)";
                Log($"Connection mode: {modeLabel}", TXT_DIM);

                if (_running)
                    _ = PerformHardReconnectAsync("mode change");
            };
            modulePanel.Controls.Add(_cmbConnectMode);

            modulePanel.Controls.Add(MakeLabel("Device", 8, new Point(224, 40), color: TXT_DIM));
            _cmbDeviceTarget = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Bounds = new Rectangle(270, 36, 178, 24),
            };
            modulePanel.Controls.Add(_cmbDeviceTarget);

            modulePanel.Controls.Add(MakeLabel("Sound", 8, new Point(462, 40), color: TXT_DIM));
            _cmbSoundPreset = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Bounds = new Rectangle(504, 36, 144, 24),
            };
            modulePanel.Controls.Add(_cmbSoundPreset);

            _btnTestSound = new Button
            {
                Text = "▶ Sound",
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Font = FONT_SM,
                Bounds = new Rectangle(652, 34, 68, 28),
                Cursor = Cursors.Hand,
            };
            _btnTestSound.FlatAppearance.BorderSize = 1;
            _btnTestSound.FlatAppearance.BorderColor = BORDER;
            _btnTestSound.Click += async (sender, args) => await TriggerManualSoundTestAsync();
            modulePanel.Controls.Add(_btnTestSound);

            modulePanel.Controls.Add(MakeLabel("LED", 8, new Point(730, 40), color: TXT_DIM));
            _cmbLedPattern = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Bounds = new Rectangle(754, 36, 120, 24),
            };
            modulePanel.Controls.Add(_cmbLedPattern);

            _btnApplyLed = new Button
            {
                Text = "Apply LED",
                BackColor = BTN_SECONDARY,
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Font = FONT_SM,
                Bounds = new Rectangle(878, 34, 76, 28),
                Cursor = Cursors.Hand,
            };
            _btnApplyLed.FlatAppearance.BorderSize = 1;
            _btnApplyLed.FlatAppearance.BorderColor = BORDER;
            _btnApplyLed.Click += async (sender, args) => await TriggerManualLedApplyAsync();
            modulePanel.Controls.Add(_btnApplyLed);

            _chkConnectSound = new CheckBox
            {
                Text = "Play connect sound",
                Checked = true,
                AutoSize = true,
                ForeColor = TXT_DIM,
                BackColor = Color.Transparent,
                Font = FONT_SM,
                Location = new Point(790, 10),
            };
            modulePanel.Controls.Add(_chkConnectSound);

            _chkMouseMode = new CheckBox
            {
                Text = "Mouse mode",
                Checked = false,
                AutoSize = true,
                ForeColor = TXT_DIM,
                BackColor = Color.Transparent,
                Font = FONT_SM,
                Location = new Point(702, 10),
            };
            _chkMouseMode.CheckedChanged += (sender, args) =>
            {
                lock (_mouseStateLock)
                {
                    _mouseModeEnabled = _chkMouseMode.Checked;
                    ResetMouseModeState(releasePressedButtons: true);
                }
                Log($"Mouse mode {( _mouseModeEnabled ? "enabled" : "disabled")}.", ACCENT);
            };
            modulePanel.Controls.Add(_chkMouseMode);

            _cmbMouseStabilizer = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Bounds = new Rectangle(472, 8, 112, 24),
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
            modulePanel.Controls.Add(_cmbMouseStabilizer);

            _cmbMouseSpeed = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT,
                Bounds = new Rectangle(590, 8, 106, 24),
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
            modulePanel.Controls.Add(_cmbMouseSpeed);

            _chkConnectSound.Location = new Point(816, 10);

            // ── Rumble row (disabled — BLE rumble not yet supported) ─────
            modulePanel.Controls.Add(MakeLabel("Rumble", 8, new Point(12, 76), color: TXT_DIM));
            _cmbRumblePreset = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FONT_SM,
                BackColor = PANEL_ALT,
                ForeColor = TXT_DIM,
                Bounds = new Rectangle(58, 72, 170, 24),
                Enabled = false,
            };
            modulePanel.Controls.Add(_cmbRumblePreset);

            _btnTestRumble = new Button
            {
                Text = "Rumble (N/A)",
                BackColor = PANEL_ALT,
                ForeColor = TXT_DIM,
                FlatStyle = FlatStyle.Flat,
                Font = FONT_SM,
                Bounds = new Rectangle(238, 70, 100, 28),
                Enabled = false,
            };
            _btnTestRumble.FlatAppearance.BorderSize = 1;
            _btnTestRumble.FlatAppearance.BorderColor = BORDER;
            modulePanel.Controls.Add(_btnTestRumble);

            var lblModuleHint = MakeLabel("Mode decides whether to expect a full pair or force a single Joy-Con as the active controller. Auto-connect sets P1 LED by default.", 8, new Point(352, 76), color: TXT_DIM);
            lblModuleHint.MaximumSize = new Size(600, 0);
            lblModuleHint.AutoSize = true;
            modulePanel.Controls.Add(lblModuleHint);

            InitializeOptionControls();

            // kick off ViGEm check
            _ = Task.Run(CheckViGEmAsync);
        }

        // ══════════════════════════════════════════════════════════════════
        //  HELPER BUILDERS
        // ══════════════════════════════════════════════════════════════════
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

        // ══════════════════════════════════════════════════════════════════
        //  VIGEM CHECK
        // ══════════════════════════════════════════════════════════════════
        private async Task CheckViGEmAsync()
        {
            await Task.Delay(200); // let window paint first
            try
            {
                _bridge = new Joycon2PC.ViGEm.ViGEmBridge();
                _bridge.Connect();

                // Forward XInput rumble commands back to physical Joy-Con 2 controllers.
                // Games that use rumble will cause the Joy-Con 2 to vibrate.
                _bridge.RumbleReceived += (large, small) =>
                {
                    var sc = _scanner;
                    if (sc != null)
                        _ = sc.SendRumbleAsync(large, small);
                };

                    Invoke(() =>
                {
                    _lblVigemStatus.Text      = "✔  Driver found — virtual controller ready";
                    _lblVigemStatus.ForeColor = GREEN;
                    Log("ViGEmBus driver found. Virtual Xbox 360 controller is ready.", GREEN);
                });
            }
            catch
            {
                    Invoke(() =>
                {
                    _lblVigemStatus.Text      = "✘  ViGEmBus driver NOT installed";
                    _lblVigemStatus.ForeColor = RED;
                    Log("ViGEmBus driver not found! Install it from:", RED);
                    Log("  https://github.com/nefarius/ViGEmBus/releases", ACCENT);
                    Log("Virtual controller will not appear in Windows without ViGEmBus.", YELLOW);
                });
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  BUTTON HANDLERS
        // ══════════════════════════════════════════════════════════════════
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

            _reconnectInProgress = true;
            try
            {
                Log($"⟳ Reconnect ({reason}) — fully restarting BLE loop…", YELLOW);
                _lblJoyconStatus.Text = "Reconnecting…";
                _lblJoyconStatus.ForeColor = YELLOW;

                _scanner?.DisconnectAll();
                _deviceStates.Clear();
                _leftDeviceId = null;
                _rightDeviceId = null;
                _deviceLStickSentinel.Clear();
                _deviceRStickSentinel.Clear();

                bool wasRunning = _running;
                if (wasRunning)
                    StopAll();

                await Task.Delay(260);

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
            _running = true;
            _cts     = new CancellationTokenSource();
            _btnStart.Text      = "Stop";
            _btnStart.BackColor = BTN_STOP;

            // Attach parser → bridge
            _parser.StateChanged -= OnStateChanged;
            _parser               = new JoyconParser();
            _parser.StateChanged += OnStateChanged;

#if INTHEHAND
            Log("Searching for Joy-Con over Bluetooth LE...", ACCENT);
            string modeLabel = (_cmbConnectMode?.SelectedItem as ConnectModeOption)?.Label ?? "Auto pair (L + R)";
            DevLog($"Connection mode: {modeLabel}", TXT_DIM);
        var os = Environment.OSVersion.Version;
        bool isWin11OrNewer = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        DevLog($"OS {os.Major}.{os.Minor}.{os.Build} ({(isWin11OrNewer ? "Win11+" : "Win10 compatibility mode")})", TXT_DIM);
            _lblJoyconStatus.Text      = "Scanning...";
            _lblJoyconStatus.ForeColor = YELLOW;
            _ = RunRealAsync(_cts.Token);
#endif
        }

        private void StopAll()
        {
            _cts?.Cancel();
            _running = false;
            _btnStart.Text      = "Connect Joy-Cons";
            _btnStart.BackColor = BTN_PRIMARY;
            _btnReconnect.BackColor = BTN_SECONDARY;
            _lblJoyconStatus.Text      = "Stopped";
            _lblJoyconStatus.ForeColor = TXT_DIM;
            Log("Stopped.", TXT_DIM);
        }

#if INTHEHAND
        // ══════════════════════════════════════════════════════════════════
        //  REAL JOYCON LOOP  (dual Joy-Con merge + keep-alive + reconnect)
        // ══════════════════════════════════════════════════════════════════

        // Per-device state tracking for dual Joy-Con merge
        private readonly Dictionary<string, JoyconState> _deviceStates = new();
        private string? _leftDeviceId;
        private string? _rightDeviceId;
        // Raw sentinel flags: true = that stick slot is unused (sentinel 2047) for this device
        private readonly Dictionary<string, bool> _deviceLStickSentinel = new();
        private readonly Dictionary<string, bool> _deviceRStickSentinel = new();

        private async Task RunRealAsync(CancellationToken ct)
        {
            // ── outer reconnect loop ──────────────────────────────────────
            while (!ct.IsCancellationRequested)
            {
                DateTime lastReportUtc = DateTime.UtcNow;
                var scanner = new BLEScanner();
                _scanner = scanner;
                _deviceStates.Clear();
                _leftDeviceId = null;
                _rightDeviceId = null;
                _deviceLStickSentinel.Clear();
                _deviceRStickSentinel.Clear();

                // ── hex-dump and change-tracking ────────────────────────
                var dumpCounts          = new Dictionary<string, int>();
                var stickLoggedDevices  = new HashSet<string>();   // prevent dc==3 flood
                var lastButtons         = new Dictionary<string, uint>();
                var lastStickLog        = new Dictionary<string, (int lx, int ly, int rx, int ry)>();
                var lastRawBytes        = new Dictionary<string, byte[]>(); // for byte-diff logger
                JoyconState?            lastMerged = null;

                // Warmup gate: Joy-Con 2 controllers send a burst of garbage button data
                // (L+ZL bits asserted) during their BLE init sequence (~200-400 ms).
                // We suppress all button output for the first WARMUP_REPORTS reports per
                // device. Stick data and sentinel detection are unaffected — they read
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
                    lastReportUtc = DateTime.UtcNow;
                    string shortId = deviceId.Length > 8 ? deviceId[..8] : deviceId;

                    // Hex dump first 3 reports per device
                    if (!dumpCounts.ContainsKey(deviceId)) dumpCounts[deviceId] = 0;
                    if (dumpCounts[deviceId] < 3)
                    {
                        dumpCounts[deviceId]++;
                        string hex = BitConverter.ToString(data, 0, Math.Min(data.Length, 20));
                        try { BeginInvoke(() => DevLog($"RAW[{shortId}] len={data.Length}: {hex}", Color.FromArgb(255, 200, 80))); } catch { }
                    }

                    // ── Byte-diff logger: scan ALL bytes (skip [0]=rolling counter) ──
                    if (enableRawByteDiffLog)
                    {
                        if (lastRawBytes.TryGetValue(deviceId, out var prevRaw) && prevRaw.Length == data.Length)
                        {
                            var diff = new System.Text.StringBuilder();
                            for (int i = 1; i < data.Length; i++)
                            {
                                if (data[i] != prevRaw[i])
                                    diff.Append($" [{i}]:{prevRaw[i]:X2}→{data[i]:X2}");
                            }
                            if (diff.Length > 0)
                                Console.WriteLine($"DIFF[{shortId}]{diff}");
                        }
                        lastRawBytes[deviceId] = (byte[])data.Clone();
                    }

                    if (!NS2InputReportDecoder.TryDecode(data, out var decoded))
                        return;

                    var state = new JoyconState
                    {
                        Buttons = decoded.Buttons,
                        LeftStickX = decoded.LeftStickX,
                        LeftStickY = decoded.LeftStickY,
                        RightStickX = decoded.RightStickX,
                        RightStickY = decoded.RightStickY,
                    };

                    // Detect L vs R from sentinel (runs every report — unconditional).
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

                    if (rxSentinel)                 _leftDeviceId  = deviceId;  // R slot unused → L Joy-Con
                    else if (lxSentinel)            _rightDeviceId = deviceId;  // L slot unused → R Joy-Con
                    else
                    {
                        if (_leftDeviceId  == null) _leftDeviceId  = deviceId;
                        if (_rightDeviceId == null) _rightDeviceId = deviceId;
                    }

                    // Log ONCE per device — exactly when dump count reaches 3
                    dumpCounts.TryGetValue(deviceId, out int dc);
                    if (dc == 3 && !stickLoggedDevices.Contains(deviceId)) // fire exactly once
                    {
                        string sideTag = deviceId == _leftDeviceId ? "L" : deviceId == _rightDeviceId ? "R" : "?";
                        int lxC = state.LeftStickX, lyC = state.LeftStickY;
                        int rxC = state.RightStickX, ryC = state.RightStickY;
                        try { BeginInvoke(() => DevLog(
                            $"STICK[{shortId}]({sideTag}) LX={lxC} LY={lyC} RX={rxC} RY={ryC}",
                            Color.FromArgb(100, 200, 255))); } catch { }
                        stickLoggedDevices.Add(deviceId);  // never fire again for this device
                    }

                    // ── Warmup gate: suppress buttons until controller has settled ──
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

                    // ── Debug: log only when buttons change or stick moves >80 counts ──
                    // (Never BeginInvoke on every report — that floods the UI thread queue)
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
                                string btnStr = btns.Length > 0 ? btns.ToString() : "·";

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

                    // ── ViGEm — direct call, NOT via BeginInvoke ───────────
                    var merged = MergeDeviceStates();
                    try { _bridge?.UpdateFromState(merged); } catch { }
                    _lastState = merged;

                    // ── UI update — only when merged state actually changed ─
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
                };

                using var scanTimeout = new CancellationTokenSource(30_000);
                var scanReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // ── Immediate status update when a device connects ────────
                // DeviceConnected fires during ScanAsync (not after), so the UI
                // shows "Connected" the moment GATT subscription succeeds.
                scanner.DeviceConnected += (deviceId, devName) =>
                {
                    try
                    {
                        BeginInvoke(() =>
                        {
                            string shortName = devName.Length > 0 ? devName : deviceId[..Math.Min(12, deviceId.Length)];
                            _lblJoyconStatus.Text      = $"Connected: {shortName}";
                            _lblJoyconStatus.ForeColor = GREEN;
                            Log($"✔ Connected: {shortName}", GREEN);
                            RefreshDeviceTargetOptions(scanner);

                            if (_connectMode == ConnectMode.AutoPair)
                            {
                                if (scanner.GetKnownDeviceIds().Length >= 2)
                                    scanReadySignal.TrySetResult(true);
                                return;
                            }

                            string wanted = _connectMode == ConnectMode.SingleLeft ? "Left" : "Right";
                            bool isTarget = MatchesRequestedSingleModeSide(scanner, deviceId, devName);
                            if (isTarget)
                            {
                                scanReadySignal.TrySetResult(true);
                            }
                            else
                            {
                                Log($"Ignoring non-target side in single mode. Waiting for {wanted} Joy-Con.", TXT_DIM);
                                _lblJoyconStatus.Text = $"Waiting for {wanted} Joy-Con...";
                                _lblJoyconStatus.ForeColor = YELLOW;
                            }
                        });
                    }
                    catch { }
                };

                // ── Scan ─────────────────────────────────────────────────
                Invoke(() =>
                {
                    _lblJoyconStatus.Text      = "Scanning…";
                    _lblJoyconStatus.ForeColor = YELLOW;
                    Log("Checking paired devices + scanning for advertising ones…", ACCENT);
                    Log("Tip: Joy-Con 2 already paired in Windows? — it will connect automatically.", TXT_DIM);
                });

                using var linkedScan  = CancellationTokenSource.CreateLinkedTokenSource(ct, scanTimeout.Token);
                var scanTask = scanner.ScanAsync(linkedScan.Token);
                try
                {
                    var completed = await Task.WhenAny(scanTask, scanReadySignal.Task);
                    if (completed == scanReadySignal.Task)
                    {
                        // Continue quickly after target-side connection in the chosen mode.
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
                        Log("No controllers found — retrying in 3 s…", YELLOW);
                        Log("Make sure Joy-Con 2 is paired in Windows Bluetooth Settings.", TXT_DIM);
                        _lblJoyconStatus.Text      = "Retrying…";
                        _lblJoyconStatus.ForeColor = YELLOW;
                    });
                    try { await Task.Delay(3_000, ct); } catch { break; }
                    continue;
                }

                // ── Assign L / R IDs from PnP (definitive, overrides early guess) ─
                AssignDeviceIds(scanner, ids);

                // In single mode, only proceed when the requested side is identified.
                if (_connectMode != ConnectMode.AutoPair && (_leftDeviceId == null || _rightDeviceId == null))
                {
                    Invoke(() =>
                    {
                        string wanted = _connectMode == ConnectMode.SingleLeft ? "Left" : "Right";
                        Log($"Single Joy-Con mode: target {wanted} side not identified yet. Retry after powering on only that side.", YELLOW);
                        _lblJoyconStatus.Text = $"Waiting for {wanted} Joy-Con...";
                        _lblJoyconStatus.ForeColor = YELLOW;
                    });

                    try { await Task.Delay(2_000, ct); } catch { break; }
                    continue;
                }

                Invoke(() =>
                {
                    bool dual = _leftDeviceId != null && _rightDeviceId != null && _leftDeviceId != _rightDeviceId;
                    string lId = _leftDeviceId?[..Math.Min(8, _leftDeviceId.Length)] ?? "?";
                    string rId = _rightDeviceId?[..Math.Min(8, _rightDeviceId.Length)] ?? "?";
                    string msg = _connectMode switch
                    {
                        ConnectMode.SingleLeft => $"Single Joy-Con mode active (Left). Device={lId}",
                        ConnectMode.SingleRight => $"Single Joy-Con mode active (Right). Device={rId}",
                        _ => dual
                            ? $"Joy-Con pair connected! L={lId}  R={rId}"
                            : $"Joy-Con 2 connected ({ids[0][..Math.Min(12, ids[0].Length)]})",
                    };
                    Log(msg, GREEN);
                    _lblJoyconStatus.Text = _connectMode switch
                    {
                        ConnectMode.SingleLeft => "Single Joy-Con (L) Connected ✔",
                        ConnectMode.SingleRight => "Single Joy-Con (R) Connected ✔",
                        _ => dual ? "Joy-Con Pair Connected ✔" : "Connected ✔",
                    };
                    _lblJoyconStatus.ForeColor = GREEN;
                    RefreshDeviceTargetOptions(scanner);
                });

                var activeIds = GetActiveDeviceIdsForConnectMode(ids);
                var sortedIds = SortDeviceIdsForPlayerOrder(scanner, activeIds);
                DevLog("Connect flow: joycon2cpp-style init -> input mode -> LED -> sound", TXT_DIM);

                // ── Post-connect init: switch to continuous full-rate reporting ─
                // Win10 typically needs a longer settle delay and more retries than Win11.
                var inputModeInit = GetInputModeInitProfile();
                try { await Task.Delay(inputModeInit.InitialDelayMs, ct); } catch { break; }
                await InitializeCommandChannelAsync(scanner, sortedIds, ct);
                await ConfigureContinuousInputModeAsync(scanner, sortedIds, inputModeInit, ct);
                await ApplyConnectFeedbackAsync(scanner, sortedIds, ct);

                // ── Wait: stay here until all devices disconnect or user stops ─
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(250, ct); } catch { break; }

                    int knownCount = scanner.GetKnownDeviceIds().Length;
                    if (knownCount <= 0)
                        break;

                    var silence = DateTime.UtcNow - lastReportUtc;
                    if (silence >= TimeSpan.FromSeconds(6))
                    {
                        Invoke(() =>
                        {
                            Log($"BLE link silent for {silence.TotalSeconds:F1}s with {knownCount} known device(s) — forcing reconnect.", YELLOW);
                            _lblJoyconStatus.Text      = "Link silent — reconnecting…";
                            _lblJoyconStatus.ForeColor = YELLOW;
                        });
                        scanner.DisconnectAll();
                        break;
                    }
                }

                if (ct.IsCancellationRequested) break;

                // All devices disconnected — attempt reconnect
                Invoke(() =>
                {
                    Log("Joy-Con disconnected — reconnecting…", YELLOW);
                    _lblJoyconStatus.Text      = "Reconnecting…";
                    _lblJoyconStatus.ForeColor = YELLOW;
                });
                try { await Task.Delay(2_000, ct); } catch { break; }
            }

            Invoke(() =>
            {
                _lblJoyconStatus.Text      = "Stopped";
                _lblJoyconStatus.ForeColor = TXT_DIM;
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
                    Log("System resume detected — auto reconnecting BLE...", YELLOW);
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
            if (_connectMode != ConnectMode.AutoPair)
            {
                AssignSingleModeDeviceIds(scanner, ids);
                return;
            }

            // Pass 0: device name — most reliable (Windows names: "Joy-Con 2 (L)" / "Joy-Con 2 (R)")
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

            // Pass 2: use sentinel-based IDs set during RawReportReceived
            // (these are already in _leftDeviceId / _rightDeviceId)
            if (newLeft == null)  newLeft  = _leftDeviceId;
            if (newRight == null) newRight = _rightDeviceId;

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
                    // R slot sentinel → this is Joy-Con L
                    if (rSentinel && !lSentinel && newLeft  == null) newLeft  = id;
                    // L slot sentinel → this is Joy-Con R
                    if (lSentinel && !rSentinel && newRight == null) newRight = id;
                }
            }

            // Pass 4: single Joy-Con — if only one device connected, assign it as its own side
            // and mark the other side as the SAME device (isSingleDevice path in MergeDeviceStates).
            if (ids.Length == 1)
            {
                // Only one controller — figure out which side it is from name/PnP,
                // then assign both pointers to it so MergeDeviceStates uses its full axes.
                string solo = ids[0];
                string soloName = scanner.GetDeviceName(solo);
                if (soloName.Contains("(R)", StringComparison.OrdinalIgnoreCase))
                {
                    newRight = solo;
                    newLeft  = solo; // will trigger isSingleDevice path
                }
                else
                {
                    newLeft  = solo;
                    newRight = solo; // will trigger isSingleDevice path
                }
                Console.WriteLine($"[Assign] Single Joy-Con mode: {soloName} — both slots → {solo[..Math.Min(8,solo.Length)]}");
            }
            else
            {
                // Two or more — last resort
                if (newLeft  == null && newRight == null) { newLeft = ids[0]; newRight = ids[1]; }
                else if (newLeft  == null) newLeft  = newRight;
                else if (newRight == null) newRight = newLeft;
            }

            _leftDeviceId  = newLeft;
            _rightDeviceId = newRight;
        }

        private void AssignSingleModeDeviceIds(BLEScanner scanner, string[] ids)
        {
            string? preferred = _connectMode == ConnectMode.SingleLeft ? _leftDeviceId : _rightDeviceId;
            if (!string.IsNullOrWhiteSpace(preferred) && ids.Any(id => string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase)))
            {
                _leftDeviceId = preferred;
                _rightDeviceId = preferred;
                return;
            }

            _leftDeviceId = null;
            _rightDeviceId = null;

            string? leftCandidate = null;
            string? rightCandidate = null;

            foreach (var id in ids)
            {
                string name = scanner.GetDeviceName(id);
                ushort pid = scanner.GetProductId(id);
                bool isLeft = name.Contains("(L)", StringComparison.OrdinalIgnoreCase) || pid == BLEScanner.PID_JOYCON_L;
                bool isRight = name.Contains("(R)", StringComparison.OrdinalIgnoreCase) || pid == BLEScanner.PID_JOYCON_R;

                if (!isLeft && _deviceRStickSentinel.TryGetValue(id, out var rSentinel) && rSentinel)
                    isLeft = true;
                if (!isRight && _deviceLStickSentinel.TryGetValue(id, out var lSentinel) && lSentinel)
                    isRight = true;

                if (isLeft && leftCandidate == null)
                    leftCandidate = id;
                if (isRight && rightCandidate == null)
                    rightCandidate = id;
            }

            string? chosen = _connectMode == ConnectMode.SingleLeft
                ? leftCandidate
                : rightCandidate;

            if (chosen == null)
            {
                foreach (var id in ids)
                {
                    bool lSentinel = _deviceLStickSentinel.TryGetValue(id, out var ls) && ls;
                    bool rSentinel = _deviceRStickSentinel.TryGetValue(id, out var rs) && rs;

                    if (_connectMode == ConnectMode.SingleLeft && rSentinel && !lSentinel)
                    {
                        chosen = id;
                        break;
                    }

                    if (_connectMode == ConnectMode.SingleRight && lSentinel && !rSentinel)
                    {
                        chosen = id;
                        break;
                    }
                }
            }

            if (chosen == null)
            {
                // Deterministic single-mode: do not fall back to an arbitrary device.
                // Caller will show retry guidance and keep scanning.
                return;
            }

            _leftDeviceId = chosen;
            _rightDeviceId = chosen;
        }

        private string[] GetActiveDeviceIdsForConnectMode(string[] knownIds)
        {
            if (knownIds.Length == 0)
                return knownIds;

            if (_connectMode == ConnectMode.AutoPair)
                return knownIds;

            string? chosenId = _connectMode == ConnectMode.SingleLeft ? _leftDeviceId : _rightDeviceId;
            if (string.IsNullOrWhiteSpace(chosenId))
                return new[] { knownIds[0] };

            foreach (var id in knownIds)
            {
                if (string.Equals(id, chosenId, StringComparison.OrdinalIgnoreCase))
                    return new[] { id };
            }

            return new[] { knownIds[0] };
        }

        private bool MatchesRequestedSingleModeSide(BLEScanner scanner, string deviceId, string deviceName)
        {
            string name = deviceName ?? string.Empty;
            ushort pid = scanner.GetProductId(deviceId);

            bool isLeft = name.Contains("(L)", StringComparison.OrdinalIgnoreCase) || pid == BLEScanner.PID_JOYCON_L;
            bool isRight = name.Contains("(R)", StringComparison.OrdinalIgnoreCase) || pid == BLEScanner.PID_JOYCON_R;

            if (!isLeft && _deviceRStickSentinel.TryGetValue(deviceId, out var rSentinel) && rSentinel)
                isLeft = true;
            if (!isRight && _deviceLStickSentinel.TryGetValue(deviceId, out var lSentinel) && lSentinel)
                isRight = true;

            return _connectMode == ConnectMode.SingleLeft ? isLeft : isRight;
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

            for (int p = 0; p < sortedIds.Length; p++)
            {
                string id = sortedIds[p];
                string side = ResolveDeviceSideLabel(scanner, id);
                int playerNum = CONNECT_FEEDBACK_PLAYER_NUM;

                try
                {
                    // joycon2cpp-exact LED command path.
                    await scanner.SendSubcommandAsync(id, Joycon2PC.Core.SubcommandBuilder.BuildNS2PlayerLedCompat(playerNum), $"led-cpp-p{playerNum}", ct);
                    Invoke(() => Log($"  Joy-Con {side} LED confirmed (shared P{playerNum})", ACCENT));

                    if (soundSettings.Enabled)
                    {
                        await Task.Delay(25, ct);
                        await scanner.SendSubcommandAsync(id, Joycon2PC.Core.SubcommandBuilder.BuildNS2SoundCompat(soundSettings.Preset), $"sound-cpp-0x{soundSettings.Preset:X2}", ct);
                        byte preset = soundSettings.Preset;
                        Invoke(() => Log($"  Joy-Con {side} connect sound sent (0x{preset:X2})", ACCENT));
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
        /// Buttons  — OR'd from every device (each Joy-Con carries its own half of the buttons).
        /// L Stick  — from the device identified as Joy-Con L (bytes [10..12] of its report).
        /// R Stick  — from the device identified as Joy-Con R.
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

            // ── Dual / single device detection ────────────────────────────
            bool isSingleDevice = _leftDeviceId != null
                                && _leftDeviceId == _rightDeviceId;
            bool dualMode = !isSingleDevice
                          && _leftDeviceId != null
                          && _rightDeviceId != null;

            // ── Buttons — side-masked OR in dual mode ───────────────────
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
            else
            {
                // Single controller or IDs not yet assigned — OR all buttons as-is.
                foreach (var s in _deviceStates.Values)
                    merged.Buttons |= s.Buttons;
            }

            if (isSingleDevice)
            {
                // Single controller — use whichever stick bytes are NOT at sentinel (1998).
                // Joy-Con L: real data on LeftStick bytes [10..12], RightStick = 1998
                // Joy-Con R: real data on RightStick bytes [13..15], LeftStick = 1998
                if (_deviceStates.TryGetValue(_leftDeviceId!, out var s))
                {
                    bool lReal = Math.Abs(s.LeftStickX  - 1998) > 50 || Math.Abs(s.LeftStickY  - 1998) > 50;
                    bool rReal = Math.Abs(s.RightStickX - 1998) > 50 || Math.Abs(s.RightStickY - 1998) > 50;

                    if (rReal && !lReal)
                    {
                        // Joy-Con R solo: physical stick → LeftStick output (primary axis for games)
                        merged.LeftStickX  = s.RightStickX;
                        merged.LeftStickY  = s.RightStickY;
                    }
                    else
                    {
                        // Joy-Con L solo or unknown: physical stick → LeftStick output
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
                // the R controller hasn't sent a real value yet — leave merged at 1998.
                if (_deviceStates.TryGetValue(_rightDeviceId!, out var rs))
                {
                    // RightStick field is authoritative for Joy-Con R.
                    // LeftStick field on the R device = 1998 (sentinel was neutralised) — ignore it.
                    merged.RightStickX = rs.RightStickX;
                    merged.RightStickY = rs.RightStickY;
                }
            }
            else
            {
                // IDs not yet fully assigned — output what we have but do NOT clobber IDs here.
                // The report handler will assign the correct IDs when the sentinel is seen.
                // Just pass through whatever data we have so the UI isn't frozen.
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
                    // Up on stick (higher raw Y) → positive wheel delta = scroll up in Windows.
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
            _cmbConnectMode.Items.Clear();
            _cmbConnectMode.Items.AddRange(new object[]
            {
                new ConnectModeOption { Label = "Auto pair (L + R)", Value = ConnectMode.AutoPair },
                new ConnectModeOption { Label = "Single Joy-Con (L)", Value = ConnectMode.SingleLeft },
                new ConnectModeOption { Label = "Single Joy-Con (R)", Value = ConnectMode.SingleRight },
            });
            _cmbConnectMode.SelectedIndex = 0;

            _cmbDeviceTarget.Items.Clear();
            _cmbDeviceTarget.Items.Add(new DeviceTargetOption { Label = "Connected setup (pair/single)", DeviceId = null });
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

            _cmbRumblePreset.Items.AddRange(new object[]
            {
                new RumblePresetOption { Label = "Short pulse", LargeMotor = 255, SmallMotor = 255, DurationMs = 120 },
                new RumblePresetOption { Label = "Medium pulse", LargeMotor = 255, SmallMotor = 255, DurationMs = 220 },
                new RumblePresetOption { Label = "Long pulse", LargeMotor = 255, SmallMotor = 255, DurationMs = 360 },
            });
            _cmbRumblePreset.SelectedIndex = 1;

            // Joy-Con 2 BLE rumble is not supported — joycon2cpp has no rumble implementation.
            // The 0x50 keep-alive format does not trigger the HD Rumble actuator.
            _btnTestRumble.Enabled   = false;
            _btnTestRumble.Text      = "Rumble (N/A)";
            _cmbRumblePreset.Enabled = false;
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
                        : $"Joy-Con {side} · {name}";
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

        private RumblePresetOption GetSelectedRumblePreset()
            => (_cmbRumblePreset?.SelectedItem as RumblePresetOption)
            ?? new RumblePresetOption { Label = "Medium pulse", LargeMotor = 255, SmallMotor = 255, DurationMs = 220 };

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

        private async Task TriggerManualRumbleTestAsync()
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
                Log("No connected Joy-Con available for rumble test.", YELLOW);
                return;
            }

            var preset = GetSelectedRumblePreset();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            foreach (var id in targetIds)
            {
                string side = ResolveDeviceSideLabel(scanner, id);
                bool started = await scanner.SendRumbleAsync(id, preset.LargeMotor, preset.SmallMotor, $"manual-rumble-{preset.DurationMs}ms-on", cts.Token, allowOff: true);
                if (!started)
                    continue;

                Log($"Manual rumble pulse sent to Joy-Con {side} ({preset.Label}).", ACCENT);
            }

            try { await Task.Delay(preset.DurationMs, cts.Token); } catch { return; }

            foreach (var id in targetIds)
                await scanner.SendRumbleAsync(id, 0, 0, $"manual-rumble-{preset.DurationMs}ms-off", cts.Token, allowOff: true);
        }

        // ══════════════════════════════════════════════════════════════════
        //  PARSER CALLBACK  (called from background thread)
        // ══════════════════════════════════════════════════════════════════
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
            // ── Stick visualisers ──────────────────────────────────────────
            _joyconViz?.SetSticks(state.LeftStickX, state.LeftStickY, state.RightStickX, state.RightStickY);

            // ── Button indicators ──────────────────────────────────────────
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
            // NS2 12-bit raw → normalised -1..1  (factory centre = 1998)
            const float ns2Centre = 1998f;
            const float ns2Range  = 1251f;  // ≈ half of 3249-746, used for symmetry
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

        // ══════════════════════════════════════════════════════════════════
        //  LOGGING
        // ══════════════════════════════════════════════════════════════════
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

        // ══════════════════════════════════════════════════════════════════
        //  FORM CLOSE
        // ══════════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            if (_powerEventsSubscribed)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _powerEventsSubscribed = false;
            }
            _bridge?.Dispose();
            base.OnFormClosing(e);
        }

        // ══════════════════════════════════════════════════════════════════
        //  JOY-CON 2 VISUALIZER  (custom-drawn GDI+ panel)
        // ══════════════════════════════════════════════════════════════════
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
                const float C = 1998f, R = 1251f;
                float lx = Math.Clamp((rawLX - C) / R, -1f, 1f);
                float ly = Math.Clamp((rawLY - C) / R, -1f, 1f);
                float rx = Math.Clamp((rawRX - C) / R, -1f, 1f);
                float ry = Math.Clamp((rawRY - C) / R, -1f, 1f);
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

            // ── L Joy-Con 2 ──────────────────────────────────────────────────
            //  Front face (top→bottom): ZL/L triggers, −, L-Stick, LED, D-pad, Cap
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

                // − button (top-right of face)
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
                SqBtn(g, 130, 258, 20, 20, "■", On("Cap"), Ac("Cap"));
            }

            // ── R Joy-Con 2 ──────────────────────────────────────────────────
            //  Front face (top→bottom): ZR/R triggers, +, ABXY+Home (upper), R-Stick, C
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

            // ── drawing primitives ──────────────────────────────────────────
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
