using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenithmLan
{
    internal sealed class LanBridge
    {
        private readonly BridgeOptions options;
        private readonly SharedMemoryState state;
        private readonly Action<string> log;
        private readonly IDeviceDiscovery discovery;
        private readonly Action<DiscoveredDevice> onConnected;
        private string preferredInstance;
        private bool sawConnectionRefused;

        public LanBridge(BridgeOptions options, SharedMemoryState state, Action<string> log)
            : this(options, state, log, new MdnsDiscovery(options.DiscoveryTimeoutMs, log)) { }

        internal LanBridge(BridgeOptions options, SharedMemoryState state, Action<string> log, IDeviceDiscovery discovery)
            : this(options, state, log, discovery, null) { }

        internal LanBridge(BridgeOptions options, SharedMemoryState state, Action<string> log, IDeviceDiscovery discovery,
            Action<DiscoveredDevice> onConnected)
        {
            this.options = options;
            this.state = state;
            this.log = log;
            this.discovery = discovery;
            this.onConnected = onConnected;
        }

        public void Run(CancellationToken token)
        {
            state.ClearInputs();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    IList<DiscoveredDevice> candidates = GetCandidates(token);
                    if (candidates.Count == 0)
                    {
                        log("No matching iPad found. Keep Brokenithm open, allow local network access, or use --ip <iPad IP>.");
                        if (sawConnectionRefused) log("Earlier discovery succeeded but TCP was refused. Fully quit and reopen the iPad app to recreate its listener.");
                    }
                    foreach (DiscoveredDevice candidate in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        bool established = false;
                        Stopwatch attempt = Stopwatch.StartNew();
                        try
                        {
                            log("Connecting to " + candidate.Name + " at " + new IPEndPoint(candidate.Address, candidate.Port) + " ...");
                            using (TcpClient client = Connect(candidate.Address, candidate.Port, token))
                            {
                                RunSession(client, token, delegate
                                {
                                    established = true;
                                    sawConnectionRefused = false;
                                    preferredInstance = candidate.InstanceName;
                                    if (onConnected != null) onConnected(candidate);
                                });
                            }
                        }
                        catch (Exception error)
                        {
                            if (!IsConnectionError(error)) throw;
                            if (!token.IsCancellationRequested)
                            {
                                SocketException socketError = error as SocketException;
                                log("Connection ended after " + attempt.ElapsedMilliseconds + " ms: " + error.GetType().Name +
                                    (socketError == null ? String.Empty : " SocketError=" + socketError.SocketErrorCode + " NativeError=" + socketError.NativeErrorCode) +
                                    "; " + error.Message);
                                if (socketError != null && socketError.SocketErrorCode == SocketError.ConnectionRefused)
                                {
                                    sawConnectionRefused = true;
                                    log("Service discovery succeeded; the advertised TCP endpoint refused the connection. Fully quit Brokenithm from the iPad app switcher, reopen it and keep it in the foreground. Restart Connection may not recreate an unused listener.");
                                }
                            }
                        }
                        finally
                        {
                            state.ClearInputs();
                        }
                        if (established) break;
                    }
                }
                catch (Exception error)
                {
                    if (!IsConnectionError(error)) throw;
                    if (!token.IsCancellationRequested) log("Discovery/connection failed: " + error.Message + " Use --ip if mDNS is blocked.");
                }
                finally
                {
                    state.ClearInputs();
                }
                if (!token.IsCancellationRequested)
                {
                    log("Inputs released; retrying in " + options.RetryDelayMs + " ms.");
                    token.WaitHandle.WaitOne(options.RetryDelayMs);
                }
            }
        }

        private IList<DiscoveredDevice> GetCandidates(CancellationToken token)
        {
            if (!String.IsNullOrEmpty(options.Host))
            {
                return new DiscoveredDevice[] { new DiscoveredDevice {
                    Name = "manual iPad", InstanceName = "manual", Address = IPAddress.Parse(options.Host), Port = options.Port
                } };
            }
            IList<DiscoveredDevice> found = discovery.Find(token);
            List<DiscoveredDevice> matches = new List<DiscoveredDevice>();
            foreach (DiscoveredDevice device in found)
            {
                if (options.DeviceName != null &&
                    !String.Equals(options.DeviceName, device.Name, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(options.DeviceName.TrimEnd('.'), device.InstanceName, StringComparison.OrdinalIgnoreCase)) continue;
                if (preferredInstance != null && !String.Equals(preferredInstance, device.InstanceName, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(device);
            }
            if (matches.Count > 0)
            {
                HashSet<string> instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DiscoveredDevice match in matches) instances.Add(match.InstanceName);
                log("Found " + instances.Count + " device(s), " + matches.Count + " address(es). Use --device <name> to choose a device.");
                foreach (DiscoveredDevice match in matches)
                {
                    log("Discovered: " + match.Name + " at " + new IPEndPoint(match.Address, match.Port) + "; instance=" + match.InstanceName + "; interface=" + match.InterfaceIndex);
                }
            }
            else if (preferredInstance != null)
            {
                log("Waiting for the previously connected iPad: " + preferredInstance);
            }
            return matches;
        }

        private static bool IsConnectionError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is SocketException ||
                error is TimeoutException || error is ObjectDisposedException || error is OperationCanceledException;
        }

        private TcpClient Connect(IPAddress address, int port, CancellationToken token)
        {
            TcpClient client = new TcpClient(address.AddressFamily);
            try
            {
                IAsyncResult operation = client.BeginConnect(address, port, null, null);
                using (WaitHandle completed = operation.AsyncWaitHandle)
                {
                    int result = WaitHandle.WaitAny(new WaitHandle[] { completed, token.WaitHandle }, options.ConnectTimeoutMs);
                    if (result == WaitHandle.WaitTimeout) throw new TimeoutException("TCP connect timed out.");
                    token.ThrowIfCancellationRequested();
                    client.EndConnect(operation);
                }
                client.NoDelay = true;
                client.SendTimeout = 2000;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                byte[] keepAlive = new byte[12];
                Buffer.BlockCopy(BitConverter.GetBytes((uint)1), 0, keepAlive, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((uint)2000), 0, keepAlive, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((uint)1000), 0, keepAlive, 8, 4);
                client.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
                return client;
            }
            catch
            {
                client.Close();
                throw;
            }
        }

        private void RunSession(TcpClient client, CancellationToken token, Action connected)
        {
            using (NetworkStream stream = client.GetStream())
            using (CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (CancellationTokenRegistration registration = session.Token.Register(client.Close))
            {
                Task writer = null;
                SessionStatistics statistics = new SessionStatistics();
                Stopwatch handshake = Stopwatch.StartNew();
                try
                {
                    session.CancelAfter(options.HandshakeTimeoutMs);
                    if (!Protocol.IsWelcome(Protocol.ReadFrame(stream)))
                    {
                        throw new InvalidDataException("Expected the WEL greeting from Brokenithm on the iPad.");
                    }
                    session.CancelAfter(Timeout.Infinite);
                    session.Token.ThrowIfCancellationRequested();
                    log("WEL greeting accepted after " + handshake.ElapsedMilliseconds + " ms.");
                    connected();
                    log("Connected to iPad. Start the game; press Ctrl+C to stop.");
                    writer = Task.Factory.StartNew(delegate { MonitorSession(stream, session, statistics); },
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    bool airEnabled = true;
                    while (!session.IsCancellationRequested)
                    {
                        byte[] payload = Protocol.ReadFrame(stream);
                        session.Token.ThrowIfCancellationRequested();
                        Protocol.ApplyFrame(payload, state, ref airEnabled);
                        Interlocked.Increment(ref statistics.ReceivedFrames);
                        Interlocked.Exchange(ref statistics.LastReceiveTick, Stopwatch.GetTimestamp());
                        if (payload[0] == (byte)'I') Interlocked.Increment(ref statistics.InputFrames);
                        if (payload[0] == (byte)'A') log("AIR setting changed: " + airEnabled);
                        if (payload[0] == (byte)'F')
                        {
                            Interlocked.Increment(ref statistics.FunctionFrames);
                            log(payload[3] == 1 ? "Insert Coin request forwarded." : "Tap Card request forwarded.");
                        }
                    }
                }
                finally
                {
                    session.Cancel();
                    if (writer != null) writer.GetAwaiter().GetResult();
                    LogStatistics("Session ended", statistics);
                }
            }
        }

        private void MonitorSession(NetworkStream stream, CancellationTokenSource session, SessionStatistics statistics)
        {
            byte[] previous = null;
            byte[] frame = new byte[100];
            frame[0] = 99;
            frame[1] = (byte)'L';
            frame[2] = (byte)'E';
            frame[3] = (byte)'D';
            Stopwatch sinceSend = Stopwatch.StartNew();
            Stopwatch sinceReport = Stopwatch.StartNew();
            try
            {
                while (!session.IsCancellationRequested)
                {
                    if (!options.DisableLed)
                    {
                        byte[] current = state.ReadLeds();
                        if (!Equal(previous, current) || sinceSend.ElapsedMilliseconds >= 500)
                        {
                            Buffer.BlockCopy(current, 0, frame, 4, current.Length);
                            stream.Write(frame, 0, frame.Length);
                            Interlocked.Increment(ref statistics.LedFrames);
                            previous = current;
                            sinceSend.Restart();
                        }
                    }
                    if (sinceReport.ElapsedMilliseconds >= 5000)
                    {
                        LogStatistics("Session heartbeat", statistics);
                        sinceReport.Restart();
                    }
                    session.Token.WaitHandle.WaitOne(options.DisableLed ? 100 : 10);
                }
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is SocketException) && !(error is ObjectDisposedException)) throw;
                if (!session.IsCancellationRequested) log("LED send failed: " + error.Message);
            }
            finally
            {
                session.Cancel();
            }
        }

        private void LogStatistics(string reason, SessionStatistics statistics)
        {
            long last = Interlocked.Read(ref statistics.LastReceiveTick);
            long idleMs = last == 0 ? -1 : (long)((Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency);
            log(reason + ": frames=" + Interlocked.Read(ref statistics.ReceivedFrames) +
                "; inputs=" + Interlocked.Read(ref statistics.InputFrames) +
                "; functions=" + Interlocked.Read(ref statistics.FunctionFrames) +
                "; ledSent=" + Interlocked.Read(ref statistics.LedFrames) + "; lastRxAgoMs=" + idleMs);
        }

        private sealed class SessionStatistics
        {
            public long ReceivedFrames;
            public long InputFrames;
            public long FunctionFrames;
            public long LedFrames;
            public long LastReceiveTick;
        }

        private static bool Equal(byte[] first, byte[] second)
        {
            if (first == null || first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }
    }
}
