
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Joycon2PC.App.Bluetooth;
using Joycon2PC.Core;

namespace Joycon2PC.App
{
    public sealed class MainForm : Form
    {
        // ── theme colours ──────────────────────────────────────────────────
        private static readonly Color BG       = Color.FromArgb(28,  28,  35);
        private static readonly Color PANEL    = Color.FromArgb(38,  38,  50);
        private static readonly Color ACCENT   = Color.FromArgb(99,  179, 237);
        private static readonly Color GREEN    = Color.FromArgb(72,  199, 116);
        private static readonly Color RED      = Color.FromArgb(252, 92,  101);
        private static readonly Color YELLOW   = Color.FromArgb(255, 200, 80);
        private static readonly Color TXT      = Color.FromArgb(220, 220, 230);
        private static readonly Color TXT_DIM  = Color.FromArgb(130, 130, 150);
        private static readonly Font  FONT_LG  = new("Segoe UI", 11f, FontStyle.Regular);
        private static readonly Font  FONT_MD  = new("Segoe UI", 9f,  FontStyle.Regular);
        private static readonly Font  FONT_SM  = new("Segoe UI", 8f,  FontStyle.Regular);
        private static readonly Font  FONT_BOLD= new("Segoe UI", 9f,  FontStyle.Bold);

        // ── runtime state ──────────────────────────────────────────────────
        private JoyconState _lastState = new();
        private bool _running   = false;
        private CancellationTokenSource? _cts;

        private Joycon2PC.ViGEm.ViGEmBridge? _bridge;
        private JoyconParser _parser = new();
        private BLEScanner? _scanner;  // active scanner — used by Reconnect button

        // ── controls ──────────────────────────────────────────────────────
        private Label  _lblVigemStatus  = null!;
        private Label  _lblJoyconStatus = null!;
        private Button _btnStart        = null!;
        private Button _btnReconnect    = null!;
        private RichTextBox _log        = null!;
        private Panel  _pnlLStick       = null!;
        private Panel  _pnlRStick       = null!;

        // Button indicator labels keyed by name
        private Dictionary<string, Panel> _btnIndicators = new();

