using System;
using System.IO;

namespace BrokenithmLan
{
    internal static class Protocol
    {
        public static byte[] ReadFrame(Stream stream)
        {
            int length = stream.ReadByte();
            if (length < 0) throw new EndOfStreamException("The iPad closed the connection.");
            if (length < 3) throw new InvalidDataException("A packet must contain a three-byte command.");

            int previousTimeout = stream.CanTimeout ? stream.ReadTimeout : 0;
            if (stream.CanTimeout) stream.ReadTimeout = 2000;
            try
            {
                byte[] payload = new byte[length];
                int offset = 0;
                while (offset < length)
                {
                    int count = stream.Read(payload, offset, length - offset);
                    if (count == 0) throw new EndOfStreamException("The connection ended inside a packet.");
                    offset += count;
                }
                return payload;
            }
            finally
            {
                if (stream.CanTimeout)
                {
                    try { stream.ReadTimeout = previousTimeout; }
                    catch (ObjectDisposedException) { }
                    catch (IOException) { }
                }
            }
        }

        public static bool IsWelcome(byte[] payload)
        {
            return payload.Length == 3 && Matches(payload, 'W', 'E', 'L');
        }

        public static void ApplyFrame(byte[] payload, SharedMemoryState state, ref bool airEnabled)
        {
            if (payload.Length >= 41 && payload.Length <= 45 && Matches(payload, 'I', 'N', 'P'))
            {
                state.ApplyInput(payload, airEnabled);
                return;
            }
            if (payload.Length == 4 && Matches(payload, 'A', 'I', 'R') && payload[3] <= 1)
            {
                airEnabled = payload[3] == 1;
                if (!airEnabled) state.ClearAir();
                return;
            }
            if (payload.Length == 4 && Matches(payload, 'F', 'N', 'C') &&
                (payload[3] == 1 || payload[3] == 2))
            {
                state.TriggerFunction(payload[3]);
                return;
            }
            throw new InvalidDataException("Unsupported command, length or value in a Brokenithm packet.");
        }

        private static bool Matches(byte[] payload, char first, char second, char third)
        {
            return payload.Length >= 3 && payload[0] == first && payload[1] == second && payload[2] == third;
        }
    }
}
