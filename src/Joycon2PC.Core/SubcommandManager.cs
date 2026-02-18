using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Joycon2PC.Core
{
    /// <summary>
    /// Manages reliable subcommand sending: framing with report id and sequence,
    /// rate-limiting, retries and waiting for acknowledgements via incoming reports.
    ///
    /// Usage: construct with a send delegate that writes a byte[] to the controller
    /// (e.g. a GATT write). Then call SendSubcommandAsync(sub, payload).
    /// Call ProcessIncomingReport when raw reports are received so the manager
    /// can match ACKs by sequence number.
    ///
    /// Note: Exact packet framing and where sequence / subcommand appear vary by
    /// transport and firmware. This implementation uses a configurable report id
    /// prefix and places the sequence byte at index 1. Adjust BuildPacket/Parse
    /// if your device expects a different layout.
    /// </summary>
    public class SubcommandManager
    {
        private readonly Func<byte[], CancellationToken, Task<bool>> _sendFunc;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _pending = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private int _sequence = 0;
        private DateTime _lastSend = DateTime.MinValue;

        public byte ReportId { get; set; } = 0x01; // default report id prefix
        public int MinDelayMs { get; set; } = 50; // minimum delay between sends
        public int AckTimeoutMs { get; set; } = 600; // wait time for ACK
        public int MaxRetries { get; set; } = 3;

        public SubcommandManager(Func<byte[], CancellationToken, Task<bool>> sendFunc)
        {
            _sendFunc = sendFunc ?? throw new ArgumentNullException(nameof(sendFunc));
        }

        /// <summary>
        /// Send a subcommand reliably: builds packet using report id + seq + [sub + data],
        /// writes it via the provided send delegate, and waits for a matching incoming
        /// report containing the sequence number. Returns the raw response bytes on success,
        /// or null on timeout/failure.
        /// </summary>
        public async Task<byte[]?> SendSubcommandAsync(byte subcommand, byte[] data, CancellationToken ct = default)
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // enforce min delay
                var elapsed = (DateTime.UtcNow - _lastSend).TotalMilliseconds;
                if (elapsed < MinDelayMs)
                {
                    await Task.Delay(MinDelayMs - (int)elapsed, ct).ConfigureAwait(false);
                }

                int attempt = 0;
                while (attempt < MaxRetries)
                {
                    attempt++;

                    int seq = NextSequence();
                    var packet = BuildPacket(seq, subcommand, data);

                    var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending[seq] = tcs;

                    bool writeOk = false;
                    try
                    {
                        writeOk = await _sendFunc(packet, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Subcommand write exception: {ex.Message}");
                        _pending.TryRemove(seq, out _);
                        if (attempt >= MaxRetries) return null;
                        await Task.Delay(50, ct).ConfigureAwait(false);
                        continue;
                    }

                    _lastSend = DateTime.UtcNow;

                    if (!writeOk)
                    {
                        _pending.TryRemove(seq, out _);
                        if (attempt >= MaxRetries) return null;
                        await Task.Delay(50, ct).ConfigureAwait(false);
                        continue;
                    }

                    // wait for ACK
                    var delayTask = Task.Delay(AckTimeoutMs, ct);
                    var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
                    if (completed == tcs.Task)
                    {
                        var resp = await tcs.Task.ConfigureAwait(false);
                        _pending.TryRemove(seq, out _);
                        return resp;
                    }
                    else
                    {
                        // timeout, retry
                        _pending.TryRemove(seq, out _);
                        Console.WriteLine($"Subcommand seq={seq} timeout, retry {attempt}/{MaxRetries}");
                        await Task.Delay(50, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                return null;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Build a packet: [reportId, seq, subcommand, ...data]
        /// Adjust this if your transport requires different framing.
        /// </summary>
        private byte[] BuildPacket(int seq, byte subcommand, byte[] data)
        {
            var body = SubcommandBuilder.BuildSubcommand(subcommand, data ?? Array.Empty<byte>());
            var outp = new byte[1 + 1 + body.Length];
            outp[0] = ReportId;
            outp[1] = (byte)seq;
            Buffer.BlockCopy(body, 0, outp, 2, body.Length);
            return outp;
        }

        private int NextSequence()
        {
            // 0-255 wrapping
            int s = Interlocked.Increment(ref _sequence) & 0xFF;
            return s;
        }

        /// <summary>
        /// Process an incoming raw report (from BLE notifications). If the report
        /// contains a sequence number matching a pending subcommand, the waiting
        /// SendSubcommandAsync will be completed with the raw report bytes.
        /// This method uses a heuristic: if report length>1, it treats report[1]
        /// as the sequence byte. Adjust if your device uses a different layout.
        /// </summary>
        public void ProcessIncomingReport(byte[] report)
        {
            if (report == null || report.Length < 2) return;

            int seq = report[1];
            if (_pending.TryGetValue(seq, out var tcs))
            {
                tcs.TrySetResult(report);
            }
            else
            {
                // not a pending subcommand ACK; ignore or handle elsewhere
            }
        }
    }
}
