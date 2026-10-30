using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace BrokenithmLan
{
    internal interface IDeviceDiscovery
    {
        IList<DiscoveredDevice> Find(CancellationToken token);
    }

    internal sealed class MdnsDatagram
    {
        public byte[] Data;
        public int InterfaceIndex;
        public string Source { get; set; }
    }

    internal interface IMdnsTransport : IDisposable
    {
        void Send(byte[] query);
        MdnsDatagram Receive(int timeoutMs);
    }

    internal sealed class MdnsDiscovery : IDeviceDiscovery
    {
        private readonly int timeoutMs;
        private readonly Action<string> log;
        private readonly Func<IMdnsTransport> createTransport;

        public MdnsDiscovery(int timeoutMs, Action<string> log)
            : this(timeoutMs, log, delegate { return new MulticastDnsTransport(log); }) { }

        internal MdnsDiscovery(int timeoutMs, Action<string> log, Func<IMdnsTransport> createTransport)
        {
            this.timeoutMs = timeoutMs;
            this.log = log;
            this.createTransport = createTransport;
        }

        public IList<DiscoveredDevice> Find(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            DnsSdCache cache = new DnsSdCache();
            Dictionary<string, long> queries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Stopwatch elapsed = Stopwatch.StartNew();
            int receivedPackets = 0;
            int malformedPackets = 0;
            log("Discovering Brokenithm on the local network ...");
            using (IMdnsTransport transport = createTransport())
            {
                while (elapsed.ElapsedMilliseconds < timeoutMs)
                {
                    token.ThrowIfCancellationRequested();
                    Query(transport, DnsSdCache.ServiceName, 12, elapsed.ElapsedMilliseconds, queries);
                    DateTime now = DateTime.UtcNow;
                    foreach (string name in cache.GetMissingNames(now))
                    {
                        Query(transport, name, 255, elapsed.ElapsedMilliseconds, queries);
                    }
                    MdnsDatagram packet = transport.Receive((int)Math.Max(0, Math.Min(100, timeoutMs - elapsed.ElapsedMilliseconds)));
                    if (packet != null)
                    {
                        receivedPackets++;
                        try { cache.AddPacket(packet.Data, packet.InterfaceIndex, DateTime.UtcNow); }
                        catch (InvalidDataException error)
                        {
                            malformedPackets++;
                            if (malformedPackets <= 3) log("Malformed mDNS packet ignored: source=" + (packet.Source ?? "unknown") +
                                "; interface=" + packet.InterfaceIndex + "; bytes=" + (packet.Data == null ? 0 : packet.Data.Length) + "; " + error.Message);
                        }
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            IList<DiscoveredDevice> devices = cache.GetDevices(DateTime.UtcNow);
            log("mDNS round completed: elapsedMs=" + elapsed.ElapsedMilliseconds + "; datagrams=" + receivedPackets +
                "; malformed=" + malformedPackets + "; endpoints=" + devices.Count);
            return devices;
        }

        private void Query(IMdnsTransport transport, string name, ushort type, long now, Dictionary<string, long> queries)
        {
            string key = type + ":" + name;
            long lastSent;
            if (queries.TryGetValue(key, out lastSent) && now - lastSent < 1000) return;
            transport.Send(DnsSdMessage.CreateQuery(name, type));
            log("mDNS query: type=" + type + "; name=" + name);
            queries[key] = now;
        }
    }

    internal sealed class MulticastDnsTransport : IMdnsTransport
    {
        private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
        private readonly Socket socket;
        private readonly List<Adapter> adapters = new List<Adapter>();
        private readonly byte[] receiveBuffer = new byte[65535];

        public MulticastDnsTransport(Action<string> log)
        {
            Socket created = OpenSocket(log);
            try
            {
                created.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                created.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                created.ReceiveBufferSize = 262144;
                created.Blocking = false;
                HashSet<int> joined = new HashSet<int>();
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || !network.SupportsMulticast ||
                        network.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        network.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    try
                    {
                        IPInterfaceProperties properties = network.GetIPProperties();
                        IPv4InterfaceProperties ipv4 = properties.GetIPv4Properties();
                        if (ipv4 == null || joined.Contains(ipv4.Index)) continue;
                        foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                        {
                            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                                IPAddress.IsLoopback(unicast.Address) || unicast.Address.Equals(IPAddress.Any)) continue;
                            int scopeIndex = ipv4.Index;
                            if (network.Supports(NetworkInterfaceComponent.IPv6))
                            {
                                IPv6InterfaceProperties ipv6 = properties.GetIPv6Properties();
                                if (ipv6 != null) scopeIndex = ipv6.Index;
                            }
                            created.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                                new MulticastOption(Group, unicast.Address));
                            adapters.Add(new Adapter { Address = unicast.Address, IPv4Index = ipv4.Index, ScopeIndex = scopeIndex });
                            log("mDNS interface: name=" + network.Name + "; local=" + unicast.Address +
                                "; IPv4Index=" + ipv4.Index + "; scopeIndex=" + scopeIndex);
                            joined.Add(ipv4.Index);
                            break;
                        }
                    }
                    catch (NetworkInformationException) { }
                    catch (SocketException error) { log("mDNS interface skipped: " + error.Message); }
                }
                if (adapters.Count == 0) throw new SocketException((int)SocketError.NetworkUnreachable);
                socket = created;
            }
            catch
            {
                created.Close();
                throw;
            }
        }

        private static Socket OpenSocket(Action<string> log)
        {
            Socket shared = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                shared.ExclusiveAddressUse = false;
                shared.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                shared.Bind(new IPEndPoint(IPAddress.Any, 5353));
                return shared;
            }
            catch (SocketException error)
            {
                shared.Close();
                if (error.SocketErrorCode != SocketError.AddressAlreadyInUse && error.SocketErrorCode != SocketError.AccessDenied) throw;
                log("UDP 5353 is unavailable; using legacy mDNS queries with unicast replies.");
                Socket fallback = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    fallback.Bind(new IPEndPoint(IPAddress.Any, 0));
                    return fallback;
                }
                catch
                {
                    fallback.Close();
                    throw;
                }
            }
            catch
            {
                shared.Close();
                throw;
            }
        }

        public void Send(byte[] query)
        {
            SocketException lastError = null;
            int sent = 0;
            foreach (Adapter adapter in adapters)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, adapter.Address.GetAddressBytes());
                    socket.SendTo(query, new IPEndPoint(Group, 5353));
                    sent++;
                }
                catch (SocketException error) { lastError = error; }
            }
            if (sent == 0 && lastError != null) throw lastError;
        }

        public MdnsDatagram Receive(int timeoutMs)
        {
            if (!socket.Poll(Math.Max(0, timeoutMs) * 1000, SelectMode.SelectRead)) return null;
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            SocketFlags flags = SocketFlags.None;
            IPPacketInformation packetInfo;
            int length;
            try
            {
                length = socket.ReceiveMessageFrom(receiveBuffer, 0, receiveBuffer.Length, ref flags, ref remote, out packetInfo);
            }
            catch (SocketException error)
            {
                if (error.SocketErrorCode == SocketError.WouldBlock || error.SocketErrorCode == SocketError.TimedOut ||
                    error.SocketErrorCode == SocketError.ConnectionReset || error.SocketErrorCode == SocketError.MessageSize) return null;
                throw;
            }
            if (((IPEndPoint)remote).Port != 5353 || (flags & SocketFlags.Truncated) != 0) return null;
            foreach (Adapter adapter in adapters)
            {
                if (adapter.IPv4Index != packetInfo.Interface) continue;
                byte[] data = new byte[length];
                Buffer.BlockCopy(receiveBuffer, 0, data, 0, length);
                return new MdnsDatagram { Data = data, InterfaceIndex = adapter.ScopeIndex, Source = remote.ToString() };
            }
            return null;
        }

        public void Dispose()
        {
            socket.Close();
        }

        private sealed class Adapter
        {
            public IPAddress Address;
            public int IPv4Index;
            public int ScopeIndex;
        }
    }
}
