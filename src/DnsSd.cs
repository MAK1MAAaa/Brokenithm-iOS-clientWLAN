using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BrokenithmLan
{
    internal sealed class DiscoveredDevice
    {
        public string Name { get; set; }
        public string InstanceName { get; set; }
        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public int InterfaceIndex { get; set; }
    }

    internal sealed class DnsSdCache
    {
        public const string ServiceName = "_brokenithm._tcp.local";
        private const int MaximumRecords = 4096;
        private readonly object gate = new object();
        private readonly List<DnsSdRecord> records = new List<DnsSdRecord>();

        public void AddPacket(byte[] packet, int interfaceIndex, DateTime now)
        {
            IList<DnsSdRecord> incoming = DnsSdMessage.ParsePacket(packet, interfaceIndex, now);
            lock (gate)
            {
                Prune(now);
                foreach (DnsSdRecord record in incoming)
                {
                    for (int i = records.Count - 1; i >= 0; i--)
                    {
                        if (SameRecord(records[i], record)) records.RemoveAt(i);
                    }
                    if (record.Expires > now)
                    {
                        if (records.Count >= MaximumRecords) records.RemoveAt(0);
                        records.Add(record);
                    }
                }
            }
        }

        public IList<DiscoveredDevice> GetDevices(DateTime now)
        {
            lock (gate)
            {
                Prune(now);
                List<DiscoveredDevice> devices = new List<DiscoveredDevice>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DnsSdRecord pointer in records)
                {
                    if (!IsServicePointer(pointer)) continue;
                    IList<DnsSdRecord> services = FindServices(pointer);
                    foreach (DnsSdRecord service in services)
                    {
                        if (service.Port == 0 || service.Target.Length == 0) continue;
                        foreach (DnsSdRecord address in records)
                        {
                            if (!IsAddressFor(address, service)) continue;
                            IPAddress endpoint = address.Address;
                            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.IsIPv6LinkLocal)
                            {
                                if (address.InterfaceIndex <= 0) continue;
                                endpoint = new IPAddress(endpoint.GetAddressBytes(), address.InterfaceIndex);
                            }
                            string identity = pointer.Target + "|" + endpoint + "|" + service.Port.ToString(CultureInfo.InvariantCulture);
                            if (!seen.Add(identity)) continue;
                            devices.Add(new DiscoveredDevice
                            {
                                Name = pointer.FriendlyTarget,
                                InstanceName = pointer.Target,
                                Address = endpoint,
                                Port = service.Port,
                                InterfaceIndex = service.InterfaceIndex
                            });
                        }
                    }
                }
                devices.Sort(delegate(DiscoveredDevice first, DiscoveredDevice second)
                {
                    int family = AddressRank(first.Address).CompareTo(AddressRank(second.Address));
                    if (family != 0) return family;
                    int name = StringComparer.OrdinalIgnoreCase.Compare(first.InstanceName, second.InstanceName);
                    if (name != 0) return name;
                    int address = StringComparer.Ordinal.Compare(first.Address.ToString(), second.Address.ToString());
                    if (address != 0) return address;
                    int port = first.Port.CompareTo(second.Port);
                    return port != 0 ? port : first.InterfaceIndex.CompareTo(second.InterfaceIndex);
                });
                return devices;
            }
        }

        public IList<string> GetMissingNames(DateTime now)
        {
            lock (gate)
            {
                Prune(now);
                HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DnsSdRecord pointer in records)
                {
                    if (!IsServicePointer(pointer)) continue;
                    IList<DnsSdRecord> services = FindServices(pointer);
                    if (services.Count == 0)
                    {
                        missing.Add(pointer.Target);
                        continue;
                    }
                    foreach (DnsSdRecord service in services)
                    {
                        if (service.Target.Length == 0 || service.Port == 0) continue;
                        bool hasAddress = false;
                        foreach (DnsSdRecord address in records)
                        {
                            if (IsAddressFor(address, service)) { hasAddress = true; break; }
                        }
                        if (!hasAddress) missing.Add(service.Target);
                    }
                }
                List<string> result = new List<string>(missing);
                result.Sort(StringComparer.OrdinalIgnoreCase);
                return result;
            }
        }

        private IList<DnsSdRecord> FindServices(DnsSdRecord pointer)
        {
            List<DnsSdRecord> found = new List<DnsSdRecord>();
            int minimumPriority = Int32.MaxValue;
            foreach (DnsSdRecord record in records)
            {
                if (record.Type != 33 || record.InterfaceIndex != pointer.InterfaceIndex || !NameEquals(record.Name, pointer.Target)) continue;
                if (record.Priority < minimumPriority)
                {
                    found.Clear();
                    minimumPriority = record.Priority;
                }
                if (record.Priority == minimumPriority) found.Add(record);
            }
            return found;
        }

        private static bool IsServicePointer(DnsSdRecord record)
        {
            return record.Type == 12 && NameEquals(record.Name, ServiceName) &&
                record.Target.Length > ServiceName.Length + 1 &&
                record.Target.EndsWith("." + ServiceName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAddressFor(DnsSdRecord address, DnsSdRecord service)
        {
            return (address.Type == 1 || address.Type == 28) &&
                address.InterfaceIndex == service.InterfaceIndex && NameEquals(address.Name, service.Target);
        }

        private static bool SameRecord(DnsSdRecord first, DnsSdRecord second)
        {
            return first.Type == second.Type && first.InterfaceIndex == second.InterfaceIndex &&
                NameEquals(first.Name, second.Name) && NameEquals(first.DataIdentity, second.DataIdentity);
        }

        private static bool NameEquals(string first, string second)
        {
            return String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static int AddressRank(IPAddress address)
        {
            return address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1;
        }

        private void Prune(DateTime now)
        {
            for (int i = records.Count - 1; i >= 0; i--)
            {
                if (records[i].Expires <= now) records.RemoveAt(i);
            }
        }
    }

    internal sealed class DnsSdRecord
    {
        public string Name;
        public string Target = String.Empty;
        public string FriendlyTarget = String.Empty;
        public string DataIdentity;
        public int Type;
        public int Priority;
        public int Port;
        public int InterfaceIndex;
        public DateTime Expires;
        public IPAddress Address;
    }

    internal static class DnsSdMessage
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] CreateQuery(string name, ushort recordType)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentException("A DNS name is required.", "name");
            IList<string> labels = SplitName(name);
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] header = new byte[12];
                header[5] = 1;
                stream.Write(header, 0, header.Length);
                int nameLength = 1;
                foreach (string label in labels)
                {
                    byte[] bytes = Utf8.GetBytes(label);
                    if (bytes.Length < 1 || bytes.Length > 63) throw new ArgumentException("A DNS label must be 1-63 UTF-8 bytes.", "name");
                    nameLength += bytes.Length + 1;
                    if (nameLength > 255) throw new ArgumentException("A DNS name exceeds 255 wire bytes.", "name");
                    stream.WriteByte((byte)bytes.Length);
                    stream.Write(bytes, 0, bytes.Length);
                }
                stream.WriteByte(0);
                stream.WriteByte((byte)(recordType >> 8));
                stream.WriteByte((byte)recordType);
                stream.WriteByte(0);
                stream.WriteByte(1);
                return stream.ToArray();
            }
        }

        internal static IList<DnsSdRecord> ParsePacket(byte[] packet, int interfaceIndex, DateTime now)
        {
            if (packet == null || packet.Length < 12 || packet.Length > 65535) throw Malformed();
            int flags = UInt16(packet, 2);
            List<DnsSdRecord> records = new List<DnsSdRecord>();
            if ((flags & 0x8000) == 0 || (flags & 0x0200) != 0 || (flags & 0x780F) != 0) return records;
            int questions = UInt16(packet, 4);
            int recordCount = UInt16(packet, 6) + UInt16(packet, 8) + UInt16(packet, 10);
            if (questions > 4096 || recordCount > 4096 || questions + recordCount > packet.Length / 5) throw Malformed();
            int offset = 12;
            for (int i = 0; i < questions; i++)
            {
                ReadName(packet, ref offset, packet.Length);
                Require(packet, offset, 4);
                offset += 4;
            }
            for (int i = 0; i < recordCount; i++)
            {
                ParsedName owner = ReadName(packet, ref offset, packet.Length);
                Require(packet, offset, 10);
                int type = UInt16(packet, offset);
                int recordClass = UInt16(packet, offset + 2) & 0x7FFF;
                uint ttl = UInt32(packet, offset + 4);
                int length = UInt16(packet, offset + 8);
                offset += 10;
                Require(packet, offset, length);
                int end = offset + length;
                DnsSdRecord record = new DnsSdRecord
                {
                    Name = owner.Canonical,
                    Type = type,
                    InterfaceIndex = interfaceIndex,
                    Expires = Expiration(now, ttl)
                };
                if (recordClass == 1 && (type == 12 || type == 33))
                {
                    int position = offset;
                    int weight = 0;
                    if (type == 33)
                    {
                        if (length < 7) throw Malformed();
                        record.Priority = UInt16(packet, position);
                        weight = UInt16(packet, position + 2);
                        record.Port = UInt16(packet, position + 4);
                        position += 6;
                    }
                    ParsedName target = ReadName(packet, ref position, end);
                    if (position != end) throw Malformed();
                    record.Target = target.Canonical;
                    record.FriendlyTarget = target.FirstLabel;
                    record.DataIdentity = target.Canonical;
                    if (type == 33)
                    {
                        record.DataIdentity += "|" + record.Priority.ToString(CultureInfo.InvariantCulture) + "|" +
                            weight.ToString(CultureInfo.InvariantCulture) + "|" + record.Port.ToString(CultureInfo.InvariantCulture);
                    }
                    records.Add(record);
                }
                else if (recordClass == 1 && (type == 1 || type == 28))
                {
                    if (length != (type == 1 ? 4 : 16)) throw Malformed();
                    byte[] address = new byte[length];
                    Buffer.BlockCopy(packet, offset, address, 0, length);
                    record.Address = new IPAddress(address);
                    record.DataIdentity = record.Address.ToString();
                    records.Add(record);
                }
                offset = end;
            }
            return records;
        }

        private static ParsedName ReadName(byte[] packet, ref int offset, int encodedLimit)
        {
            List<string> labels = new List<string>();
            HashSet<int> visited = new HashSet<int>();
            int position = offset;
            bool jumped = false;
            int wireLength = 1;
            int steps = 0;
            while (true)
            {
                if (++steps > 128) throw Malformed();
                if (position < 0 || position >= packet.Length || (!jumped && position >= encodedLimit) || !visited.Add(position)) throw Malformed();
                int length = packet[position++];
                if ((length & 0xC0) == 0xC0)
                {
                    if (position >= packet.Length || (!jumped && position >= encodedLimit)) throw Malformed();
                    int pointer = ((length & 0x3F) << 8) | packet[position++];
                    if (!jumped) offset = position;
                    jumped = true;
                    position = pointer;
                    continue;
                }
                if ((length & 0xC0) != 0) throw Malformed();
                if (length == 0)
                {
                    if (!jumped) offset = position;
                    string[] escaped = new string[labels.Count];
                    for (int i = 0; i < labels.Count; i++) escaped[i] = labels[i].Replace("\\", "\\\\").Replace(".", "\\.");
                    return new ParsedName
                    {
                        Canonical = String.Join(".", escaped),
                        FirstLabel = labels.Count == 0 ? String.Empty : labels[0]
                    };
                }
                wireLength += length + 1;
                if (wireLength > 255 || position > packet.Length - length || (!jumped && position > encodedLimit - length)) throw Malformed();
                try { labels.Add(Utf8.GetString(packet, position, length)); }
                catch (DecoderFallbackException error) { throw new InvalidDataException("A DNS label contains invalid UTF-8.", error); }
                position += length;
            }
        }

        private static IList<string> SplitName(string name)
        {
            List<string> labels = new List<string>();
            StringBuilder label = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char value = name[i];
                if (value == '\\')
                {
                    if (++i == name.Length) throw new ArgumentException("Incomplete DNS name escape.", "name");
                    label.Append(name[i]);
                }
                else if (value == '.')
                {
                    if (label.Length == 0) throw new ArgumentException("Empty DNS label.", "name");
                    labels.Add(label.ToString());
                    label.Length = 0;
                }
                else label.Append(value);
            }
            if (label.Length != 0) labels.Add(label.ToString());
            if (labels.Count == 0) throw new ArgumentException("A DNS name is required.", "name");
            return labels;
        }

        private static int UInt16(byte[] packet, int offset)
        {
            return (packet[offset] << 8) | packet[offset + 1];
        }

        private static uint UInt32(byte[] packet, int offset)
        {
            return ((uint)packet[offset] << 24) | ((uint)packet[offset + 1] << 16) |
                ((uint)packet[offset + 2] << 8) | packet[offset + 3];
        }

        private static void Require(byte[] packet, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > packet.Length - count) throw Malformed();
        }

        private static DateTime Expiration(DateTime now, uint ttl)
        {
            long lifetime = (long)ttl * TimeSpan.TicksPerSecond;
            return new DateTime(now.Ticks > DateTime.MaxValue.Ticks - lifetime ? DateTime.MaxValue.Ticks : now.Ticks + lifetime, now.Kind);
        }

        private static InvalidDataException Malformed()
        {
            return new InvalidDataException("Malformed mDNS packet.");
        }

        private sealed class ParsedName
        {
            public string Canonical;
            public string FirstLabel;
        }
    }
}
