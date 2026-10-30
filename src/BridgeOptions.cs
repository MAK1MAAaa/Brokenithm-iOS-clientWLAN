using System;
using System.Globalization;
using System.Net;

namespace BrokenithmLan
{
    internal sealed class BridgeOptions
    {
        public string Host;
        public int Port = 24864;
        public int ConnectTimeoutMs = 3000;
        public int HandshakeTimeoutMs = 5000;
        public int RetryDelayMs = 1000;
        public int DiscoveryTimeoutMs = 3000;
        public string DeviceName;
        public string LogDirectory;
        public bool Diagnostics;
        public bool DisableLed;
        public bool ShowHelp;

        public static BridgeOptions Parse(string[] args)
        {
            BridgeOptions options = new BridgeOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument == "--help" || argument == "-h")
                {
                    options.ShowHelp = true;
                }
                else if (argument == "--no-led")
                {
                    options.DisableLed = true;
                }
                else if (argument == "--diagnostics")
                {
                    options.Diagnostics = true;
                }
                else if (argument == "--ip")
                {
                    options.Host = NextValue(args, ref i);
                }
                else if (argument == "--port")
                {
                    options.Port = ParseNumber(NextValue(args, ref i), argument, 1, 65535);
                }
                else if (argument == "--device")
                {
                    options.DeviceName = NextValue(args, ref i);
                    if (String.IsNullOrWhiteSpace(options.DeviceName)) throw new ArgumentException("--device requires a device name.");
                }
                else if (argument == "--log-dir")
                {
                    options.LogDirectory = NextValue(args, ref i);
                    options.Diagnostics = true;
                    if (String.IsNullOrWhiteSpace(options.LogDirectory)) throw new ArgumentException("--log-dir requires a directory.");
                }
                else if (argument == "--discover-timeout-ms")
                {
                    options.DiscoveryTimeoutMs = ParseNumber(NextValue(args, ref i), argument, 500, 60000);
                }
                else if (argument == "--retry-ms")
                {
                    options.RetryDelayMs = ParseNumber(NextValue(args, ref i), argument, 100, 60000);
                }
                else if (argument == "--connect-timeout-ms")
                {
                    options.ConnectTimeoutMs = ParseNumber(NextValue(args, ref i), argument, 100, 60000);
                }
                else if (argument == "--handshake-timeout-ms")
                {
                    options.HandshakeTimeoutMs = ParseNumber(NextValue(args, ref i), argument, 100, 60000);
                }
                else
                {
                    throw new ArgumentException("Unknown option: " + argument);
                }
            }

            IPAddress address;
            if (!options.ShowHelp && options.Host != null && (String.IsNullOrWhiteSpace(options.Host) ||
                !IPAddress.TryParse(options.Host, out address)))
            {
                throw new ArgumentException("--ip must be the iPad's IPv4 or IPv6 address.");
            }
            if (!options.ShowHelp && options.Host != null && options.DeviceName != null)
            {
                throw new ArgumentException("Use --device with automatic discovery, or --ip for a manual address.");
            }
            return options;
        }

        private static string NextValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Missing value for " + args[index]);
            }
            return args[++index];
        }

        private static int ParseNumber(string value, string name, int minimum, int maximum)
        {
            int number;
            if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                number < minimum || number > maximum)
            {
                throw new ArgumentException(name + " must be between " + minimum + " and " + maximum + ".");
            }
            return number;
        }
    }
}
