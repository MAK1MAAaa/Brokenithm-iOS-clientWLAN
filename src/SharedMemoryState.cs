using System;
using System.IO.MemoryMappedFiles;

namespace BrokenithmLan
{
    internal sealed class SharedMemoryState : IDisposable
    {
        public const string DefaultName = "Local\\BROKENITHM_SHARED_BUFFER";
        private readonly object gate = new object();
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor accessor;
        private bool disposed;

        public SharedMemoryState(string name)
        {
            mapping = MemoryMappedFile.CreateOrOpen(name, 1024, MemoryMappedFileAccess.ReadWrite);
            try
            {
                accessor = mapping.CreateViewAccessor(0, 150, MemoryMappedFileAccess.ReadWrite);
            }
            catch
            {
                mapping.Dispose();
                throw;
            }
        }

        public void ClearInputs()
        {
            lock (gate)
            {
                if (disposed) return;
                for (int i = 0; i < 38; i++) accessor.Write(i, (byte)0);
                for (int i = 134; i < 138; i++) accessor.Write(i, (byte)0);
            }
        }

        public void ClearAir()
        {
            lock (gate)
            {
                for (int i = 0; i < 6; i++) accessor.Write(i, (byte)0);
            }
        }

        public void ApplyInput(byte[] payload, bool airEnabled)
        {
            lock (gate)
            {
                for (int i = 0; i < 6; i++) accessor.Write(i, airEnabled ? payload[3 + i] : (byte)0);
                accessor.WriteArray(6, payload, 9, 32);
                for (int i = 41; i < payload.Length; i++) accessor.Write(134 + i - 41, payload[i]);
            }
        }

        public void TriggerFunction(byte function)
        {
            lock (gate)
            {
                accessor.Write(function == 1 ? 136 : 137, (byte)1);
            }
        }

        public byte[] ReadLeds()
        {
            byte[] leds = new byte[96];
            lock (gate)
            {
                accessor.ReadArray(38, leds, 0, leds.Length);
            }
            return leds;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                ClearInputs();
                disposed = true;
                accessor.Dispose();
                mapping.Dispose();
            }
        }
    }
}
