
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

        private readonly object _outputStateLock = new();
        private readonly object _stateLock = new();

        // NS2 factory rest position for a 12-bit stick field. Used to neutralise
        // and to detect an idle stick slot. (The raw -> XInput calibration
        // constants live in Joycon2PC.Core.AxisCalibration, not here.)
        private const int NS2_CENTRE = 1998;

        // Single latest-value mailbox for the non-INTHEHAND parser path, which has
        // no per-device split to merge. In the INTHEHAND path the pump takes its
        // own coherent snapshot of both devices directly (see OutputPumpLoopAsync)
        // and never reads this, because a latest-value mailbox is a zero-order-hold
        // staircase: the same value gets submitted twice in a row before the next
        // one arrives.
#if !INTHEHAND
        private JoyconState _latestPumpedState = new();
        private bool _latestPumpedStateAvailable;
#endif

        // UI repaint coalescing: at most one BeginInvoke in flight, always rendering
        // the newest state. Intermediate frames are dropped rather than queued.
        private readonly object _uiStateLock = new();
        private JoyconState _pendingUiState = new();
        private int _uiRefreshPending;

        private CancellationTokenSource? _outputPumpCts;
        private Task? _outputPumpTask;
        private const int OUTPUT_PUMP_HZ = 120;
        private static readonly TimeSpan OUTPUT_PUMP_INTERVAL = TimeSpan.FromMilliseconds(1000.0 / OUTPUT_PUMP_HZ);

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

            #if !INTHEHAND
            lock (_outputStateLock)
            {
                _latestPumpedStateAvailable = false;
            }
#endif
        }

        private async Task OutputPumpLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                JoyconState? state = null;
                string? leftDeviceId = null;
                string? rightDeviceId = null;

#if INTHEHAND
                // joycon2cpp TimedInputBuffer pattern: the merge happens HERE, on the
                // pump thread, from ONE coherent snapshot of both devices taken under
                // _stateLock. The BLE handler's only job under the lock is to publish
                // its own per-device JoyconState.
                //
                // This fixes two distinct artefacts of the previous arrangement:
                //  - latest-value mailbox staircase (same merged value submitted
                //    twice in a row before jumping to the next), and
                //  - differently-phased halves: the merge used to read each device's
                //    most recent value, so when one side updated, the other side's
                //    axes were held from an older, differently-phased report.
                // Both sides now always come from the same pump tick.
                //
                // It also removes one heap allocation per report (the merged-state
                // clone the BLE handler used to push into the mailbox).
                state = MergeDeviceStates(out leftDeviceId, out rightDeviceId);
#else
                lock (_outputStateLock)
                {
                    if (_latestPumpedStateAvailable)
                        state = CloneState(_latestPumpedState);
                }
