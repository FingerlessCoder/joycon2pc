using System.Windows.Forms;

namespace Joycon2PC.App
{
    internal class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var os = Environment.OSVersion.Version;
                MessageBox.Show(
                    "Detected Windows 10 (or older). Joycon2PC can run in compatibility mode, but BLE stability may be lower than Windows 11 22H2+.\r\n\r\n" +
                    $"Current OS version: {os.Major}.{os.Minor}.{os.Build}",
                    "Joycon2PC - Windows compatibility notice",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            Application.Run(new MainForm());
        }
    }
}
