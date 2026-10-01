using System;
using System.Windows.Forms;

namespace Joycon2PC.App
{
    internal class Program
    {
        [STAThread]
        private static void Main()
        {
            // ApplicationConfiguration is generated from the MSBuild properties in
            // Joycon2PC.App.csproj: PerMonitorV2 DPI awareness, visual styles, GDI text
            // rendering and the Segoe UI 9pt default font. app.manifest declares the same
            // DPI awareness at the process level, which is what actually takes effect
            // before the first window exists.
            try
            {
                ApplicationConfiguration.Initialize();
            }
            catch
            {
                // Extremely defensive: if the generated helper is ever unavailable, fall back
                // to the equivalent runtime calls. Must not run twice - SetCompatibleTextRenderingDefault throws.
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
            }

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