#endif

                if (state != null)
                {
                    _lastState = state;
                    try { _bridge?.UpdateFromState(state, leftDeviceId, rightDeviceId); } catch { }
                    RequestInputDisplayRefresh(state);
                }

                try { await Task.Delay(OUTPUT_PUMP_INTERVAL, ct); } catch { break; }
            }
        }

        /// <summary>
        /// Coalesces stick/button repaints onto the UI thread.
        ///
        /// One input report used to mean one BeginInvoke. At BLE rate that floods
        /// the WinForms message queue with repaints that all render the same thing,
        /// and JoyConVisualizerPanel.OnPaint allocates a Font per paint, so every
        /// queued delegate is also a GDI+ allocation. Instead: keep the newest state
        /// in a field, and only queue a new delegate when none is in flight. Frames
        /// that arrive while a delegate is queued simply overwrite the pending
        /// state, so the UI renders the latest snapshot once it gets CPU.
        /// </summary>
        private void RequestInputDisplayRefresh(JoyconState state)
        {
            if (!IsHandleCreated || IsDisposed) return;

            lock (_uiStateLock)
            {
                _pendingUiState = state;
            }

            // 0 -> 1 means we just became the single in-flight request.
            if (Interlocked.Exchange(ref _uiRefreshPending, 1) == 1) return;

            try
            {
                BeginInvoke(() =>
                {
                    // Release the in-flight slot BEFORE reading the state: if a new
                    // report lands after this point it will queue its own delegate
                    // (rather than being swallowed by a stale in-flight one).
                    Interlocked.Exchange(ref _uiRefreshPending, 0);

                    JoyconState latest;
                    lock (_uiStateLock)
                    {
                        latest = _pendingUiState;
                    }

                    UpdateInputDisplay(latest);
                });
            }
            catch
            {
                // Handle destroyed / shutting down: do not leave the slot stuck.
                Interlocked.Exchange(ref _uiRefreshPending, 0);
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
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        //  REAL JOYCON LOOP  (dual Joy-Con merge + keep-alive + reconnect)
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Per-device state tracking for dual Joy-Con merge
        private readonly Dictionary<string, JoyconState> _deviceStates = new();
        private string? _leftDeviceId;
        private string? _rightDeviceId;
        // Raw sentinel flags: true = that stick slot is unused (sentinel 2047) for this device
        private readonly Dictionary<string, bool> _deviceLStickSentinel = new();
        private readonly Dictionary<string, bool> _deviceRStickSentinel = new();

        // Single-device mode only: which stick byte range is currently believed to
        // carry the physical stick (true = RightStick bytes, false = LeftStick
        // bytes, null = no choice made yet). Held across ticks by the hysteresis in
        // MergeDeviceStates and guarded by _stateLock.
        private bool? _singleStickFromRight;

        private async Task RunRealAsync(CancellationToken ct)
        {
            // ------ outer reconnect loop ------------------------------------------------------------------------------------------------------------------
            while (!ct.IsCancellationRequested)
            {
                DateTime lastReportUtc = DateTime.UtcNow;
                DateTime lastInputModeRecoveryUtc = DateTime.MinValue;
                var lastReportByDevice = new Dictionary<string, DateTime>();
                var deviceReportLock = new object();
                var scanner = new BLEScanner();
                _scanner = scanner;
                lock (_stateLock)
                {
                    _deviceStates.Clear();
                    _leftDeviceId = null;
                    _rightDeviceId = null;
                    _deviceLStickSentinel.Clear();
                    _deviceRStickSentinel.Clear();
                    _singleStickFromRight = null;
                }

                // ------ hex-dump and change-tracking ------------------------------------------------------------------------
                var dumpCounts          = new Dictionary<string, int>();
                var stickLoggedDevices  = new HashSet<string>();   // prevent dc==3 flood
                var lastButtons         = new Dictionary<string, uint>();
                var lastStickLog        = new Dictionary<string, (int lx, int ly, int rx, int ry)>();
                var lastRawBytes        = new Dictionary<string, byte[]>(); // for byte-diff logger

                // Warmup gate: Joy-Con 2 controllers send a burst of garbage button data
                // (L+ZL bits asserted) during their BLE init sequence (~200-400 ms).
                // We suppress all button output for the first WARMUP_REPORTS reports per
                // device. Stick data and sentinel detection are unaffected --- they read
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

                        // ------ Byte-diff logger: scan ALL bytes (skip [0]=rolling counter) ------
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

                        // Detect L vs R from sentinel (runs every report --- unconditional).
                        // Joy-Con L: its right-stick slot is always 2047 (unused).
                        // Joy-Con R: its left-stick slot is always 2047 (unused).
                        bool lxSentinel = decoded.RawLeftStickX == NS2InputReportDecoder.SentinelStickValue
                                       && decoded.RawLeftStickY == NS2InputReportDecoder.SentinelStickValue;
                        bool rxSentinel = decoded.RawRightStickX == NS2InputReportDecoder.SentinelStickValue
                                       && decoded.RawRightStickY == NS2InputReportDecoder.SentinelStickValue;

                        // Persist raw sentinel flags and side IDs under a shared lock.
                        // Reconnect can clear these collections concurrently.
                        string sideTag;
                        string side2;
                        lock (_stateLock)
                        {
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

                            sideTag = deviceId == _leftDeviceId && deviceId == _rightDeviceId ? "S"
                                : deviceId == _leftDeviceId ? "L"
                                : deviceId == _rightDeviceId ? "R"
                                : "?";

                            side2 = deviceId == _leftDeviceId && deviceId == _rightDeviceId ? "S"
                                : deviceId == _leftDeviceId ? "L"
                                : deviceId == _rightDeviceId ? "R"
                                : "?";
                        }

                        // Log ONCE per device - exactly when dump count reaches 3
                        dumpCounts.TryGetValue(deviceId, out int dc);
                        if (dc == 3 && !stickLoggedDevices.Contains(deviceId)) // fire exactly once
                        {
                            int lxC = state.LeftStickX, lyC = state.LeftStickY;
                            int rxC = state.RightStickX, ryC = state.RightStickY;
                            try { BeginInvoke(() => DevLog(
                                $"STICK[{shortId}]({sideTag}) LX={lxC} LY={lyC} RX={rxC} RY={ryC}",
                                Color.FromArgb(100, 200, 255))); } catch { }
                            stickLoggedDevices.Add(deviceId);  // never fire again for this device
                        }

                        // ------ Warmup gate: suppress buttons until controller has settled ------
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

                        // Publish final per-device state only after all in-frame mutations complete.
                        // JoyconState is mutable; publishing early can leak partially updated values.
                        lock (_stateLock)
                        {
                            _deviceStates[deviceId] = new JoyconState
                            {
                                Buttons = state.Buttons,
                                LeftStickX = state.LeftStickX,
                                LeftStickY = state.LeftStickY,
                                RightStickX = state.RightStickX,
                                RightStickY = state.RightStickY,
                            };
                        }

                        ApplyMouseModeFromDevice(deviceId, data, state);

                        // ------ Debug: log only when buttons change or stick moves >80 counts ------
                        // (Never BeginInvoke on every report --- that floods the UI thread queue)
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

                        // ------ Done on this thread. --------------------------------------------------------
                        // Nothing else happens here. The merge, the ViGEm submit and
                        // the UI repaint all belong to OutputPumpLoopAsync, which
                        // snapshots both devices under _stateLock once per tick.
                        // Doing the merge per-report meant:
                        //   - one merged-state clone allocated per BLE report, and
                        //   - a zero-order-hold mailbox: the pump submitted the same
                        //     merged value twice in a row before the next arrived,
                        //     which reads as a staircase on the virtual stick.
                    }
                    finally
                    {
                        double processMs = (Stopwatch.GetTimestamp() - diagArrivalTicks) * 1000.0 / Stopwatch.Frequency;
                        RecordDiagnosticSample(deviceId, diagArrivalTicks, processMs, decodeOk, sizeLooksValid);
                    }
                };

                using var scanTimeout = new CancellationTokenSource(30_000);
                var scanReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // ------ Immediate status update when a device connects ------------------------
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

                // ------ Scan ---------------------------------------------------------------------------------------------------------------------------------------------------
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
                        try { await scanTask; }
                        catch (OperationCanceledException) { }
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
                // ------ Assign L / R IDs from PnP (definitive, overrides early guess) ---
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

                // ------ Post-connect init: switch to continuous full-rate reporting ---
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

                // ------ Wait: stay here until all devices disconnect or user stops ---
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
            lock (_stateLock)
            {
                // Pass 0: device name --- most reliable (Windows names: "Joy-Con 2 (L)" / "Joy-Con 2 (R)")
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
                    // R slot sentinel --- this is Joy-Con L
                    if (rSentinel && !lSentinel && newLeft  == null) newLeft  = id;
                    // L slot sentinel --- this is Joy-Con R
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
        /// Buttons  --- OR'd from every device (each Joy-Con carries its own half of the buttons).
        /// L Stick  --- from the device identified as Joy-Con L (bytes [10..12] of its report).
        /// R Stick  --- from the device identified as Joy-Con R.
        ///             Joy-Con R sends its physical stick at the RIGHT-stick bytes [13..15];
        ///             if those are at centre (1998) we also try the left-stick bytes [10..12]
        ///             as a fallback in case the protocol puts it there on some firmware.
        ///
        /// Called ONLY from OutputPumpLoopAsync, so the whole snapshot below is one
        /// coherent tick: every field comes from a single _stateLock acquisition.
        /// </summary>
        /// <param name="leftDeviceId">Receives the device id currently owning the left stick.</param>
        /// <param name="rightDeviceId">Receives the device id currently owning the right stick.</param>
        private JoyconState MergeDeviceStates(out string? leftDeviceId, out string? rightDeviceId)
        {
            lock (_stateLock)
            {
                leftDeviceId = _leftDeviceId;
                rightDeviceId = _rightDeviceId;

                // Default sticks to centre so the visualiser and ViGEm start neutral
                var merged = new JoyconState
                {
                    LeftStickX  = NS2_CENTRE, LeftStickY  = NS2_CENTRE,
                    RightStickX = NS2_CENTRE, RightStickY = NS2_CENTRE,
                };

                if (_deviceStates.Count == 0) return merged;

            // ------ Dual / single device detection ------------------------------------------------------------------------------------
            bool isSingleDevice = _leftDeviceId != null
                                && _leftDeviceId == _rightDeviceId;
            bool dualMode = !isSingleDevice
                          && _leftDeviceId != null
                          && _rightDeviceId != null;

            // ------ Buttons --- side-masked OR in dual mode ---------------------------------------------------------
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
                // Single controller or IDs not yet assigned --- OR all buttons as-is.
                foreach (var s in _deviceStates.Values)
                    merged.Buttons |= s.Buttons;
            }

            if (isSingleDevice)
            {
                // Single controller --- use whichever stick bytes are NOT at rest (1998).
                // Joy-Con L: real data on LeftStick bytes [10..12], RightStick = 1998
                // Joy-Con R: real data on RightStick bytes [13..15], LeftStick = 1998
                if (_deviceStates.TryGetValue(_leftDeviceId!, out var s))
                {
                    // Deviation measured as the larger of the two axes on a slot.
                    int lDev = Math.Max(Math.Abs(s.LeftStickX  - NS2_CENTRE), Math.Abs(s.LeftStickY  - NS2_CENTRE));
                    int rDev = Math.Max(Math.Abs(s.RightStickX - NS2_CENTRE), Math.Abs(s.RightStickY - NS2_CENTRE));

                    // Dual-threshold hysteresis on the axis-source decision.
                    // The previous code used a bare `> 50` on every report, so noise
                    // sitting near the threshold made the stick source flip back and
                    // forth between the two byte ranges. Entering a source needs
                    // AXIS_SOURCE_ENTER counts of travel; holding it only needs the
                    // sample to stay within AXIS_SOURCE_EXIT of the rest position.
                    // This is the same idea, and deliberately the same shape, as
                    // `sideMappingLocked` in the report handler above which locks
                    // L/R *device* ownership: both stick ownership decisions are
                    // sticky, they just lock different things (which device owns the
                    // stick vs which byte range carries it).
                    const int AXIS_SOURCE_ENTER = 80;
                    const int AXIS_SOURCE_EXIT  = 30;

                    bool lReal = lDev > (_singleStickFromRight == false ? AXIS_SOURCE_EXIT : AXIS_SOURCE_ENTER);
                    bool rReal = rDev > (_singleStickFromRight == true  ? AXIS_SOURCE_EXIT : AXIS_SOURCE_ENTER);

                    bool useRight;
                    if (rReal && lReal)
                    {
                        // Both slots look live (some firmware mirrors the stick, or
                        // both are off-centre). Keep the previous choice rather
                        // than oscillating between them; default to the left range.
                        useRight = _singleStickFromRight ?? false;
                    }
                    else
                    {
                        useRight = rReal && !lReal;
                    }

                    _singleStickFromRight = useRight;

                    if (useRight)
                    {
                        // Joy-Con R solo: physical stick --- LeftStick output (primary axis for games)
                        merged.LeftStickX  = s.RightStickX;
                        merged.LeftStickY  = s.RightStickY;
                    }
                    else
                    {
                        // Joy-Con L solo or unknown: physical stick --- LeftStick output
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
                // the R controller hasn't sent a real value yet --- leave merged at 1998.
                if (_deviceStates.TryGetValue(_rightDeviceId!, out var rs))
                {
                    // RightStick field is authoritative for Joy-Con R.
                    // LeftStick field on the R device = 1998 (sentinel was neutralised) --- ignore it.
                    merged.RightStickX = rs.RightStickX;
                    merged.RightStickY = rs.RightStickY;
                }
            }
            else
            {
                // IDs not yet fully assigned --- output what we have but do NOT clobber IDs here.
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
        }
#endif

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

        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        //  PARSER CALLBACK  (called from background thread)
        // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        private void OnStateChanged(JoyconState state)
        {
            // Never write ViGEm from this thread: the StickFilter instances are
            // thread-confined to the output pump. In an INTHEHAND build the pump
            // merges live device state itself and this callback only keeps the UI
            // in sync; otherwise it hands the sample to the pump mailbox and the
            // 120 Hz loop drains it.
#if !INTHEHAND
            lock (_outputStateLock)
            {
                _latestPumpedState = CloneState(state);
                _latestPumpedStateAvailable = true;
            }
#endif

            RequestInputDisplayRefresh(state);
        }

        private void UpdateInputDisplay(JoyconState state)
        {
            // ------ Stick visualisers ------------------------------------------------------------------------------------------------------------------------------
            _joyconViz?.SetSticks(state.LeftStickX, state.LeftStickY, state.RightStickX, state.RightStickY);

            // ------ Button indicators ------------------------------------------------------------------------------------------------------------------------------
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
    }
}
