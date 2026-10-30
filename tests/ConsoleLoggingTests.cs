using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenithmLan.Tests
{
    internal static class ConsoleLoggingTests
    {
        public static void RunAll()
        {
            BlockedOutputDoesNotBlockProducer();
            DisposalHasBoundedWait();
            BrokenOutputDoesNotFailProducer();
            NormalOutputPreservesOrderAndWriterOwnership();
            PersistentFileContinuesWhileConsoleIsBlocked();
            InvalidFilePathLetsCallerFallBack();
        }

        private static void BlockedOutputDoesNotBlockProducer()
        {
            using (BlockingWriter writer = new BlockingWriter())
            {
                AsyncConsoleLog log = new AsyncConsoleLog(writer);
                Task producer = null;
                bool completed = false;
                try
                {
                    log.Write("block the output worker");
                    Assert(writer.Entered.WaitOne(1500), "output worker starts within timeout");
                    producer = Task.Factory.StartNew(delegate
                    {
                        for (int i = 0; i < 1500; i++) log.Write("queued event " + i);
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    completed = producer.Wait(1000);
                }
                finally
                {
                    writer.Release.Set();
                    if (producer != null) Assert(producer.Wait(2000), "producer finishes after releasing output");
                    log.Dispose();
                    if (producer != null) producer.Dispose();
                }
                Assert(completed, "1500 log writes must finish while output is blocked");
                log.Write("ignored after disposal");
            }
        }

        private static void DisposalHasBoundedWait()
        {
            using (BlockingWriter writer = new BlockingWriter())
            {
                AsyncConsoleLog log = new AsyncConsoleLog(writer);
                Task disposal = null;
                bool completed = false;
                try
                {
                    log.Write("hold the only output operation");
                    Assert(writer.Entered.WaitOne(1500), "output worker blocks before disposal");
                    disposal = Task.Factory.StartNew(log.Dispose, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    completed = disposal.Wait(1200);
                }
                finally
                {
                    writer.Release.Set();
                    if (disposal != null) Assert(disposal.Wait(2000), "disposal completes after releasing output");
                    else log.Dispose();
                    Assert(writer.Finished.WaitOne(1500), "blocked output call unwinds");
                    if (disposal != null) disposal.Dispose();
                }
                Assert(completed, "Dispose must not wait indefinitely for blocked output");
                log.Write("ignored after bounded disposal");
            }
        }

        private static void BrokenOutputDoesNotFailProducer()
        {
            using (ThrowingWriter writer = new ThrowingWriter())
            {
                AsyncConsoleLog log = new AsyncConsoleLog(writer);
                Stopwatch elapsed = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++) log.Write("event to unavailable output " + i);
                Assert(elapsed.ElapsedMilliseconds < 1000, "broken output must not delay producers");
                log.Dispose();
                Assert(writer.Calls > 0, "failing output path was exercised");
                log.Write("ignored after disposal");
            }
        }

        private static void NormalOutputPreservesOrderAndWriterOwnership()
        {
            using (StringWriter writer = new StringWriter())
            {
                AsyncConsoleLog log = new AsyncConsoleLog(writer);
                log.Write("first message");
                log.Write("second message");
                log.Write("third message");
                log.Dispose();
                string output = writer.ToString();
                int first = output.IndexOf("first message", StringComparison.Ordinal);
                int second = output.IndexOf("second message", StringComparison.Ordinal);
                int third = output.IndexOf("third message", StringComparison.Ordinal);
                Assert(first >= 0 && second > first && third > second, "normal messages preserve enqueue order");
                Assert(output.StartsWith("[", StringComparison.Ordinal) && output.IndexOf(']') < first,
                    "output includes a timestamp before the message");
                writer.WriteLine("caller still owns this writer");
                log.Write("ignored after disposal");
                Assert(!writer.ToString().Contains("ignored after disposal"), "disposed logger ignores later writes");
            }
        }

        private static void PersistentFileContinuesWhileConsoleIsBlocked()
        {
            string directory = Path.Combine(Path.GetTempPath(), "BrokenithmLogTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "session.log");
            try
            {
                using (BlockingWriter writer = new BlockingWriter())
                {
                    AsyncConsoleLog log = new AsyncConsoleLog(writer, path);
                    try
                    {
                        Assert(log.FilePath == Path.GetFullPath(path), "persistent logger exposes its full path");
                        log.Write("block console while file remains available");
                        Assert(writer.Entered.WaitOne(1500), "console is blocked before persistent events");
                        for (int i = 0; i < 600; i++) log.Write("persistent event " + i);
                        log.Write("tail marker\r\nwith escaped newline");
                        Stopwatch timeout = Stopwatch.StartNew();
                        string content = String.Empty;
                        while (timeout.ElapsedMilliseconds < 3000)
                        {
                            content = ReadTrace(path);
                            if (content.Contains("tail marker\\r\\nwith escaped newline")) break;
                            Thread.Sleep(10);
                        }
                        Assert(content.Contains("tail marker\\r\\nwith escaped newline"),
                            "persistent file receives final marker while console remains blocked");
                        Assert(content.Contains("seq=") && content.Contains("elapsed=") && content.Contains("thread="),
                            "trace contains sequence, monotonic elapsed time and producer thread");
                    }
                    finally
                    {
                        writer.Release.Set();
                        log.Dispose();
                    }
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                Directory.Delete(directory);
            }
        }

        private static void InvalidFilePathLetsCallerFallBack()
        {
            string directory = Path.Combine(Path.GetTempPath(), "BrokenithmLogMissing-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "session.log");
            using (StringWriter output = new StringWriter())
            {
                bool failed = false;
                try
                {
                    using (AsyncConsoleLog unused = new AsyncConsoleLog(output, path)) { }
                }
                catch (IOException) { failed = true; }
                Assert(failed, "constructor exposes a file-open failure for caller fallback");
                using (AsyncConsoleLog fallback = new AsyncConsoleLog(output)) fallback.Write("console fallback");
                Assert(output.ToString().Contains("console fallback"), "console-only fallback remains usable");
            }
        }

        private static string ReadTrace(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private sealed class BlockingWriter : TextWriter
        {
            public readonly ManualResetEvent Entered = new ManualResetEvent(false);
            public readonly ManualResetEvent Release = new ManualResetEvent(false);
            public readonly ManualResetEvent Finished = new ManualResetEvent(false);

            public override Encoding Encoding { get { return Encoding.UTF8; } }

            public override void WriteLine(string value)
            {
                Entered.Set();
                Release.WaitOne();
                Finished.Set();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Release.Set();
                    Entered.Dispose();
                    Release.Dispose();
                    Finished.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class ThrowingWriter : TextWriter
        {
            private int calls;
            public int Calls { get { return Interlocked.CompareExchange(ref calls, 0, 0); } }
            public override Encoding Encoding { get { return Encoding.UTF8; } }

            public override void WriteLine(string value)
            {
                Interlocked.Increment(ref calls);
                throw new IOException("Simulated unavailable console.");
            }
        }
    }
}
