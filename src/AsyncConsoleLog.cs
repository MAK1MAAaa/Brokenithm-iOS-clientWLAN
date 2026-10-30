using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BrokenithmLan
{
    internal sealed class AsyncConsoleLog : IDisposable
    {
        private readonly object producerGate = new object();
        private readonly BlockingCollection<string> pending = new BlockingCollection<string>(512);
        private readonly BlockingCollection<string> console = new BlockingCollection<string>(256);
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly TextWriter output;
        private readonly StreamWriter file;
        private readonly Thread dispatcher;
        private readonly Thread consoleWorker;
        private long sequence;
        private int disposed;

        public string FilePath { get; private set; }

        public AsyncConsoleLog(TextWriter output) : this(output, null) { }

        public AsyncConsoleLog(TextWriter output, string filePath)
        {
            if (output == null) throw new ArgumentNullException("output");
            this.output = output;
            if (filePath != null)
            {
                FilePath = Path.GetFullPath(filePath);
                file = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.ReadWrite), new UTF8Encoding(false));
            }

            dispatcher = new Thread(Dispatch) { IsBackground = true, Name = "Brokenithm trace dispatcher" };
            consoleWorker = new Thread(WriteConsole) { IsBackground = true, Name = "Brokenithm console output" };
            try
            {
                consoleWorker.Start();
                dispatcher.Start();
            }
            catch
            {
                pending.CompleteAdding();
                console.CompleteAdding();
                if (file != null) file.Dispose();
                throw;
            }
        }

        public void Write(string message)
        {
            if (Interlocked.CompareExchange(ref disposed, 0, 0) != 0) return;
            lock (producerGate)
            {
                if (disposed != 0) return;
                Offer(pending, Format(message));
            }
        }

        public void Dispose()
        {
            Stopwatch deadline = Stopwatch.StartNew();
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (producerGate) pending.CompleteAdding();
            bool dispatcherFinished = dispatcher.Join(Remaining(deadline));
            bool consoleFinished = consoleWorker.Join(Remaining(deadline));
            if (dispatcherFinished && consoleFinished)
            {
                pending.Dispose();
                console.Dispose();
            }
        }

        private string Format(string message)
        {
            long current = Interlocked.Increment(ref sequence);
            string clean = (message ?? String.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
            return String.Format(CultureInfo.InvariantCulture,
                "[{0:yyyy-MM-dd HH:mm:ss.fff zzz}] [seq={1} elapsed={2}ms thread={3}] {4}",
                DateTimeOffset.Now, current, elapsed.ElapsedMilliseconds, Thread.CurrentThread.ManagedThreadId, clean);
        }

        private void Dispatch()
        {
            bool fileFailed = false;
            try
            {
                foreach (string line in pending.GetConsumingEnumerable())
                {
                    string warning = null;
                    if (file != null && !fileFailed)
                    {
                        try
                        {
                            file.WriteLine(line);
                            file.Flush();
                        }
                        catch (Exception error)
                        {
                            fileFailed = true;
                            warning = Format("Persistent log write failed; console logging continues: " +
                                error.GetType().Name + ": " + error.Message);
                        }
                    }
                    Offer(console, line);
                    if (warning != null) Offer(console, warning);
                }
            }
            catch (Exception error)
            {
                Offer(console, Format("Log dispatcher stopped: " + error.GetType().Name + ": " + error.Message));
            }
            finally
            {
                if (file != null)
                {
                    try { file.Dispose(); }
                    catch (Exception) { }
                }
                console.CompleteAdding();
            }
        }

        private void WriteConsole()
        {
            try
            {
                foreach (string line in console.GetConsumingEnumerable()) output.WriteLine(line);
            }
            catch (Exception)
            {
                // Console failures must not stop persistent logging or network processing.
            }
        }

        private static void Offer(BlockingCollection<string> queue, string value)
        {
            if (queue.TryAdd(value)) return;
            string dropped;
            queue.TryTake(out dropped);
            queue.TryAdd(value);
        }

        private static int Remaining(Stopwatch deadline)
        {
            return (int)Math.Max(0, 500 - deadline.ElapsedMilliseconds);
        }
    }
}
