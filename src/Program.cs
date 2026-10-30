using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BrokenithmLan
{
    internal static class Program
    {
        private const string BuildVersion = "2026.10.29-quiet1";
        private delegate bool ConsoleControlHandler(uint control);
        private static ConsoleControlHandler consoleHandler;

        [DllImport("Kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(ConsoleControlHandler handler, bool add);

        private static int Main(string[] args)
        {
            try
            {
                BridgeOptions options = BridgeOptions.Parse(args);
                if (options.ShowHelp)
                {
                    PrintUsage();
                    return 0;
                }
                using (Mutex instance = new Mutex(false, "Local\\BROKENITHM_LAN_BRIDGE"))
                {
                    bool acquired;
                    try { acquired = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("Another LAN bridge is already running in this Windows session.");
                    try
                    {
                        Run(options);
                    }
                    finally
                    {
                        instance.ReleaseMutex();
                    }
                }
                return 0;
            }
            catch (ArgumentException error)
            {
                Console.Error.WriteLine(error.Message);
                PrintUsage();
                return 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Fatal: " + error.Message);
                return 1;
            }
        }

        private static void Run(BridgeOptions options)
        {
            if (!options.Diagnostics)
            {
                using (ConnectionNotice notice = new ConnectionNotice(Console.Out))
                {
                    RunBridge(options, delegate(string message) { }, notice.Connected);
                }
                return;
            }
            using (AsyncConsoleLog trace = CreateTrace(options))
            {
                trace.Write("Brokenithm LAN bridge " + BuildVersion + " - SwiftUI / TCP 24864");
                trace.Write("Log file: " + (trace.FilePath ?? "unavailable; console only"));
                trace.Write("Environment: OS=" + Environment.OSVersion + "; CLR=" + Environment.Version +
                    "; process64=" + Environment.Is64BitProcess + "; PID=" + Process.GetCurrentProcess().Id);
                trace.Write("Options: mode=" + (options.Host == null ? "automatic" : "manual " + options.Host + ":" + options.Port) +
                    "; device=" + (options.DeviceName ?? "any") + "; LED=" + !options.DisableLed +
                    "; discoverMs=" + options.DiscoveryTimeoutMs + "; connectMs=" + options.ConnectTimeoutMs +
                    "; handshakeMs=" + options.HandshakeTimeoutMs + "; retryMs=" + options.RetryDelayMs);
                trace.Write("Keep the iPad app open. Run only one USB or LAN bridge at a time.");
                if (String.IsNullOrEmpty(options.Host)) trace.Write("Automatic Bonjour/mDNS discovery enabled. Allow UDP 5353 on your private network if prompted.");
                if (options.DisableLed) trace.Write("LED feedback is disabled.");
                try { RunBridge(options, trace.Write, null); }
                catch (Exception error)
                {
                    trace.Write("Fatal exception: " + error);
                    throw;
                }
                finally { trace.Write("Bridge stopped; inputs released."); }
            }
        }

        private static AsyncConsoleLog CreateTrace(BridgeOptions options)
        {
            string fileName = "brokenithm-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Process.GetCurrentProcess().Id + ".log";
            string requested = options.LogDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            try
            {
                Directory.CreateDirectory(requested);
                return new AsyncConsoleLog(Console.Out, Path.Combine(requested, fileName));
            }
            catch (Exception firstError)
            {
                if (!IsFileError(firstError)) throw;
                string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brokenithm-LAN", "logs");
                try
                {
                    Directory.CreateDirectory(fallback);
                    AsyncConsoleLog trace = new AsyncConsoleLog(Console.Out, Path.Combine(fallback, fileName));
                    trace.Write("Requested log directory unavailable: " + firstError.Message + "; using " + fallback);
                    return trace;
                }
                catch (Exception secondError)
                {
                    if (!IsFileError(secondError)) throw;
                    AsyncConsoleLog trace = new AsyncConsoleLog(Console.Out);
                    trace.Write("File logging unavailable: " + secondError.Message);
                    return trace;
                }
            }
        }

        private static bool IsFileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is NotSupportedException || error is System.Security.SecurityException;
        }

        private static void RunBridge(BridgeOptions options, Action<string> log, Action<DiscoveredDevice> onConnected)
        {
            using (SharedMemoryState state = new SharedMemoryState(SharedMemoryState.DefaultName))
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancelHandler = delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    log("Console cancellation requested: " + e.SpecialKey);
                    stop.Cancel();
                };
                EventHandler exitHandler = delegate { state.ClearInputs(); };
                consoleHandler = delegate(uint control)
                {
                    if (control == 2 || control == 5 || control == 6)
                    {
                        log("Console control event: " + control);
                        stop.Cancel();
                        state.ClearInputs();
                    }
                    return false;
                };
                Console.CancelKeyPress += cancelHandler;
                AppDomain.CurrentDomain.ProcessExit += exitHandler;
                SetConsoleCtrlHandler(consoleHandler, true);
                try
                {
                    new LanBridge(options, state, log, new MdnsDiscovery(options.DiscoveryTimeoutMs, log), onConnected).Run(stop.Token);
                }
                finally
                {
                    state.ClearInputs();
                    Console.CancelKeyPress -= cancelHandler;
                    AppDomain.CurrentDomain.ProcessExit -= exitHandler;
                    SetConsoleCtrlHandler(consoleHandler, false);
                    GC.KeepAlive(consoleHandler);
                }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Brokenithm LAN bridge " + BuildVersion);
            Console.WriteLine("Usage: Brokenithm-LAN.exe [--device <name>] [--no-led]");
            Console.WriteLine("       Brokenithm-LAN.exe --ip <iPad IP> [--port 24864] [--no-led]");
            Console.WriteLine("       [--discover-timeout-ms 3000]");
            Console.WriteLine("       [--diagnostics] [--log-dir <directory>]");
            Console.WriteLine("       [--retry-ms 1000] [--connect-timeout-ms 3000] [--handshake-timeout-ms 5000]");
            Console.WriteLine("       Brokenithm-LAN.exe --help");
            Console.WriteLine("Without --ip, discover _brokenithm._tcp.local automatically. Manual IP bypasses discovery.");
            Console.WriteLine("Default: no logs; show a message only after a successful WEL handshake. --diagnostics enables detailed logs.");
            Console.WriteLine("The manual IP belongs to the iPad. No Apple USB driver or Bonjour installation is required.");
        }
    }
}