        public MainForm()
        {
            InitUI();
            _parser.StateChanged += OnStateChanged;
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ══════════════════════════════════════════════════════════════════
        private void InitUI()
        {
            Text            = "Joycon2PC";
            Size            = new Size(820, 660);
            MinimumSize     = new Size(780, 600);
            BackColor       = BG;
            ForeColor       = TXT;
            Font            = FONT_MD;
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            // ── title bar area ────────────────────────────────────────────
            var lblTitle = MakeLabel("🎮  Joycon2PC", 16, new Point(16, 12), bold: true, color: ACCENT);
            lblTitle.AutoSize = true;
            Controls.Add(lblTitle);

            // ── status row ────────────────────────────────────────────────
            var statusPanel = new Panel
            {
                BackColor = PANEL,
                Bounds    = new Rectangle(12, 48, 790, 52),
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(statusPanel);

            statusPanel.Controls.Add(MakeLabel("ViGEm (virtual gamepad):", 9,  new Point(12, 8),  bold: true));
            _lblVigemStatus = MakeLabel("Not checked", 9, new Point(186, 8), color: YELLOW);
            statusPanel.Controls.Add(_lblVigemStatus);

            statusPanel.Controls.Add(MakeLabel("Joy-Con:", 9, new Point(12, 30), bold: true));
            _lblJoyconStatus = MakeLabel("Not connected", 9, new Point(186, 30), color: TXT_DIM);
            statusPanel.Controls.Add(_lblJoyconStatus);

            var hintViGEm = MakeLabel("Need to install ViGEmBus driver first — see help below", 8, new Point(380, 8), color: TXT_DIM);
            hintViGEm.AutoSize = true;
            statusPanel.Controls.Add(hintViGEm);

            var hintJoyCon = MakeLabel("Pair Joy-Con in Windows Bluetooth Settings first", 8, new Point(380, 30), color: TXT_DIM);
            hintJoyCon.AutoSize = true;
            statusPanel.Controls.Add(hintJoyCon);

            // ── button panel (left half, below status) ────────────────────
            var pnlLeft = new Panel
            {
                BackColor = PANEL,
                Bounds    = new Rectangle(12, 110, 390, 310),
                Anchor    = AnchorStyles.Top | AnchorStyles.Left,
            };
            Controls.Add(pnlLeft);
            pnlLeft.Controls.Add(MakeLabel("Controller Inputs", 9, new Point(8, 6), bold: true, color: ACCENT));

            // Stick visualisers
            _pnlLStick = MakeStickPanel(new Point(20, 30));
            _pnlRStick = MakeStickPanel(new Point(130, 30));
            pnlLeft.Controls.Add(_pnlLStick);
            pnlLeft.Controls.Add(_pnlRStick);
            pnlLeft.Controls.Add(MakeLabel("L Stick", 8, new Point(32, 120), color: TXT_DIM));
            pnlLeft.Controls.Add(MakeLabel("R Stick", 8, new Point(140, 120), color: TXT_DIM));

            // Button grid
            int bx = 248, by = 28;
            AddButtonIndicator(pnlLeft, "A",    new Point(bx + 44, by + 22));
            AddButtonIndicator(pnlLeft, "B",    new Point(bx + 22, by + 44));
            AddButtonIndicator(pnlLeft, "X",    new Point(bx + 22, by));
            AddButtonIndicator(pnlLeft, "Y",    new Point(bx,      by + 22));
            AddButtonIndicator(pnlLeft, "ZR",   new Point(bx + 60, by + 70), YELLOW);
            AddButtonIndicator(pnlLeft, "ZL",   new Point(bx,      by + 70), YELLOW);
            AddButtonIndicator(pnlLeft, "R",    new Point(bx + 60, by + 92));
            AddButtonIndicator(pnlLeft, "L",    new Point(bx,      by + 92));
            AddButtonIndicator(pnlLeft, "+",    new Point(bx + 44, by + 116));
            AddButtonIndicator(pnlLeft, "-",    new Point(bx,      by + 116));
            AddButtonIndicator(pnlLeft, "Home", new Point(bx + 22, by + 138), ACCENT);
            AddButtonIndicator(pnlLeft, "Cap",  new Point(bx + 46, by + 138), Color.FromArgb(180, 100, 220));
            AddButtonIndicator(pnlLeft, "C",    new Point(bx + 70, by + 138), Color.FromArgb(255, 160, 30));  // Joy-Con 2 new C button

            // D-Pad
            AddButtonIndicator(pnlLeft, "Up",    new Point(bx + 22, by + 162));
            AddButtonIndicator(pnlLeft, "Dn",    new Point(bx + 22, by + 186));
            AddButtonIndicator(pnlLeft, "Lt",    new Point(bx,      by + 174));
            AddButtonIndicator(pnlLeft, "Rt",    new Point(bx + 44, by + 174));

            // ── log panel (right half) ────────────────────────────────────
            _log = new RichTextBox
            {
                BackColor   = Color.FromArgb(20, 20, 28),
                ForeColor   = TXT,
                Font        = new Font("Consolas", 8.5f),
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                Bounds      = new Rectangle(414, 110, 388, 310),
                Anchor      = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                WordWrap    = false,
            };
            Controls.Add(_log);
            var logLabel = MakeLabel("Log", 9, new Point(414, 94), bold: true, color: ACCENT);
            logLabel.AutoSize = true;
            Controls.Add(logLabel);

            // ── action buttons ────────────────────────────────────────────
            _btnStart = new Button
            {
                Text      = "▶  Start (Scan for Joy-Con)",
                BackColor = Color.FromArgb(50, 120, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                Bounds    = new Rectangle(12, 430, 240, 48),
                Anchor    = AnchorStyles.Bottom | AnchorStyles.Left,
                Cursor    = Cursors.Hand,
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += OnStartClicked;
            Controls.Add(_btnStart);

            _btnReconnect = new Button
            {
                Text      = "🔄  Reconnect",
                BackColor = Color.FromArgb(80, 60, 20),
                ForeColor = TXT,
                FlatStyle = FlatStyle.Flat,
                Font      = FONT_MD,
                Bounds    = new Rectangle(418, 430, 140, 48),
                Anchor    = AnchorStyles.Bottom | AnchorStyles.Left,
                Cursor    = Cursors.Hand,
            };
            _btnReconnect.FlatAppearance.BorderSize = 0;
            _btnReconnect.Click += (s, e) => OnReconnectClicked();
            Controls.Add(_btnReconnect);

            // ── help / instruction panel ──────────────────────────────────
            var helpBox = new RichTextBox
            {
                BackColor   = Color.FromArgb(24, 24, 32),
                ForeColor   = TXT_DIM,
                Font        = FONT_SM,
                ReadOnly    = true,
                BorderStyle = BorderStyle.None,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                Bounds      = new Rectangle(12, 490, 790, 130),
                Anchor      = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            helpBox.Text =
                "QUICK START — HOW TO USE\r\n" +
                "────────────────────────────────────────────\r\n" +
                "  Step 1 — Install ViGEmBus driver:  https://github.com/nefarius/ViGEmBus/releases  (run the installer)\r\n" +
                "  Step 2 — Pair your Joy-Con: Windows Settings → Bluetooth → Add device → choose your Joy-Con 2\r\n" +
                "  Step 3 — Press ▶ Start — the app will scan, connect, and map inputs to a virtual Xbox controller.\r\n\r\n" +
                "NOTE: ViGEmBus must be installed for the virtual controller to work.";
            Controls.Add(helpBox);

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
            BackColor = Color.FromArgb(22, 22, 30),
        };

        private void AddButtonIndicator(Control parent, string name, Point loc, Color? onColor = null)
        {
            var p = new Panel
            {
                Size      = new Size(20, 20),
                Location  = loc,
                BackColor = Color.FromArgb(50, 50, 65),
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
            Log("⟳ Reconnect pressed — clearing BLE state and restarting scan…", YELLOW);
            _lblJoyconStatus.Text      = "Reconnecting…";
            _lblJoyconStatus.ForeColor = YELLOW;

            // Force-clear all stale device entries so GetKnownDeviceIds() returns 0
            // and the wait loop in RunRealAsync exits immediately.
            _scanner?.DisconnectAll();
            _deviceStates.Clear();
            _leftDeviceId  = null;
            _rightDeviceId = null;
            _deviceLStickSentinel.Clear();
            _deviceRStickSentinel.Clear();

            if (!_running)
            {
                StartAll();
                return;
            }

            // Cancel current RunRealAsync iteration so the outer while-loop restarts
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = Task.Delay(400).ContinueWith(_ =>
            {
                if (!IsDisposed)
                    BeginInvoke(() =>
                    {
                        _running = true;
#if INTHEHAND
                        _ = RunRealAsync(_cts.Token);
#endif
                    });
            });
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
            _btnStart.Text      = "⏹  Stop";
            _btnStart.BackColor = Color.FromArgb(140, 50, 50);

            // Attach parser → bridge
            _parser.StateChanged -= OnStateChanged;
            _parser               = new JoyconParser();
            _parser.StateChanged += OnStateChanged;

#if INTHEHAND
            Log("Searching for Joy-Con over Bluetooth LE...", ACCENT);
            _lblJoyconStatus.Text      = "Scanning...";
            _lblJoyconStatus.ForeColor = YELLOW;
            _ = RunRealAsync(_cts.Token);
#endif
        }

        private void StopAll()
        {
            _cts?.Cancel();
            _running = false;
            _btnStart.Text      = "▶  Start (Scan for Joy-Con)";
            _btnStart.BackColor = Color.FromArgb(50, 120, 80);
            _btnReconnect.BackColor = Color.FromArgb(80, 60, 20);
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

                scanner.RawReportReceived += (deviceId, data) =>
                {
                    string shortId = deviceId.Length > 8 ? deviceId[..8] : deviceId;

                    // Hex dump first 3 reports per device
                    if (!dumpCounts.ContainsKey(deviceId)) dumpCounts[deviceId] = 0;
                    if (dumpCounts[deviceId] < 3)
                    {
                        dumpCounts[deviceId]++;
                        string hex = BitConverter.ToString(data, 0, Math.Min(data.Length, 20));
                        try { BeginInvoke(() => Log($"RAW[{shortId}] len={data.Length}: {hex}", Color.FromArgb(255, 200, 80))); } catch { }
                    }

                    // ── Byte-diff logger: scan ALL bytes (skip [0]=rolling counter) ──
                    // Output goes to Console so it's never lost even if UI thread is busy.
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

                    // ── Strip 0xA1 HID-over-GATT prefix if present ──────
                    // BLE GATT notifications from a HID service prepend 0xA1
                    // (ATT "Handle Value Notification" opcode for input reports).
                    // All logical offsets below assume it has been removed.
                    int off = (data.Length > 0 && data[0] == 0xA1) ? 1 : 0;

                    // Skip subcommand replies / too-short packets
                    if (data.Length < off + 8) return;
                    if (data[off + 0] == 0x21) return;   // subcommand reply

                    // ── Parse input report ──────────────────────────────────
                    var state = new JoyconState();
                    state.Buttons = (uint)data[off + 4]
                                  | ((uint)data[off + 5] << 8)
                                  | ((uint)data[off + 6] << 16)
                                  | ((uint)data[off + 7] << 24);

                    if (data.Length >= off + 16)
                    {
                        int lx = data[off + 10] | ((data[off + 11] & 0x0F) << 8);
                        int ly = (data[off + 11] >> 4) | (data[off + 12] << 4);
                        int rx = data[off + 13] | ((data[off + 14] & 0x0F) << 8);
                        int ry = (data[off + 14] >> 4) | (data[off + 15] << 4);

                        const int NS2_SENTINEL = 2047;
                        state.LeftStickX  = (lx == 0 || lx == NS2_SENTINEL) ? 1998 : lx;
                        state.LeftStickY  = (ly == 0 || ly == NS2_SENTINEL) ? 1998 : ly;
                        state.RightStickX = (rx == 0 || rx == NS2_SENTINEL) ? 1998 : rx;
                        state.RightStickY = (ry == 0 || ry == NS2_SENTINEL) ? 1998 : ry;

                        // Detect L vs R from sentinel (runs every report — unconditional).
                        // Joy-Con L: its right-stick slot is always 2047 (unused).
                        // Joy-Con R: its left-stick slot is always 2047 (unused).
                        bool lxSentinel = lx == NS2_SENTINEL && ly == NS2_SENTINEL;
                        bool rxSentinel = rx == NS2_SENTINEL && ry == NS2_SENTINEL;

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
                            try { BeginInvoke(() => Log(
                                $"STICK[{shortId}]({sideTag}) LX={lxC} LY={lyC} RX={rxC} RY={ryC}",
                                Color.FromArgb(100, 200, 255))); } catch { }
                            stickLoggedDevices.Add(deviceId);  // never fire again for this device
                        }
                    }

                    _deviceStates[deviceId] = state;

                    // ── Debug: log only when buttons change or stick moves >80 counts ──
                    // (Never BeginInvoke on every report — that floods the UI thread queue)
                    if (IsHandleCreated)
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
                                BeginInvoke(() => Log(line, btnChanged
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
                            || Math.Abs(merged.LeftStickX  - lastMerged.LeftStickX)  > 30
                            || Math.Abs(merged.LeftStickY  - lastMerged.LeftStickY)  > 30
                            || Math.Abs(merged.RightStickX - lastMerged.RightStickX) > 30
                            || Math.Abs(merged.RightStickY - lastMerged.RightStickY) > 30;
                        if (mergedChanged)
                        {
                            lastMerged = merged;
                            try { BeginInvoke(() => UpdateInputDisplay(merged)); } catch { }
                        }
                    }
                };

                // ── Scan ─────────────────────────────────────────────────
                Invoke(() =>
                {
                    _lblJoyconStatus.Text      = "Scanning…";
                    _lblJoyconStatus.ForeColor = YELLOW;
                    Log("Checking paired devices + scanning for advertising ones…", ACCENT);
                    Log("Tip: Joy-Con 2 already paired in Windows? — it will connect automatically.", TXT_DIM);
                });

                using var scanTimeout = new CancellationTokenSource(30_000);
                using var linkedScan  = CancellationTokenSource.CreateLinkedTokenSource(ct, scanTimeout.Token);
                try   { await scanner.ScanAsync(linkedScan.Token); }
                catch { /* scan cancelled or timed out */ }

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

                Invoke(() =>
                {
                    bool dual = ids.Length >= 2;
                    string lId = _leftDeviceId?[..Math.Min(8, _leftDeviceId.Length)] ?? "?";
                    string rId = _rightDeviceId?[..Math.Min(8, _rightDeviceId.Length)] ?? "?";
                    string msg = dual
                        ? $"Both Joy-Cons connected! L={lId}  R={rId}"
                        : $"Joy-Con 2 connected ({ids[0][..Math.Min(12, ids[0].Length)]})";
                    Log(msg, GREEN);
                    _lblJoyconStatus.Text      = dual ? "L + R Connected ✔" : "Connected ✔";
                    _lblJoyconStatus.ForeColor = GREEN;
                });

                // ── Send player LEDs ─────────────────────────────────────
                // Sort so L gets player-1 LED, R gets player-2 LED
                var sortedIds = ids
                    .OrderBy(id => scanner.GetProductId(id) == BLEScanner.PID_JOYCON_R ? 1 : 0)
                    .ToArray();
                for (int p = 0; p < sortedIds.Length; p++)
                {
                    string id = sortedIds[p];
                    try
                    {
                        var ledCmd = Joycon2PC.Core.SubcommandBuilder.BuildNS2PlayerLed(p + 1);
                        await scanner.SendSubcommandAsync(id, ledCmd);
                        ushort pid2 = scanner.GetProductId(id);
                        string side = pid2 == BLEScanner.PID_JOYCON_L ? "L" : pid2 == BLEScanner.PID_JOYCON_R ? "R" : "?";
                        int pNum = p + 1;
                        Invoke(() => Log($"  Player {pNum} LED → Joy-Con {side}", ACCENT));
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => Log($"LED command failed: {ex.Message}", YELLOW));
                    }
                }

                // ── Wait: stay here until all devices disconnect or user stops ─
                while (!ct.IsCancellationRequested && scanner.GetKnownDeviceIds().Length > 0)
                {
                    try { await Task.Delay(500, ct); } catch { break; }
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

        /// <summary>
        /// After scan, confirm _leftDeviceId / _rightDeviceId.
        /// Priority: (1) PnP product ID, (2) sentinel-based detection already done in
        /// RawReportReceived, (3) arbitrary fallback for single device.
        /// </summary>
        private void AssignDeviceIds(BLEScanner scanner, string[] ids)
        {
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

            // Buttons — OR all devices
            foreach (var s in _deviceStates.Values)
                merged.Buttons |= s.Buttons;

            bool isSingleDevice = _leftDeviceId != null
                                && _leftDeviceId == _rightDeviceId;
            bool dualMode = !isSingleDevice
                          && _leftDeviceId != null
                          && _rightDeviceId != null;

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
            DrawStick(_pnlLStick, state.LeftStickX,  state.LeftStickY);
            DrawStick(_pnlRStick, state.RightStickX, state.RightStickY);

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
            SetBtn("Up",   state.IsPressed(SW2Button.Up));
            SetBtn("Dn",   state.IsPressed(SW2Button.Down));
            SetBtn("Lt",   state.IsPressed(SW2Button.Left));
            SetBtn("Rt",   state.IsPressed(SW2Button.Right));
        }

        private void SetBtn(string name, bool pressed)
        {
            if (!_btnIndicators.TryGetValue(name, out var p)) return;
            var onColor = (Color)(p.Tag ?? GREEN);
            p.BackColor = pressed ? onColor : Color.FromArgb(50, 50, 65);
            if (p.Controls.Count > 0)
                ((Label)p.Controls[0]).ForeColor = pressed ? Color.White : TXT_DIM;
        }

        private void DrawStick(Panel panel, int rawX, int rawY)
        {
            // NS2 12-bit raw → normalised -1..1  (factory centre = 1998)
            const float ns2Centre = 1998f;
            const float ns2Range  = 1251f;  // ≈ half of 3249-746, used for symmetry
            float nx = Math.Clamp((rawX - ns2Centre) / ns2Range, -1f, 1f);
            float ny = Math.Clamp((rawY - ns2Centre) / ns2Range, -1f, 1f);

            var bmp = new Bitmap(panel.Width, panel.Height);
            using var g   = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(22, 22, 30));

            // outer ring
            g.DrawEllipse(new Pen(Color.FromArgb(70, 70, 90), 1),
                1, 1, panel.Width - 3, panel.Height - 3);

            // centre crosshair
            g.DrawLine(new Pen(Color.FromArgb(50, 50, 70), 1),
                panel.Width / 2, 0, panel.Width / 2, panel.Height);
            g.DrawLine(new Pen(Color.FromArgb(50, 50, 70), 1),
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
        private void Log(string text, Color? color = null)
        {
            if (_log.InvokeRequired) { Invoke(() => Log(text, color)); return; }

            string time = DateTime.Now.ToString("HH:mm:ss");
            _log.SelectionStart  = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor  = Color.FromArgb(70, 70, 90);
            _log.AppendText($"[{time}] ");
            _log.SelectionColor  = color ?? TXT;
            _log.AppendText(text + "\n");
            _log.ScrollToCaret();

            // keep log to 300 lines
            if (_log.Lines.Length > 300)
            {
                _log.Select(0, _log.GetFirstCharIndexFromLine(50));
                _log.SelectedText = "";
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  FORM CLOSE
        // ══════════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            _bridge?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
