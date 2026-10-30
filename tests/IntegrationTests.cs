using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenithmLan.Tests
{
    internal static class IntegrationTests
    {
        private static int failures;

        private static int Main()
        {
            Run("command-line validation", TestOptions);
            Run("fragmented and coalesced protocol frames", TestFraming);
            Run("shared-memory boundaries and AIR state", TestSharedMemory);
            Run("existing 150-byte DLL map", TestExistingDllMap);
            Run("extended INP flag boundaries", TestExtendedInputFlags);
            Run("TCP input, LED, reconnect, and cancellation", TestNetworkBridge);
            Run("invalid greeting, unknown command, and stalled frame recovery", TestNetworkRecovery);
            Run("no-LED mode and an idle held input", TestNoLedMode);
            Run("DNS-SD discovery packets and cache", DiscoveryTests.RunAll);
            Run("non-blocking console logging", ConsoleLoggingTests.RunAll);
            Run("quiet mode and handshake-only success notification", QuietModeTests.RunAll);
            Console.WriteLine(failures == 0 ? "All tests passed." : failures + " test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + error);
            }
        }

        private static void TestOptions()
        {
            Assert(String.IsNullOrEmpty(BridgeOptions.Parse(new string[0]).Host), "no arguments select discovery");
            BridgeOptions.Parse(new string[] { "--discover-timeout-ms", "1200", "--device", "Living Room iPad" });
            Throws<ArgumentException>(delegate { BridgeOptions.Parse(new string[] { "--ip", "" }); });
            Throws<ArgumentException>(delegate { BridgeOptions.Parse(new string[] { "--ip", "invalid" }); });
            Throws<ArgumentException>(delegate { BridgeOptions.Parse(new string[] { "--discover-timeout-ms", "499" }); });
            Throws<ArgumentException>(delegate { BridgeOptions.Parse(new string[] { "--discover-timeout-ms", "60001" }); });
            BridgeOptions defaults = BridgeOptions.Parse(new string[] { "--ip", "192.168.1.42" });
            Equal("192.168.1.42", defaults.Host, "IP address");
            Equal(24864, defaults.Port, "default port");
            BridgeOptions custom = BridgeOptions.Parse(new string[] {
                "--ip", "127.0.0.1", "--port", "12345", "--no-led"
            });
            Equal(12345, custom.Port, "custom port");
            Assert(custom.DisableLed, "--no-led");
            Assert(BridgeOptions.Parse(new string[] { "--help" }).ShowHelp, "--help");
            Throws<ArgumentException>(delegate { BridgeOptions.Parse(new string[] { "--ip" }); });
            Throws<ArgumentException>(delegate {
                BridgeOptions.Parse(new string[] { "--ip", "127.0.0.1", "--port", "0" });
            });
            Throws<ArgumentException>(delegate {
                BridgeOptions.Parse(new string[] { "--ip", "127.0.0.1", "--unknown" });
            });
        }

        private static void TestFraming()
        {
            byte[] welcome = new byte[] { (byte)'W', (byte)'E', (byte)'L' };
            byte[] input = Input(1, 0xA5);
            byte[] air = new byte[] { (byte)'A', (byte)'I', (byte)'R', 0 };
            byte[] bytes = Join(Frame(welcome), Frame(input), Frame(air));
            using (Stream fragmented = new FragmentingStream(new MemoryStream(bytes), 1))
            {
                Assert(Protocol.IsWelcome(Protocol.ReadFrame(fragmented)), "fragmented WEL");
                Sequence(input, Protocol.ReadFrame(fragmented), "fragmented INP");
                Sequence(air, Protocol.ReadFrame(fragmented), "coalesced AIR");
            }
            using (Stream truncated = new MemoryStream(new byte[] { 41, (byte)'I', (byte)'N', (byte)'P', 1 }))
            {
                Throws<EndOfStreamException>(delegate { Protocol.ReadFrame(truncated); });
            }
            Assert(!Protocol.IsWelcome(new byte[] { (byte)'W', (byte)'E' }), "short welcome rejected");
            Assert(!Protocol.IsWelcome(new byte[] { (byte)'B', (byte)'A', (byte)'D' }), "wrong welcome rejected");
        }

        private static void TestSharedMemory()
        {
            using (MemoryFixture memory = new MemoryFixture())
            {
                memory.View.Write(38, (byte)0xE1);
                memory.View.Write(138, (byte)0xD1);
                memory.View.Write(149, (byte)0xD2);
                bool airEnabled = true;
                Protocol.ApplyFrame(Input(1, 0x82), memory.State, ref airEnabled);
                Equal((byte)1, memory.View.ReadByte(0), "AIR offset");
                Equal((byte)0x82, memory.View.ReadByte(6), "slider start");
                Equal((byte)0x82, memory.View.ReadByte(37), "slider end");
                Equal((byte)0xE1, memory.View.ReadByte(38), "input preserves LED");

                Protocol.ApplyFrame(new byte[] { (byte)'A', (byte)'I', (byte)'R', 0 }, memory.State, ref airEnabled);
                Assert(!airEnabled, "AIR disabled");
                CheckZeros(memory.View, 0, 6, "disabling AIR clears held sensors");
                Protocol.ApplyFrame(Input(1, 0x83), memory.State, ref airEnabled);
                CheckZeros(memory.View, 0, 6, "disabled AIR ignores new sensor values");
                Equal((byte)0x83, memory.View.ReadByte(6), "slider works with AIR disabled");
                Protocol.ApplyFrame(new byte[] { (byte)'A', (byte)'I', (byte)'R', 1 }, memory.State, ref airEnabled);
                Protocol.ApplyFrame(Input(1, 0x84), memory.State, ref airEnabled);
                Equal((byte)1, memory.View.ReadByte(5), "AIR re-enabled");

                memory.View.Write(134, (byte)0xA1);
                memory.View.Write(135, (byte)0xA2);
                memory.View.Write(136, (byte)0);
                memory.View.Write(137, (byte)0xA3);
                Protocol.ApplyFrame(new byte[] { (byte)'F', (byte)'N', (byte)'C', 1 }, memory.State, ref airEnabled);
                Equal((byte)1, memory.View.ReadByte(136), "coin offset");
                Equal((byte)0xA3, memory.View.ReadByte(137), "coin does not overwrite card");
                Protocol.ApplyFrame(new byte[] { (byte)'F', (byte)'N', (byte)'C', 2 }, memory.State, ref airEnabled);
                Equal((byte)1, memory.View.ReadByte(137), "card offset");
                Equal((byte)0xA1, memory.View.ReadByte(134), "function preserves test");
                Equal((byte)0xA2, memory.View.ReadByte(135), "function preserves service");
                Equal((byte)0xD1, memory.View.ReadByte(138), "card does not overwrite remote fields");

                Throws<InvalidDataException>(delegate {
                    Protocol.ApplyFrame(new byte[] { (byte)'B', (byte)'A', (byte)'D' }, memory.State, ref airEnabled);
                });
                Throws<InvalidDataException>(delegate {
                    Protocol.ApplyFrame(new byte[] { (byte)'A', (byte)'I', (byte)'R', 2 }, memory.State, ref airEnabled);
                });
                Throws<InvalidDataException>(delegate {
                    Protocol.ApplyFrame(new byte[] { (byte)'F', (byte)'N', (byte)'C', 3 }, memory.State, ref airEnabled);
                });

                memory.State.ClearInputs();
                CheckZeros(memory.View, 0, 38, "disconnect clears AIR and slider");
                CheckZeros(memory.View, 134, 4, "disconnect clears function flags");
                Equal((byte)0xE1, memory.View.ReadByte(38), "clear preserves LED");
                Equal((byte)0xD1, memory.View.ReadByte(138), "clear preserves remote card fields");
                Equal((byte)0xD2, memory.View.ReadByte(149), "clear stops before remote field end");
            }
        }

        private static void TestNetworkBridge()
        {
            using (BridgeFixture bridge = new BridgeFixture(false))
            {
                byte[] leds = new byte[96];
                for (int i = 0; i < leds.Length; i++) leds[i] = (byte)(i + 1);
                bridge.Memory.View.WriteArray(38, leds, 0, leds.Length);

                using (TcpClient device = bridge.Accept())
                {
                    NetworkStream stream = device.GetStream();
                    SendFragmented(stream, Frame(new byte[] { (byte)'W', (byte)'E', (byte)'L' }));
                    SendFragmented(stream, Frame(Input(1, 0xB1)));
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0xB1; }, "fragmented TCP input");
                    Equal((byte)1, bridge.Memory.View.ReadByte(5), "TCP AIR input");

                    byte[] ledPayload = Protocol.ReadFrame(stream);
                    Equal(99, ledPayload.Length, "LED payload length (100 including prefix)");
                    Equal((byte)'L', ledPayload[0], "LED header L");
                    Equal((byte)'E', ledPayload[1], "LED header E");
                    Equal((byte)'D', ledPayload[2], "LED header D");
                    byte[] receivedLeds = new byte[96];
                    Buffer.BlockCopy(ledPayload, 3, receivedLeds, 0, 96);
                    Sequence(leds, receivedLeds, "LED RGB bytes");

                    byte[] combined = Join(
                        Frame(new byte[] { (byte)'A', (byte)'I', (byte)'R', 0 }),
                        Frame(Input(1, 0xB2)),
                        Frame(new byte[] { (byte)'F', (byte)'N', (byte)'C', 1 }),
                        Frame(new byte[] { (byte)'F', (byte)'N', (byte)'C', 2 }));
                    stream.Write(combined, 0, combined.Length);
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(137) == 1; }, "coalesced packets");
                    CheckZeros(bridge.Memory.View, 0, 6, "TCP AIR disable");
                    Equal((byte)0xB2, bridge.Memory.View.ReadByte(6), "coalesced slider input");
                    Equal((byte)1, bridge.Memory.View.ReadByte(136), "coalesced coin");
                }

                WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0; }, "disconnect input release");
                CheckZeros(bridge.Memory.View, 134, 4, "disconnect function release");
                using (TcpClient reconnected = bridge.Accept())
                {
                    NetworkStream stream = reconnected.GetStream();
                    byte[] combined = Join(
                        Frame(new byte[] { (byte)'W', (byte)'E', (byte)'L' }),
                        Frame(Input(1, 0xB3)));
                    stream.Write(combined, 0, combined.Length);
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0xB3; }, "reconnect input");
                    bridge.Stop();
                    CheckZeros(bridge.Memory.View, 0, 38, "cancellation releases input");
                    Equal(leds[0], bridge.Memory.View.ReadByte(38), "disconnect preserves game LED state");
                }
            }
        }

        private static void TestExistingDllMap()
        {
            string name = "Local\\BROKENITHM_LAN_TEST_SMALL_" + Guid.NewGuid().ToString("N");
            using (MemoryMappedFile dllMap = MemoryMappedFile.CreateNew(name, 150, MemoryMappedFileAccess.ReadWrite))
            using (MemoryMappedViewAccessor dllView = dllMap.CreateViewAccessor(0, 150, MemoryMappedFileAccess.ReadWrite))
            {
                dllView.Write(38, (byte)0xE2);
                dllView.Write(149, (byte)0xD4);
                using (SharedMemoryState state = new SharedMemoryState(name))
                {
                    bool airEnabled = true;
                    Protocol.ApplyFrame(Input(1, 0x91), state, ref airEnabled);
                    Equal((byte)1, dllView.ReadByte(0), "AIR visible to existing DLL mapping");
                    Equal((byte)0x91, dllView.ReadByte(37), "slider visible to existing DLL mapping");
                    Equal((byte)0xE2, state.ReadLeds()[0], "existing DLL LED visible to bridge");
                }
                CheckZeros(dllView, 0, 38, "dispose releases input on existing DLL mapping");
                Equal((byte)0xE2, dllView.ReadByte(38), "dispose preserves existing DLL LED");
                Equal((byte)0xD4, dllView.ReadByte(149), "dispose preserves existing remote card data");
            }
        }

        private static void TestExtendedInputFlags()
        {
            using (MemoryFixture memory = new MemoryFixture())
            {
                bool airEnabled = true;
                memory.View.Write(38, (byte)0xE3);
                memory.View.Write(138, (byte)0xD5);
                memory.View.Write(149, (byte)0xD6);
                for (int length = 41; length <= 45; length++)
                {
                    byte[] payload = new byte[length];
                    Buffer.BlockCopy(Input(1, 0x92), 0, payload, 0, 41);
                    for (int flag = 0; flag < 4; flag++) memory.View.Write(134 + flag, (byte)(0x71 + flag));
                    for (int position = 41; position < length; position++) payload[position] = (byte)(0x11 + position - 41);
                    Protocol.ApplyFrame(payload, memory.State, ref airEnabled);
                    for (int flag = 0; flag < 4; flag++)
                    {
                        byte expected = flag < length - 41 ? (byte)(0x11 + flag) : (byte)(0x71 + flag);
                        Equal(expected, memory.View.ReadByte(134 + flag), "INP length " + length + " flag " + flag);
                    }
                    Equal((byte)0xE3, memory.View.ReadByte(38), "extended INP preserves LED");
                    Equal((byte)0xD5, memory.View.ReadByte(138), "extended INP stops before remote fields");
                    Equal((byte)0xD6, memory.View.ReadByte(149), "extended INP preserves remote card ID");
                }
                foreach (int length in new int[] { 40, 46 })
                {
                    byte[] malformed = new byte[length];
                    malformed[0] = (byte)'I';
                    malformed[1] = (byte)'N';
                    malformed[2] = (byte)'P';
                    Throws<InvalidDataException>(delegate { Protocol.ApplyFrame(malformed, memory.State, ref airEnabled); });
                    Equal((byte)0xD5, memory.View.ReadByte(138), "oversized INP cannot overwrite remote fields");
                }
            }
        }

        private static void TestNetworkRecovery()
        {
            using (BridgeFixture bridge = new BridgeFixture(true))
            {
                using (TcpClient invalidGreeting = bridge.Accept())
                {
                    bridge.Memory.View.Write(6, (byte)0xD1);
                    NetworkStream stream = invalidGreeting.GetStream();
                    byte[] bad = Frame(new byte[] { (byte)'B', (byte)'A', (byte)'D' });
                    stream.Write(bad, 0, bad.Length);
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0; }, "wrong WEL releases input");
                }
                using (TcpClient unknownCommand = bridge.Accept())
                {
                    NetworkStream stream = unknownCommand.GetStream();
                    byte[] valid = Join(Frame(new byte[] { (byte)'W', (byte)'E', (byte)'L' }), Frame(Input(1, 0xD2)));
                    stream.Write(valid, 0, valid.Length);
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0xD2; }, "reconnected after wrong WEL");
                    byte[] bad = Frame(new byte[] { (byte)'B', (byte)'A', (byte)'D' });
                    stream.Write(bad, 0, bad.Length);
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0; }, "unknown command releases input");
                }
                using (TcpClient stalled = bridge.Accept())
                {
                    NetworkStream stream = stalled.GetStream();
                    byte[] valid = Join(Frame(new byte[] { (byte)'W', (byte)'E', (byte)'L' }), Frame(Input(1, 0xD3)));
                    stream.Write(valid, 0, valid.Length);
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0xD3; }, "reconnected after unknown command");
                    byte[] partial = new byte[] { 41, (byte)'I', (byte)'N', (byte)'P', 1 };
                    stream.Write(partial, 0, partial.Length);
                    WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0; }, "stalled partial payload times out and releases input");
                    using (TcpClient recovered = bridge.Accept())
                    {
                        NetworkStream recoveredStream = recovered.GetStream();
                        byte[] recovery = Join(Frame(new byte[] { (byte)'W', (byte)'E', (byte)'L' }), Frame(Input(1, 0xD4)));
                        recoveredStream.Write(recovery, 0, recovery.Length);
                        WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0xD4; }, "reconnected after a stalled frame");
                    }
                }
            }
        }

        private static void TestNoLedMode()
        {
            using (BridgeFixture bridge = new BridgeFixture(true))
            using (TcpClient device = bridge.Accept())
            {
                NetworkStream stream = device.GetStream();
                byte[] combined = Join(
                    Frame(new byte[] { (byte)'W', (byte)'E', (byte)'L' }),
                    Frame(Input(1, 0xC1)));
                stream.Write(combined, 0, combined.Length);
                WaitUntil(delegate { return bridge.Memory.View.ReadByte(6) == 0xC1; }, "no-LED input");
                Thread.Sleep(1700);
                Equal((byte)0xC1, bridge.Memory.View.ReadByte(6), "idle held slider remains pressed");
                Equal((byte)1, bridge.Memory.View.ReadByte(0), "idle held AIR remains pressed");
                Equal(0, device.Available, "--no-led sends no application bytes");
                bridge.Stop();
                CheckZeros(bridge.Memory.View, 0, 38, "no-LED cancellation releases input");
            }
        }

        private static byte[] Input(byte air, byte slider)
        {
            byte[] payload = new byte[41];
            payload[0] = (byte)'I';
            payload[1] = (byte)'N';
            payload[2] = (byte)'P';
            for (int i = 0; i < 6; i++) payload[3 + i] = air;
            for (int i = 0; i < 32; i++) payload[9 + i] = slider;
            return payload;
        }

        private static byte[] Frame(byte[] payload)
        {
            byte[] bytes = new byte[payload.Length + 1];
            bytes[0] = checked((byte)payload.Length);
            Buffer.BlockCopy(payload, 0, bytes, 1, payload.Length);
            return bytes;
        }

        private static byte[] Join(params byte[][] chunks)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                foreach (byte[] chunk in chunks) stream.Write(chunk, 0, chunk.Length);
                return stream.ToArray();
            }
        }

        private static void SendFragmented(Stream stream, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                stream.Write(bytes, i, 1);
                if (i < 5) Thread.Sleep(5);
            }
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

        private static void CheckZeros(MemoryMappedViewAccessor view, int offset, int count, string message)
        {
            for (int i = 0; i < count; i++) Equal((byte)0, view.ReadByte(offset + i), message + " @" + (offset + i));
        }

        private static void Sequence(byte[] expected, byte[] actual, string message)
        {
            Assert(actual != null, message + ": non-null");
            Equal(expected.Length, actual.Length, message + ": length");
            for (int i = 0; i < expected.Length; i++) Equal(expected[i], actual[i], message + " @" + i);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(message + ": expected " + expected + ", got " + actual);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }

        private sealed class MemoryFixture : IDisposable
        {
            public readonly SharedMemoryState State;
            public readonly MemoryMappedFile Map;
            public readonly MemoryMappedViewAccessor View;

            public MemoryFixture()
            {
                string name = "Local\\BROKENITHM_LAN_TEST_" + Guid.NewGuid().ToString("N");
                State = new SharedMemoryState(name);
                Map = MemoryMappedFile.OpenExisting(name);
                View = Map.CreateViewAccessor(0, 150, MemoryMappedFileAccess.ReadWrite);
            }

            public void Dispose()
            {
                View.Dispose();
                Map.Dispose();
                State.Dispose();
            }
        }

        private sealed class BridgeFixture : IDisposable
        {
            public readonly MemoryFixture Memory = new MemoryFixture();
            private readonly TcpListener listener;
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private readonly Task worker;
            private bool stopped;

            public BridgeFixture(bool disableLed)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                BridgeOptions options = new BridgeOptions();
                options.Host = "127.0.0.1";
                options.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                options.ConnectTimeoutMs = 1000;
                options.HandshakeTimeoutMs = 2000;
                options.RetryDelayMs = 50;
                options.DisableLed = disableLed;
                LanBridge bridge = new LanBridge(options, Memory.State, delegate(string message) { });
                worker = Task.Factory.StartNew(delegate {
                    try { bridge.Run(cancellation.Token); }
                    catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) throw; }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            public TcpClient Accept()
            {
                IAsyncResult pending = listener.BeginAcceptTcpClient(null, null);
                using (WaitHandle ready = pending.AsyncWaitHandle)
                {
                    Assert(ready.WaitOne(5000), "bridge connection accepted within timeout");
                    TcpClient client = listener.EndAcceptTcpClient(pending);
                    client.NoDelay = true;
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;
                    return client;
                }
            }

            public void Stop()
            {
                if (stopped) return;
                cancellation.Cancel();
                Assert(worker.Wait(3000), "bridge cancels a blocked read promptly");
                stopped = true;
            }

            public void Dispose()
            {
                try { Stop(); }
                finally
                {
                    listener.Stop();
                    if (worker.IsCompleted)
                    {
                        worker.Dispose();
                        cancellation.Dispose();
                        Memory.Dispose();
                    }
                }
            }
        }

        private sealed class FragmentingStream : Stream
        {
            private readonly Stream inner;
            private readonly int maximumRead;

            public FragmentingStream(Stream inner, int maximumRead)
            {
                this.inner = inner;
                this.maximumRead = maximumRead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return inner.Read(buffer, offset, Math.Min(count, maximumRead));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
