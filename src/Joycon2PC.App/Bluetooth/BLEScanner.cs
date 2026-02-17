using System;
using System.Threading.Tasks;

namespace Joycon2PC.App.Bluetooth
{
    public class BLEScanner
    {
        public BLEScanner()
        {
        }

        public Task ScanAsync()
        {
            Console.WriteLine("BLE scanner placeholder: Windows BLE APIs disabled in this build.");
            Console.WriteLine("When ready, install Windows SDK support and re-enable Bluetooth scanning code.");
            return Task.CompletedTask;
        }
    }
}
