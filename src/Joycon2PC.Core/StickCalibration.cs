using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Joycon2PC.Core
{
    /// <summary>Which physical stick axis a calibration profile describes.</summary>
    public enum StickAxis
    {
        LeftX = 0,
        LeftY = 1,
        RightX = 2,
        RightY = 3,
    }

    /// <summary>
    /// One axis' asymmetric raw-&gt;XInput normalisation profile.
    ///
    /// The Joy-Con 2 factory travel is asymmetric: min 746 / centre 1998 / max
    /// 3249 gives 1252 counts below centre but only 1251 above, and real units
    /// drift further from that. Scaling both directions by a single half-range
    /// therefore saturates one end before the other, which is why the two
    /// directions are normalised independently:
    ///
    ///     raw &gt;= Center :  (raw - Center) / (Max - Center) * 32767
    ///     raw &lt;  Center :  (Center - raw) / (Center - Min) * 32767
    ///
    /// The <em>idea</em> (per-axis asymmetric normalisation) comes from
    /// TheFrano/joycon2cpp. Its implementation is not copied: the scale here is a
    /// 64-bit numerator with round-half-up on the magnitude rather than a
    /// truncating integer divide, and centre only ever comes from a
    /// caller-declared rest frame (or, on the capture path, from that rest frame
    /// plus the observed extrema) -- so resting noise is never baked into the
    /// middle of the deadzone.
    /// </summary>
    public readonly struct AxisCalibration
    {
        /// <summary>Factory default low end (NS2-Connect.py / BetterJoy-NS2).</summary>
        public const int DefaultMin = 746;

        /// <summary>Factory default centre.</summary>
        public const int DefaultCenter = 1998;

        /// <summary>Factory default high end.</summary>
        public const int DefaultMax = 3249;

        /// <summary>Highest value a 12-bit stick field can carry.</summary>
        public const int MaxRawValue = 4095;

        public AxisCalibration(int center, int min, int max, bool isCalibrated)
        {
            Center = center;
            Min = min;
            Max = max;
            IsCalibrated = isCalibrated;
        }

        /// <summary>Factory defaults, flagged as not user-calibrated.</summary>
        public static AxisCalibration Default => new(DefaultCenter, DefaultMin, DefaultMax, false);

        public int Center { get; }

        public int Min { get; }

        public int Max { get; }

        /// <summary>True when this profile came from a recorded capture rather than the factory defaults.</summary>
        public bool IsCalibrated { get; }

        /// <summary>
        /// A profile is usable only if it is non-degenerate: centre strictly
        /// between min and max, all three inside the 12-bit input range. Anything
        /// else (Max &lt;= Center, Center &lt;= Min, garbage from a hand-edited
        /// JSON file) must fall back to <see cref="Default"/> rather than divide by
        /// zero or produce nonsense.
        /// </summary>
        public bool IsUsable
            => Min >= 0
            && Max <= MaxRawValue
            && Center >= 0
            && Center <= MaxRawValue
            && Max > Center
            && Center > Min;

        /// <summary>
        /// Normalises a raw 12-bit stick value to the signed-16-bit XInput domain,
        /// clamping the result to [-32767, 32767].
        /// </summary>
        public int Normalize(int raw)
        {
            if (!IsUsable) return 0;

            // Clamping the raw value into [Min, Max] first keeps the numerator
            // bounded by the denominator, so the product can never overflow and
            // an out-of-range sample saturates instead of wrapping.
            if (raw < Min) raw = Min;
            if (raw > Max) raw = Max;

            long numerator;
            long denominator;
            if (raw >= Center)
            {
                numerator = raw - (long)Center;
                denominator = Max - (long)Center;
            }
            else
            {
                numerator = Center - (long)raw;
                denominator = Center - (long)Min;
            }

            if (denominator <= 0) return 0;

            // Round-half-up on the magnitude, sign applied afterwards. This is the
            // fix for the old `centered * 32767 / halfRange` truncation.
            long magnitude = (numerator * short.MaxValue + denominator / 2) / denominator;
            if (magnitude > short.MaxValue) magnitude = short.MaxValue;

            return raw >= Center ? (int)magnitude : (int)-magnitude;
        }
    }

    /// <summary>
    /// Per-device analog stick calibration profiles, persisted as JSON.
    ///
    /// API surface is deliberately small:
    ///   Load() / TrySave()  - %APPDATA%\Joycon2PC\calibration.json
    ///   GetAxis() / Normalize() - read path, always returns something usable
    ///   SetAxis()           - explicit profile assignment (also returns validity)
    ///   BeginCapture() / RecordSample() / EndCapture() - the measurement flow
    ///
    /// Serialisation uses System.Text.Json. joycon2cpp hand-rolls a substring
    /// scanner for its config file; that is an anti-pattern and is not copied.
    /// </summary>
    public sealed class StickCalibration
    {
        public const string AppFolderName = "Joycon2PC";
        public const string FileName = "calibration.json";
        private const int FileVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly object _gate = new();
        private readonly Dictionary<string, DeviceProfile> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Capture> _captures = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;

        /// <param name="path">Overrides the default %APPDATA% location; used by tests.</param>
        public StickCalibration(string? path = null)
        {
            _path = string.IsNullOrEmpty(path) ? DefaultPath() : path!;
        }

        public static string DefaultPath()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName,
                FileName);

        /// <summary>Absolute path this instance reads from and writes to.</summary>
        public string FilePath => _path;

        /// <summary>
        /// Loads profiles from disk. A missing, unreadable, malformed or partially
        /// corrupt file is not an error: it just leaves the factory defaults in
        /// place. Returns false only when nothing could be loaded.
        /// </summary>
        public bool Load()
        {
            try
            {
                if (!File.Exists(_path)) return false;

                string json = File.ReadAllText(_path);
                var file = JsonSerializer.Deserialize<CalibrationFile>(json);
                if (file?.Devices == null || file.Devices.Count == 0) return false;

                lock (_gate)
                {
                    _devices.Clear();
                    foreach (var pair in file.Devices)
                    {
                        if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;

                        var device = new DeviceProfile();
                        device.Axes[StickAxis.LeftX] = ToAxis(pair.Value.LeftX);
                        device.Axes[StickAxis.LeftY] = ToAxis(pair.Value.LeftY);
                        device.Axes[StickAxis.RightX] = ToAxis(pair.Value.RightX);
                        device.Axes[StickAxis.RightY] = ToAxis(pair.Value.RightY);
                        _devices[pair.Key] = device;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Writes the current profiles to disk atomically-ish. Returns false on any I/O failure.</summary>
        public bool TrySave()
        {
            try
            {
                CalibrationFile snapshot;
                lock (_gate)
                {
                    snapshot = new CalibrationFile();
                    foreach (var pair in _devices)
                    {
                        var device = new DeviceProfileFile();
                        device.LeftX = FromAxis(pair.Value.Axes[StickAxis.LeftX]);
                        device.LeftY = FromAxis(pair.Value.Axes[StickAxis.LeftY]);
                        device.RightX = FromAxis(pair.Value.Axes[StickAxis.RightX]);
                        device.RightY = FromAxis(pair.Value.Axes[StickAxis.RightY]);
                        snapshot.Devices[pair.Key] = device;
                    }
                }

                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);

                File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Effective profile for one device axis. Always returns a usable profile:
        /// an unknown device, an uncalibrated axis or a degenerate stored profile
        /// all yield <see cref="AxisCalibration.Default"/>.
        /// </summary>
        public AxisCalibration GetAxis(string? deviceId, StickAxis axis)
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                lock (_gate)
                {
                    if (_devices.TryGetValue(deviceId!, out var device)
                        && device.Axes.TryGetValue(axis, out var axis0)
                        && axis0.IsCalibrated
                        && axis0.IsUsable)
                    {
                        return axis0;
                    }
                }
            }

            return AxisCalibration.Default;
        }

        /// <summary>Convenience: <c>GetAxis(...).Normalize(raw)</c>.</summary>
        public int Normalize(string? deviceId, StickAxis axis, int raw)
            => GetAxis(deviceId, axis).Normalize(raw);

        /// <summary>
        /// Stores an explicit profile for one device axis and reports whether it is
        /// usable. An unusable profile is stored anyway (so a UI can show what the
        /// user typed) but <see cref="GetAxis"/> will keep handing out the factory
        /// defaults for it - in particular, Max &lt;= Center never reaches the
        /// division in <see cref="AxisCalibration.Normalize"/>.
        /// </summary>
        public bool SetAxis(string deviceId, StickAxis axis, int center, int min, int max)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;

            var profile = new AxisCalibration(center, min, max, isCalibrated: true);
            lock (_gate)
            {
                if (!_devices.TryGetValue(deviceId, out var device))
                {
                    device = new DeviceProfile();
                    _devices[deviceId] = device;
                }
                device.Axes[axis] = profile;
            }

            return profile.IsUsable;
        }

        /// <summary>
        /// Starts measuring one device: from here on <see cref="RecordSample"/>
        /// tracks the observed per-axis extremes. Extrema (not a single frame) are
        /// what keeps resting noise from being baked into the centre of the range.
        /// </summary>
        public void BeginCapture(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            lock (_gate)
            {
                _captures[deviceId] = new Capture();
            }
        }

        /// <summary>Records one raw sample during a capture. No-op when no capture is open.</summary>
        public void RecordSample(string? deviceId, int rawLX, int rawLY, int rawRX, int rawRY)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            lock (_gate)
            {
                if (!_captures.TryGetValue(deviceId!, out var capture)) return;
                capture.Observe(StickAxis.LeftX, rawLX);
                capture.Observe(StickAxis.LeftY, rawLY);
                capture.Observe(StickAxis.RightX, rawRX);
                capture.Observe(StickAxis.RightY, rawRY);
            }
        }

        /// <summary>
        /// Closes a capture. <paramref name="rest*"/> are the raw values the caller
        /// observed while the stick was genuinely at rest; that frame supplies
        /// <c>Center</c>, while the observed extremes supply Min/Max. Any axis whose
        /// extremes do not strictly bracket the centre is left on the factory
        /// defaults. Returns true only if all four axes were committed.
        /// </summary>
        public bool EndCapture(string deviceId, int restLX, int restLY, int restRX, int restRY)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;

            Capture capture;
            lock (_gate)
            {
                if (!_captures.TryGetValue(deviceId, out var found)) return false;
                capture = found;
                _captures.Remove(deviceId!);
            }

            int[] rest =
            {
                restLX, restLY, restRX, restRY,
            };

            bool complete = true;
            lock (_gate)
            {
                if (!_devices.TryGetValue(deviceId, out var device))
                {
                    device = new DeviceProfile();
                    _devices[deviceId] = device;
                }

                for (int i = 0; i < 4; i++)
                {
                    var axis = (StickAxis)i;
                    int center = rest[i];
                    int min = capture.Min[axis];
                    int max = capture.Max[axis];

                    var candidate = new AxisCalibration(center, min, max, isCalibrated: true);
                    if (!candidate.IsUsable)
                    {
                        // Not enough travel was seen on this axis to trust it.
                        complete = false;
                        continue;
                    }

                    device.Axes[axis] = candidate;
                }
            }

            return complete;
        }

        /// <summary>Drops every stored profile for one device, restoring factory defaults.</summary>
        public void Reset(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            lock (_gate)
            {
                _devices.Remove(deviceId);
                _captures.Remove(deviceId);
            }
        }

        /// <summary>Drops every stored profile for every device.</summary>
        public void ResetAll()
        {
            lock (_gate)
            {
                _devices.Clear();
                _captures.Clear();
            }
        }

        // ── persistence DTOs ────────────────────────────────────────────────────
        // Plain mutable classes because System.Text.Json needs a parameterless
        // ctor and settable members.

        private static AxisCalibration ToAxis(AxisProfileFile? file)
            => file == null
                ? AxisCalibration.Default
                : new AxisCalibration(file.Center, file.Min, file.Max, file.IsCalibrated);

        private static AxisProfileFile FromAxis(AxisCalibration axis)
            => new()
            {
                Center = axis.Center,
                Min = axis.Min,
                Max = axis.Max,
                IsCalibrated = axis.IsCalibrated,
            };

        private sealed class DeviceProfile
        {
            public Dictionary<StickAxis, AxisCalibration> Axes { get; } = new()
            {
                [StickAxis.LeftX] = AxisCalibration.Default,
                [StickAxis.LeftY] = AxisCalibration.Default,
                [StickAxis.RightX] = AxisCalibration.Default,
                [StickAxis.RightY] = AxisCalibration.Default,
            };
        }

        private sealed class Capture
        {
            public Dictionary<StickAxis, int> Min { get; } = NewExtremes(int.MaxValue);

            public Dictionary<StickAxis, int> Max { get; } = NewExtremes(int.MinValue);

            public void Observe(StickAxis axis, int raw)
            {
                if (raw < Min[axis]) Min[axis] = raw;
                if (raw > Max[axis]) Max[axis] = raw;
            }

            private static Dictionary<StickAxis, int> NewExtremes(int seed)
                => new()
                {
                    [StickAxis.LeftX] = seed,
                    [StickAxis.LeftY] = seed,
                    [StickAxis.RightX] = seed,
                    [StickAxis.RightY] = seed,
                };
        }

        private sealed class CalibrationFile
        {
            public int Version { get; set; } = FileVersion;

            public Dictionary<string, DeviceProfileFile> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class DeviceProfileFile
        {
            public AxisProfileFile LeftX { get; set; } = new();

            public AxisProfileFile LeftY { get; set; } = new();

            public AxisProfileFile RightX { get; set; } = new();

            public AxisProfileFile RightY { get; set; } = new();
        }

        private sealed class AxisProfileFile
        {
            public int Center { get; set; }

            public int Min { get; set; }

            public int Max { get; set; }

            public bool IsCalibrated { get; set; }
        }
    }
}