using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenithmLan.Tests
{
    internal static class QuietModeTests
    {
        public static void RunAll()
        {
            DiagnosticsAreOptIn();
            SuccessfulHandshakeRaisesExactlyOneNotification();
        }

        private static void DiagnosticsAreOptIn()
        {
            BridgeOptions defaults = BridgeOptions.Parse(new string[0]);
            Assert(!defaults.Diagnostics, "default mode must not enable diagnostic logging");
            Assert(defaults.LogDirectory == null, "default mode has no requested log directory");
            Assert(!BridgeOptions.Parse(new string[] { "--no-led" }).Diagnostics,
                "disabling LEDs must preserve quiet mode");
            Assert(!BridgeOptions.Parse(new string[] { "--ip", "127.0.0.1" }).Diagnostics,
                "manual IP mode must preserve quiet mode");
            Assert(BridgeOptions.Parse(new string[] { "--diagnostics" }).Diagnostics,
                "explicit diagnostics flag enables tracing");
            string path = Path.Combine(Path.GetTempPath(), "BrokenithmRequestedLogs");
            BridgeOptions logDirectory = BridgeOptions.Parse(new string[] { "--log-dir", path });
            Assert(logDirectory.Diagnostics, "requesting a log directory explicitly enables diagnostics");
            Equal(path, logDirectory.LogDirectory, "requested diagnostic directory");
        }

        private static void SuccessfulHandshakeRaisesExactlyOneNotification()
        {
            using (SharedMemoryState state = new SharedMemoryState("Local\\BROKENITHM_QUIET_TEST_" + Guid.NewGuid().ToString("N")))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                TcpListener unavailable = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                unavailable.Start();
                int unavailablePort = ((IPEndPoint)unavailable.LocalEndpoint).Port;
                unavailable.Stop();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                List<DiscoveredDevice> candidates = new List<DiscoveredDevice>();
                candidates.Add(new DiscoveredDevice {
                    Name = "A unavailable iPad", InstanceName = "A unavailable iPad._brokenithm._tcp.local",
                    Address = IPAddress.Loopback, Port = unavailablePort, InterfaceIndex = 1
                });
                DiscoveredDevice expected = new DiscoveredDevice {
                    Name = "B quiet iPad", InstanceName = "B quiet iPad._brokenithm._tcp.local",
                    Address = IPAddress.Loopback, Port = port, InterfaceIndex = 1
                };
                candidates.Add(expected);
                List<DiscoveredDevice> notifications = new List<DiscoveredDevice>();
                BridgeOptions options = BridgeOptions.Parse(new string[] { "--no-led" });
                options.ConnectTimeoutMs = 500;
                options.HandshakeTimeoutMs = 1000;
                options.RetryDelayMs = 50;
                LanBridge bridge = new LanBridge(options, state, delegate(string message) { },
                    new FakeDiscovery(candidates), delegate(DiscoveredDevice device) {
                        lock (notifications) notifications.Add(device);
                    });
                Task worker = Task.Factory.StartNew(delegate { bridge.Run(cancellation.Token); },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                try
                {
                    using (TcpClient device = Accept(listener))
                    {
                        Equal(0, Count(notifications), "TCP connect or earlier connection refusal must not announce success");
                        Send(device, new byte[] { 3, (byte)'B', (byte)'A', (byte)'D' });
                    }
                    using (TcpClient device = Accept(listener))
                    {
                        Equal(0, Count(notifications), "invalid WEL greeting must not announce success");
                        Send(device, new byte[] { 3, (byte)'W', (byte)'E', (byte)'L' });
                        WaitUntil(delegate { return Count(notifications) == 1; }, "first successful handshake notification");
                        Thread.Sleep(100);
                        Equal(1, Count(notifications), "one connection produces one notification");
                        AssertNotification(notifications, 0, expected);
                    }
                    using (TcpClient device = Accept(listener))
                    {
                        Equal(1, Count(notifications), "reconnect must wait for a valid greeting");
                        Send(device, new byte[] { 3, (byte)'W', (byte)'E', (byte)'L' });
                        WaitUntil(delegate { return Count(notifications) == 2; }, "reconnected handshake notification");
                        AssertNotification(notifications, 1, expected);
                        cancellation.Cancel();
                        Assert(worker.Wait(3000), "quiet bridge cancellation");
                        Equal(2, Count(notifications), "shutdown must not announce an extra connection");
                    }
                }
                finally
                {
                    cancellation.Cancel();
                    listener.Stop();
                    Assert(worker.Wait(3000), "quiet test worker stopped");
                    worker.Dispose();
                }
            }
        }

        private static void AssertNotification(List<DiscoveredDevice> notifications, int index, DiscoveredDevice expected)
        {
            lock (notifications)
            {
                DiscoveredDevice actual = notifications[index];
                Equal(expected.Name, actual.Name, "success device name");
                Equal(expected.InstanceName, actual.InstanceName, "success service instance");
                Equal(expected.Address, actual.Address, "success IP address");
                Equal(expected.Port, actual.Port, "success service port");
            }
        }

        private static int Count(List<DiscoveredDevice> notifications)
        {
            lock (notifications) return notifications.Count;
        }

        private static TcpClient Accept(TcpListener listener)
        {
            IAsyncResult operation = listener.BeginAcceptTcpClient(null, null);
            using (WaitHandle ready = operation.AsyncWaitHandle)
            {
                Assert(ready.WaitOne(5000), "quiet bridge connects within timeout");
                return listener.EndAcceptTcpClient(operation);
            }
        }

        private static void Send(TcpClient client, byte[] bytes)
        {
            client.GetStream().Write(bytes, 0, bytes.Length);
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.ElapsedMilliseconds >= 5000) throw new Exception("Timed out: " + message);
                Thread.Sleep(10);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(message + ": expected " + expected + ", got " + actual);
        }

        private sealed class FakeDiscovery : IDeviceDiscovery
        {
            private readonly IList<DiscoveredDevice> devices;

            public FakeDiscovery(IList<DiscoveredDevice> devices)
            {
                this.devices = devices;
            }

            public IList<DiscoveredDevice> Find(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return devices;
            }
        }
    }
}
