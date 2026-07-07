using System.Windows.Forms;

namespace DynoDynamicsCanFeeder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Headless mode (unattended / scripted):  DynoCanFeeder.exe --headless <config.json>
        if (args.Length >= 1 && args[0].Equals("--headless", StringComparison.OrdinalIgnoreCase))
            return HeadlessRun(args.Length >= 2 ? args[1] : "feeder.config.json");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Jump straight to the ECU database editor:  DynoDynamicsCanFeeder.exe --dbeditor [dyno-e.db]
        if (args.Length >= 1 && args[0].Equals("--dbeditor", StringComparison.OrdinalIgnoreCase))
        {
            Application.Run(new EcuDbForm(new List<SignalRoute>(), args.Length >= 2 ? args[1] : null));
            return 0;
        }

        // Jump straight to the comms-hub emulator:  DynoDynamicsCanFeeder.exe --comms [comms.config.json]
        if (args.Length >= 1 && args[0].Equals("--comms", StringComparison.OrdinalIgnoreCase))
        {
            var cfg = args.Length >= 2 && File.Exists(args[1]) ? CommsEmulatorConfig.Load(args[1]) : null;
            Application.Run(new CommsEmulatorForm(cfg));
            return 0;
        }

        // Jump straight to the EDM / custom sensor channel editor:  DynoDynamicsCanFeeder.exe --customch [dyno-2.db]
        if (args.Length >= 1 && args[0].Equals("--customch", StringComparison.OrdinalIgnoreCase))
        {
            Application.Run(new CustomChannelForm(args.Length >= 2 ? args[1] : null));
            return 0;
        }

        Application.Run(new MainForm(args.Length >= 1 ? args[0] : null));
        return 0;
    }

    private static int HeadlessRun(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config not found: {configPath}");
            return 2;
        }
        try
        {
            var cfg = FeederConfig.Load(configPath);
            using var feeder = new Feeder(cfg);
            feeder.Status += s => Console.WriteLine(s);
            feeder.Start();
            Console.WriteLine("Feeder running. Press Ctrl+C to stop.");
            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            stop.Wait();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fatal: " + ex);
            return 1;
        }
    }
}
