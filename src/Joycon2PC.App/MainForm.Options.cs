
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
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
        //  OPTION INITIALISATION
        // ======================================================================================================================================
        private void InitializeOptionControls()
        {
            UpdateConnectModeStatusLabel("Idle", TXT_DIM);

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

            _cmbMouseStabilizer.Items.AddRange(new object[] { "Raw", "Stable", "Very stable" });
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

            _cmbMouseSpeed.Items.AddRange(new object[] { "Fast", "Normal", "Slow" });
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
            _cmbDeviceTarget.SelectedIndex = Math.Min(selectedIndex, Math.Max(0, _cmbDeviceTarget.Items.Count - 1));
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

        // ======================================================================================================================================
        //  MANUAL FEEDBACK
        // ======================================================================================================================================
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

        // ======================================================================================================================================
        //  VIGEM CHECK
        // ======================================================================================================================================
        // WHY THE BRIDGE EVENTS EXIST
        // ---------------------------
        // ViGEmBridge.Connect() never throws: it catches everything internally. It used
        // to report the reason with Console.WriteLine, which is a no-op here because
        // Joycon2PC.App is OutputType=WinExe and has no console attached. So on a
        // machine with no ViGEmBus the app reported nothing at all -- and worse, the
        // old code below set the status card to a green "Driver found and ready"
        // purely because Connect() had returned, which it does even when it failed.
        // It now asks the bridge, and everything user-visible is driven from the two
        // event handlers.
        private bool _vigemFailureReported;

        private async Task CheckViGEmAsync()
        {
            await Task.Delay(200); // let window paint first
            try
            {
                var bridge = new Joycon2PC.ViGEm.ViGEmBridge();
                // Subscribe before Connect(): the failure is raised from inside it.
                bridge.ConnectionFailed += OnViGEmConnectionFailed;
                bridge.UpdateFailed     += OnViGEmUpdateFailed;
                _bridge = bridge;

                bridge.Connect();

                // Connect() returning is not success -- it returns void and swallows
                // the exception. Without this check the card would claim "ready" on a
                // machine with no driver at all. The failure handler has already run
                // by this point, so just leave its red state alone.
                if (!bridge.IsConnected)
                    return;

                Invoke(() =>
                {
                    _lblVigemStatus.Text      = "Driver found and ready";
                    _lblVigemStatus.ForeColor = GREEN;
                    _cardVigem.Invalidate();
                    Log("ViGEmBus driver found. Virtual Xbox 360 controller is ready.", GREEN);
                });
            }
            catch (Exception ex)
            {
                // Defensive: Connect() is written to swallow its own exceptions, so
                // in practice this only guards a disposed form or a driver that fails
                // outside the bridge's own try.
                OnViGEmConnectionFailed(ex.Message);
            }
        }

        /// <summary>
        /// The virtual controller could not be created. Carries the driver exception
        /// text verbatim into the log, because that text is the actionable part: it
        /// distinguishes "ViGEmBus is not installed" from "the device is disabled in
        /// Device Manager", and both look identical from the outside otherwise.
        /// Raised on the check thread; marshals to the UI thread.
        /// </summary>
        private void OnViGEmConnectionFailed(string reason)
        {
            if (InvokeRequired)
            {
                Invoke(() => OnViGEmConnectionFailed(reason));
                return;
            }
            if (IsDisposed || Disposing || !IsHandleCreated || _vigemFailureReported)
                return;
            _vigemFailureReported = true;

            _lblVigemStatus.Text      = "ViGEmBus not installed";
            _lblVigemStatus.ForeColor = RED;
            _cardVigem.Invalidate();
            Log("ViGEmBus driver not found! Install it from:", RED);
            Log("  https://github.com/nefarius/ViGEmBus/releases", ACCENT);
            Log("Virtual controller will not appear in Windows without ViGEmBus.", YELLOW);
            Log($"Driver reported: {reason}", RED);
        }

        /// <summary>
        /// A live virtual controller started rejecting reports -- the bus survived the
        /// startup check but went away (driver disabled, device removed, game exited
        /// the exclusive session). Throttled to ~1 line per 10 s on the bridge side,
        /// because the caller here is the 120 Hz output pump. Yellow rather than red:
        /// the driver is present, the link is broken.
        /// </summary>
        private void OnViGEmUpdateFailed(string reason)
        {
            if (InvokeRequired)
            {
                Invoke(() => OnViGEmUpdateFailed(reason));
                return;
            }
            if (IsDisposed || Disposing || !IsHandleCreated)
                return;

            _lblVigemStatus.Text      = "Controller link lost";
            _lblVigemStatus.ForeColor = YELLOW;
            _cardVigem.Invalidate();
            Log($"Virtual controller stopped accepting reports: {reason}", YELLOW);
            Log("Reinstall or re-enable ViGEmBus in Device Manager, then restart Joycon2PC.", TXT_DIM);
        }

        // ======================================================================================================================================
        //  START / STOP
        // ======================================================================================================================================
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
                _cardLink.Invalidate();
                UpdateConnectModeStatusLabel("Recycling BLE session", YELLOW);

                _scanner?.DisconnectAll();
                lock (_stateLock)
                {
                    _deviceStates.Clear();
                    _leftDeviceId = null;
                    _rightDeviceId = null;
                    _deviceLStickSentinel.Clear();
                    _deviceRStickSentinel.Clear();
                }

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

            // Attach parser --- bridge
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
            _cardLink.Invalidate();
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
            _cardLink.Invalidate();
            UpdateConnectModeStatusLabel("Stopped", TXT_DIM);
            UpdateLinkHealthSummary();
            Log("Stopped.", TXT_DIM);
        }

        // ======================================================================================================================================
        //  LOG EXPORT
        // ======================================================================================================================================
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
                lines.AddRange(new[]
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

        // ======================================================================================================================================
        //  LOGGING
        // ======================================================================================================================================
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

        // ======================================================================================================================================
        //  MOUSE MODE  (input logic unchanged - only the controls moved)
        // ======================================================================================================================================
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
                    // Up on stick (higher raw Y) -> positive wheel delta = scroll up in Windows.
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
    }
}
