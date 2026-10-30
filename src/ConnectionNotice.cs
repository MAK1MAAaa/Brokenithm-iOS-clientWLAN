using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace BrokenithmLan
{
    internal sealed class ConnectionNotice : IDisposable
    {
        private readonly object gate = new object();
        private readonly BlockingCollection<string> pending = new BlockingCollection<string>(4);
        private readonly TextWriter output;
        private readonly Thread worker;
        private bool disposed;

        public ConnectionNotice(TextWriter output)
        {
            this.output = output;
            worker = new Thread(WriteNotices) { IsBackground = true, Name = "Brokenithm connection notice" };
            worker.Start();
        }

        public void Connected(DiscoveredDevice device)
        {
            string name = device.Name.Replace("\r", " ").Replace("\n", " ");
            string message = "已连接 iPad：" + name + "（" + device.Address + "）";
            lock (gate)
            {
                if (disposed) return;
                if (pending.TryAdd(message)) return;
                string ignored;
                pending.TryTake(out ignored);
                pending.TryAdd(message);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                pending.CompleteAdding();
            }
            if (worker.Join(500)) pending.Dispose();
        }

        private void WriteNotices()
        {
            try
            {
                foreach (string message in pending.GetConsumingEnumerable()) output.WriteLine(message);
            }
            catch (Exception) { }
        }
    }
}
