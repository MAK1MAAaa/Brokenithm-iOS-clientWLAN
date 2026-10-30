using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenithmLan.Tests
{
    internal static class DiscoveryTests
    {
        private const string Service = "_brokenithm._tcp.local";
        private const string Instance = "Living Room iPad._brokenithm._tcp.local";
        private const string Host = "living-room-ipad.local";
        private static readonly DateTime Epoch = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

        public static void RunAll()
        {
            QueryEncoding();
            CompressedResponse();
            MultiPacketAndMissingNames();
            IPv6AndMultipleDevices();
            TtlExpiryAndGoodbye();
            InterfaceIsolation();
            IgnoreUnusableResponses();
            RejectMalformedAtomically();
            SimulatedDiscoveryTransport();
            AutomaticBridgeFallback();
        }

        private static void QueryEncoding()
        {
            byte[] query = DnsSdMessage.CreateQuery(Service, 12);
            Equal(0, UInt16(query, 0), "mDNS query ID");
            Equal(0, UInt16(query, 2), "query flags");
            Equal(1, UInt16(query, 4), "one query question");
            Equal(0, UInt16(query, 6), "no known-answer records");
            Equal(0, UInt16(query, 8), "no authority records");
            Equal(0, UInt16(query, 10), "no additional records");
            int position = 12;
            Equal(Service, ReadUncompressedName(query, ref position), "query name");
            Equal(12, UInt16(query, position), "PTR question");
            Equal(1, UInt16(query, position + 2), "multicast IN question without QU bit");
            Equal(position + 4, query.Length, "exact query length");
            byte[] unicode = DnsSdMessage.CreateQuery("客厅 iPad._brokenithm._tcp.local", 255);
            position = 12;
            Equal("客厅 iPad._brokenithm._tcp.local", ReadUncompressedName(unicode, ref position), "UTF-8 query labels");
            Equal(255, UInt16(unicode, position), "ANY follow-up question");
        }

        private static void CompressedResponse()
        {
            DnsPacket packet = new DnsPacket();
            AddPtr(packet, Instance, 120);
            AddSrv(packet, Instance, Host, 24864, 120);
            AddAddress(packet, Host, "192.168.1.42", 120);
            byte[] wire = packet.Build();
            bool containsPointer = false;
            for (int i = 12; i < wire.Length; i++) if ((wire[i] & 0xC0) == 0xC0) containsPointer = true;
            Assert(containsPointer, "fixture includes compressed DNS names");
            DnsSdCache cache = new DnsSdCache();
            cache.AddPacket(wire, 7, Epoch);
            IList<DiscoveredDevice> devices = cache.GetDevices(Epoch);
            Equal(1, devices.Count, "complete compressed PTR/SRV/A chain");
            Equal("Living Room iPad", devices[0].Name, "friendly instance name");
            Equal(Instance, devices[0].InstanceName, "full instance name");
            Equal("192.168.1.42", devices[0].Address.ToString(), "A record address");
            Equal(24864, devices[0].Port, "SRV advertised port");
            Equal(7, devices[0].InterfaceIndex, "interface attached to result");
            cache.AddPacket(wire, 7, Epoch.AddSeconds(1));
            Equal(1, cache.GetDevices(Epoch.AddSeconds(1)).Count, "repeated packet deduplicated");
        }

        private static void MultiPacketAndMissingNames()
        {
            DnsSdCache cache = new DnsSdCache();
            DnsPacket ptr = new DnsPacket();
            AddPtr(ptr, Instance, 120);
            cache.AddPacket(ptr.Build(), 8, Epoch);
            Contains(cache.GetMissingNames(Epoch), Instance, "PTR triggers SRV follow-up");

            DnsPacket srv = new DnsPacket();
            AddSrv(srv, Instance, Host, 25000, 120);
            cache.AddPacket(srv.Build(), 8, Epoch);
            Contains(cache.GetMissingNames(Epoch), Host, "SRV triggers address follow-up");

            DnsPacket address = new DnsPacket();
            AddAddress(address, Host, "192.168.1.43", 120);
            cache.AddPacket(address.Build(), 8, Epoch);
            Equal(1, cache.GetDevices(Epoch).Count, "records resolve across packets");
            Equal(25000, cache.GetDevices(Epoch)[0].Port, "discovery uses SRV port");
            Equal(0, cache.GetMissingNames(Epoch).Count, "complete chain needs no follow-up");

            DnsSdCache reverse = new DnsSdCache();
            reverse.AddPacket(address.Build(), 8, Epoch);
            reverse.AddPacket(srv.Build(), 8, Epoch);
            reverse.AddPacket(ptr.Build(), 8, Epoch);
            Equal(1, reverse.GetDevices(Epoch).Count, "out-of-order responses retained");
        }

        private static void IPv6AndMultipleDevices()
        {
            const string second = "客厅 iPad._brokenithm._tcp.local";
            DnsPacket packet = new DnsPacket();
            AddPtr(packet, Instance, 120);
            AddSrv(packet, Instance, Host, 24864, 120);
            AddAddress(packet, Host, "fe80::1234", 120);
            AddAddress(packet, Host, "192.168.1.44", 120);
            AddPtr(packet, second, 120);
            AddSrv(packet, second, "another-ipad.local", 24865, 120);
            AddAddress(packet, "another-ipad.local", "192.168.1.45", 120);
            DnsSdCache cache = new DnsSdCache();
            cache.AddPacket(packet.Build(), 17, Epoch);
            IList<DiscoveredDevice> devices = cache.GetDevices(Epoch);
            Equal(3, devices.Count, "multiple instances and address families");
            bool foundScoped = false;
            bool foundUnicode = false;
            foreach (DiscoveredDevice device in devices)
            {
                if (device.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    Equal(17L, device.Address.ScopeId, "link-local IPv6 carries interface scope");
                    foundScoped = true;
                }
                if (device.Name == "客厅 iPad") foundUnicode = true;
            }
            Assert(foundScoped, "AAAA discovered");
            Assert(foundUnicode, "UTF-8 instance discovered");
        }

        private static void TtlExpiryAndGoodbye()
        {
            DnsPacket chain = new DnsPacket();
            AddPtr(chain, Instance, 3);
            AddSrv(chain, Instance, Host, 24864, 3);
            AddAddress(chain, Host, "192.168.1.42", 3);
            DnsSdCache cache = new DnsSdCache();
            cache.AddPacket(chain.Build(), 9, Epoch);
            Equal(1, cache.GetDevices(Epoch.AddSeconds(2)).Count, "live TTL");
            Equal(0, cache.GetDevices(Epoch.AddSeconds(4)).Count, "expired TTL removed");

            cache.AddPacket(chain.Build(), 9, Epoch.AddSeconds(5));
            DnsPacket addressGoodbye = new DnsPacket();
            AddAddress(addressGoodbye, Host, "192.168.1.42", 0);
            cache.AddPacket(addressGoodbye.Build(), 9, Epoch.AddSeconds(5));
            Equal(0, cache.GetDevices(Epoch.AddSeconds(5)).Count, "A TTL zero removes address");
            cache.AddPacket(chain.Build(), 9, Epoch.AddSeconds(6));
            DnsPacket ptrGoodbye = new DnsPacket();
            AddPtr(ptrGoodbye, Instance, 0);
            cache.AddPacket(ptrGoodbye.Build(), 9, Epoch.AddSeconds(6));
            Equal(0, cache.GetDevices(Epoch.AddSeconds(6)).Count, "PTR TTL zero removes instance");
            cache.AddPacket(chain.Build(), 9, Epoch.AddSeconds(7));
            DnsPacket srvGoodbye = new DnsPacket();
            AddSrv(srvGoodbye, Instance, Host, 24864, 0);
            cache.AddPacket(srvGoodbye.Build(), 9, Epoch.AddSeconds(7));
            Equal(0, cache.GetDevices(Epoch.AddSeconds(7)).Count, "SRV TTL zero removes endpoint");
        }

        private static void InterfaceIsolation()
        {
            DnsPacket service = new DnsPacket();
            AddPtr(service, Instance, 120);
            AddSrv(service, Instance, Host, 24864, 120);
            DnsPacket address = new DnsPacket();
            AddAddress(address, Host, "192.168.1.42", 120);
            DnsSdCache cache = new DnsSdCache();
            cache.AddPacket(service.Build(), 10, Epoch);
            DnsPacket otherAddress = new DnsPacket();
            AddAddress(otherAddress, Host, "192.168.1.43", 120);
            cache.AddPacket(otherAddress.Build(), 11, Epoch);
            Equal(0, cache.GetDevices(Epoch).Count, "records from different interfaces never form a chain");
            cache.AddPacket(address.Build(), 10, Epoch);
            Equal(1, cache.GetDevices(Epoch).Count, "same-interface chain resolves");
            cache.AddPacket(service.Build(), 11, Epoch);
            Equal(2, cache.GetDevices(Epoch).Count, "same instance on two links keeps both endpoints");
        }

        private static void IgnoreUnusableResponses()
        {
            byte[] packet = CompletePacket();
            int[] unusableFlags = new int[] { 0, 0x8600, 0x8800, 0x8403 };
            foreach (int flags in unusableFlags)
            {
                byte[] changed = (byte[])packet.Clone();
                changed[2] = (byte)(flags >> 8);
                changed[3] = (byte)flags;
                DnsSdCache cache = new DnsSdCache();
                cache.AddPacket(changed, 3, Epoch);
                Equal(0, cache.GetDevices(Epoch).Count, "query/truncated/opcode/error packet ignored");
            }
        }

        private static void RejectMalformedAtomically()
        {
            List<byte[]> malformed = new List<byte[]>();
            DnsPacket loop = new DnsPacket();
            loop.AddRawRecord(new byte[] { 0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 120, 0, 2, 0xC0, 12 });
            malformed.Add(loop.Build());
            DnsPacket outOfBounds = new DnsPacket();
            outOfBounds.AddRawRecord(new byte[] { 0xC0, 255, 0, 12, 0, 1, 0, 0, 0, 120, 0, 2, 0, 0 });
            malformed.Add(outOfBounds.Build());
            DnsPacket shortAddress = new DnsPacket();
            shortAddress.AddRecord(Host, 1, 120, delegate(DnsPacket data) { data.Bytes(new byte[] { 1, 2, 3 }); });
            malformed.Add(shortAddress.Build());
            DnsPacket extraPtrData = new DnsPacket();
            extraPtrData.AddRecord(Service, 12, 120, delegate(DnsPacket data) { data.Name(Instance); data.Byte(255); });
            malformed.Add(extraPtrData.Build());
            malformed.Add(new byte[] { 0, 0, 0x84, 0 });

            foreach (byte[] packet in malformed)
            {
                DnsSdCache cache = new DnsSdCache();
                Throws<InvalidDataException>(delegate { cache.AddPacket(packet, 3, Epoch); });
                Equal(0, cache.GetDevices(Epoch).Count, "malformed input creates no device");
            }

            DnsPacket validThenInvalid = new DnsPacket();
            AddPtr(validThenInvalid, Instance, 120);
            AddSrv(validThenInvalid, Instance, Host, 24864, 120);
            AddAddress(validThenInvalid, Host, "192.168.1.42", 120);
            validThenInvalid.AddRawRecord(new byte[] { 0xC0, 255 });
            DnsSdCache atomic = new DnsSdCache();
            Throws<InvalidDataException>(delegate { atomic.AddPacket(validThenInvalid.Build(), 3, Epoch); });
            Equal(0, atomic.GetDevices(Epoch).Count, "no partial commit before malformed final record");
            Equal(0, atomic.GetMissingNames(Epoch).Count, "no partial PTR commit on rejection");
        }

        private static void SimulatedDiscoveryTransport()
        {
            DnsPacket ptr = new DnsPacket();
            AddPtr(ptr, Instance, 120);
            DnsPacket srv = new DnsPacket();
            AddSrv(srv, Instance, Host, 24864, 120);
            DnsPacket address = new DnsPacket();
            AddAddress(address, Host, "192.168.1.42", 120);
            FakeTransport transport = new FakeTransport();
            transport.Incoming.Enqueue(new MdnsDatagram { Data = new byte[] { 0, 0, 0x84 }, InterfaceIndex = 6 });
            transport.Incoming.Enqueue(new MdnsDatagram { Data = ptr.Build(), InterfaceIndex = 6 });
            transport.Incoming.Enqueue(new MdnsDatagram { Data = srv.Build(), InterfaceIndex = 6 });
            transport.Incoming.Enqueue(new MdnsDatagram { Data = address.Build(), InterfaceIndex = 6 });
            MdnsDiscovery discovery = new MdnsDiscovery(700, delegate(string message) { }, delegate { return transport; });
            IList<DiscoveredDevice> devices = discovery.Find(CancellationToken.None);
            Equal(1, devices.Count, "fake transport resolves split responses");
            Assert(transport.Disposed, "transport disposed after discovery result");
            Assert(transport.Sent.Count >= 3, "discovery sent PTR and dependent queries");
            List<string> names = new List<string>();
            foreach (byte[] query in transport.Sent)
            {
                int position = 12;
                names.Add(ReadUncompressedName(query, ref position));
            }
            Contains(names, Service, "initial PTR service query");
            Contains(names, Instance, "missing instance SRV query");
            Contains(names, Host, "missing host address query");

            DnsPacket delayedDevice = new DnsPacket();
            AddPtr(delayedDevice, "Second iPad." + Service, 120);
            AddSrv(delayedDevice, "Second iPad." + Service, "second-ipad.local", 24864, 120);
            AddAddress(delayedDevice, "second-ipad.local", "192.168.1.44", 120);
            FakeTransport staggered = new FakeTransport();
            staggered.Incoming.Enqueue(new MdnsDatagram { Data = CompletePacket(), InterfaceIndex = 6 });
            Stopwatch responseDelay = Stopwatch.StartNew();
            bool secondResponseQueued = false;
            staggered.OnReceive = delegate {
                if (!secondResponseQueued && responseDelay.ElapsedMilliseconds >= 350)
                {
                    staggered.Incoming.Enqueue(new MdnsDatagram { Data = delayedDevice.Build(), InterfaceIndex = 6 });
                    secondResponseQueued = true;
                }
            };
            MdnsDiscovery fullWindow = new MdnsDiscovery(700, delegate(string message) { }, delegate { return staggered; });
            IList<DiscoveredDevice> staggeredDevices = fullWindow.Find(CancellationToken.None);
            Equal(2, staggeredDevices.Count, "early complete endpoint does not hide a device responding after 350 ms");
            Assert(staggered.Disposed, "staggered response transport disposed");

            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                FakeTransport cancelled = new FakeTransport();
                cancelled.OnReceive = delegate { cancellation.Cancel(); };
                MdnsDiscovery waiting = new MdnsDiscovery(500, delegate(string message) { }, delegate { return cancelled; });
                Throws<OperationCanceledException>(delegate { waiting.Find(cancellation.Token); });
                Assert(cancelled.Disposed, "transport disposed when discovery cancelled");
            }
        }

        private static void AutomaticBridgeFallback()
        {
            RunAutomaticBridge(null);
            RunAutomaticBridge("B Working iPad");
        }

        private static void RunAutomaticBridge(string selectedName)
        {
            string mapName = "Local\\BROKENITHM_DISCOVERY_TEST_" + Guid.NewGuid().ToString("N");
            using (SharedMemoryState state = new SharedMemoryState(mapName))
            using (MemoryMappedFile map = MemoryMappedFile.OpenExisting(mapName))
            using (MemoryMappedViewAccessor view = map.CreateViewAccessor(0, 150))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                TcpListener unavailable = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                unavailable.Start();
                int failedPort = ((IPEndPoint)unavailable.LocalEndpoint).Port;
                unavailable.Stop();
                List<DiscoveredDevice> devices = new List<DiscoveredDevice>();
                devices.Add(new DiscoveredDevice { Name = "A Unavailable iPad", InstanceName = "A Unavailable iPad." + Service,
                    Address = IPAddress.Loopback, Port = failedPort, InterfaceIndex = 1 });
                devices.Add(new DiscoveredDevice { Name = "B Working iPad", InstanceName = "B Working iPad." + Service,
                    Address = IPAddress.Loopback, Port = ((IPEndPoint)listener.LocalEndpoint).Port, InterfaceIndex = 1 });
                FakeDiscovery discovery = new FakeDiscovery(devices);
                BridgeOptions options = BridgeOptions.Parse(selectedName == null ? new string[] { "--no-led" } :
                    new string[] { "--no-led", "--device", selectedName });
                options.ConnectTimeoutMs = 500;
                options.HandshakeTimeoutMs = 1000;
                options.RetryDelayMs = 50;
                List<string> logs = new List<string>();
                Action<string> log = delegate(string message) { lock (logs) logs.Add(message); };
                LanBridge bridge = new LanBridge(options, state, log, discovery);
                Task worker = Task.Factory.StartNew(delegate { bridge.Run(cancellation.Token); },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                try
                {
                    using (TcpClient device = Accept(listener))
                    {
                        SendInput(device, 0xB4);
                        WaitForValue(view, 6, 0xB4);
                    }
                    WaitForValue(view, 6, 0);
                    using (TcpClient device = Accept(listener))
                    {
                        SendInput(device, 0xB5);
                        WaitForValue(view, 6, 0xB5);
                        cancellation.Cancel();
                        Assert(worker.Wait(3000), "automatic connection cancels promptly");
                    }
                    Equal((byte)0, view.ReadByte(6), "automatic connection cancellation releases inputs");
                    Assert(discovery.Calls > 0, "automatic mode invokes discovery");
                    int unavailableAttempts = 0;
                    lock (logs)
                    {
                        foreach (string message in logs)
                        {
                            if (message.IndexOf("Connecting to ", StringComparison.Ordinal) >= 0 &&
                                message.IndexOf("127.0.0.1:" + failedPort, StringComparison.Ordinal) >= 0) unavailableAttempts++;
                        }
                    }
                    Equal(selectedName == null ? 1 : 0, unavailableAttempts,
                        selectedName == null ? "fallback happens once, reconnect keeps successful instance" : "--device skips unmatched instance");
                }
                finally
                {
                    cancellation.Cancel();
                    listener.Stop();
                    Assert(worker.Wait(3000), "automatic bridge test worker stopped");
                    worker.Dispose();
                }
            }
        }

        private static TcpClient Accept(TcpListener listener)
        {
            IAsyncResult operation = listener.BeginAcceptTcpClient(null, null);
            using (WaitHandle ready = operation.AsyncWaitHandle)
            {
                Assert(ready.WaitOne(5000), "discovered endpoint connected");
                return listener.EndAcceptTcpClient(operation);
            }
        }

        private static void SendInput(TcpClient device, byte slider)
        {
            byte[] bytes = new byte[46];
            bytes[0] = 3; bytes[1] = (byte)'W'; bytes[2] = (byte)'E'; bytes[3] = (byte)'L';
            bytes[4] = 41; bytes[5] = (byte)'I'; bytes[6] = (byte)'N'; bytes[7] = (byte)'P';
            for (int i = 8; i < 14; i++) bytes[i] = 1;
            for (int i = 14; i < bytes.Length; i++) bytes[i] = slider;
            device.GetStream().Write(bytes, 0, bytes.Length);
        }

        private static void WaitForValue(MemoryMappedViewAccessor view, int offset, byte value)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (view.ReadByte(offset) != value)
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Timed out waiting for shared-memory input " + value);
                Thread.Sleep(10);
            }
        }

        private sealed class FakeDiscovery : IDeviceDiscovery
        {
            private readonly IList<DiscoveredDevice> devices;
            public int Calls;

            public FakeDiscovery(IList<DiscoveredDevice> devices)
            {
                this.devices = devices;
            }

            public IList<DiscoveredDevice> Find(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref Calls);
                return devices;
            }
        }

        private sealed class FakeTransport : IMdnsTransport
        {
            public readonly Queue<MdnsDatagram> Incoming = new Queue<MdnsDatagram>();
            public readonly List<byte[]> Sent = new List<byte[]>();
            public Action OnReceive;
            public bool Disposed;

            public void Send(byte[] query)
            {
                Sent.Add((byte[])query.Clone());
            }

            public MdnsDatagram Receive(int timeoutMs)
            {
                if (Incoming.Count > 0) return Incoming.Dequeue();
                if (OnReceive != null) OnReceive();
                Thread.Sleep(Math.Max(0, Math.Min(timeoutMs, 5)));
                return null;
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private static byte[] CompletePacket()
        {
            DnsPacket packet = new DnsPacket();
            AddPtr(packet, Instance, 120);
            AddSrv(packet, Instance, Host, 24864, 120);
            AddAddress(packet, Host, "192.168.1.42", 120);
            return packet.Build();
        }

        private static void AddPtr(DnsPacket packet, string instance, uint ttl)
        {
            packet.AddRecord(Service, 12, ttl, delegate(DnsPacket data) { data.Name(instance); });
        }

        private static void AddSrv(DnsPacket packet, string instance, string host, int port, uint ttl)
        {
            packet.AddRecord(instance, 33, ttl, delegate(DnsPacket data) {
                data.UInt16(0); data.UInt16(0); data.UInt16(port); data.Name(host);
            });
        }

        private static void AddAddress(DnsPacket packet, string host, string address, uint ttl)
        {
            byte[] bytes = IPAddress.Parse(address).GetAddressBytes();
            packet.AddRecord(host, (ushort)(bytes.Length == 4 ? 1 : 28), ttl,
                delegate(DnsPacket data) { data.Bytes(bytes); });
        }

        private static int UInt16(byte[] bytes, int position)
        {
            return (bytes[position] << 8) | bytes[position + 1];
        }

        private static string ReadUncompressedName(byte[] bytes, ref int position)
        {
            List<string> labels = new List<string>();
            int length;
            while ((length = bytes[position++]) != 0)
            {
                Assert(length <= 63 && position + length <= bytes.Length, "valid query label length");
                labels.Add(Encoding.UTF8.GetString(bytes, position, length));
                position += length;
            }
            return String.Join(".", labels.ToArray());
        }

        private static void Contains(IList<string> values, string expected, string message)
        {
            foreach (string value in values) if (String.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) return;
            throw new Exception(message + ": missing " + expected);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": expected " + expected + ", got " + actual);
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

        private sealed class DnsPacket
        {
            private readonly MemoryStream stream = new MemoryStream();
            private readonly Dictionary<string, int> nameOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private int records;

            public DnsPacket()
            {
                Bytes(new byte[12]);
            }

            public void AddRecord(string owner, ushort type, uint ttl, Action<DnsPacket> writeData)
            {
                Name(owner);
                UInt16(type);
                UInt16(type == 12 ? 1 : 0x8001);
                UInt32(ttl);
                long lengthPosition = stream.Position;
                UInt16(0);
                long dataPosition = stream.Position;
                writeData(this);
                long end = stream.Position;
                stream.Position = lengthPosition;
                UInt16(checked((int)(end - dataPosition)));
                stream.Position = end;
                records++;
            }

            public void AddRawRecord(byte[] bytes)
            {
                Bytes(bytes);
                records++;
            }

            public byte[] Build()
            {
                byte[] bytes = stream.ToArray();
                bytes[2] = 0x84;
                bytes[3] = 0;
                bytes[6] = (byte)(records >> 8);
                bytes[7] = (byte)records;
                return bytes;
            }

            public void Name(string name)
            {
                string[] labels = name.Split('.');
                for (int i = 0; i < labels.Length; i++)
                {
                    string suffix = String.Join(".", labels, i, labels.Length - i);
                    int offset;
                    if (nameOffsets.TryGetValue(suffix, out offset))
                    {
                        UInt16(0xC000 | offset);
                        return;
                    }
                    if (stream.Position < 0x4000) nameOffsets[suffix] = checked((int)stream.Position);
                    byte[] bytes = Encoding.UTF8.GetBytes(labels[i]);
                    if (bytes.Length < 1 || bytes.Length > 63) throw new Exception("Bad test label length.");
                    Byte(bytes.Length);
                    Bytes(bytes);
                }
                Byte(0);
            }

            public void UInt16(int number)
            {
                Byte(number >> 8);
                Byte(number);
            }

            public void UInt32(uint number)
            {
                Byte((int)(number >> 24)); Byte((int)(number >> 16)); Byte((int)(number >> 8)); Byte((int)number);
            }

            public void Byte(int value)
            {
                stream.WriteByte((byte)value);
            }

            public void Bytes(byte[] bytes)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
