using System;
using System.Threading.Tasks;
using Joycon2PC.App.Bluetooth;

namespace Joycon2PC.App
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            Console.WriteLine("Joycon2PC starting (scaffold).");
            Console.WriteLine("Scanning for Bluetooth LE devices (this may require Windows 10+).");

            var scanner = new BLEScanner();
            await scanner.ScanAsync();

            // Wire parser + ViGEm bridge for basic local testing
            var parser = new Joycon2PC.Core.JoyconParser();
            var bridge = new Joycon2PC.ViGEm.ViGEmBridge();
            bridge.Connect();

            parser.StateChanged += s =>
            {
                Console.WriteLine($"Parsed state: Buttons=0x{s.Buttons:X4} LX={s.LeftStickX} LY={s.LeftStickY}");
                bridge.UpdateFromState(s);
            };

            // Example: simulate a short raw report to exercise parser/bridge
            var exampleReport = new byte[] { 0x30, 0x05, 0x00, 128, 128, 128, 128 };
            parser.Parse(exampleReport);

            Console.WriteLine("Scan complete.");
        }
    }
}
