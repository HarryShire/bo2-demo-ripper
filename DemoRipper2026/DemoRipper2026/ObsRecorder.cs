using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DemoRipper2026
{
    /// <summary>
    /// Records through OBS over obs-websocket v5, which ships inside OBS 28+.
    /// Works with OBS 32.1.1 and obs-websocket 5.7.3.
    ///
    /// Protocol reference:
    ///   https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md
    ///
    /// Framework-only on purpose (ClientWebSocket is in System.dll, JSON in
    /// System.Web.Extensions): OBS recording needs no NuGet package.
    /// </summary>
    public sealed class ObsRecorder : IRecorder
    {
        // protocol.md, "OpCode Types"
        private const int OpHello = 0;
        private const int OpIdentify = 1;
        private const int OpIdentified = 2;
        private const int OpEvent = 5;
        private const int OpRequest = 6;
        private const int OpRequestResponse = 7;

        private const int RpcVersion = 1;
        private const int SubscribeOutputs = 1 << 6;   // EventSubscription::Outputs

        // protocol.md, "RequestStatus"
        private const int CodeOutputNotRunning = 501;

        // protocol.md, "WebSocketCloseCode"
        private const int CloseAuthenticationFailed = 4009;
        private const int CloseUnsupportedRpcVersion = 4010;

        // protocol.md, "ObsOutputState" — the strings RecordStateChanged carries.
        private const string StateStarted = "OBS_WEBSOCKET_OUTPUT_STARTED";
        private const string StatePaused = "OBS_WEBSOCKET_OUTPUT_PAUSED";
        private const string StateResumed = "OBS_WEBSOCKET_OUTPUT_RESUMED";
        private const string StateStopping = "OBS_WEBSOCKET_OUTPUT_STOPPING";
        private const string StateStopped = "OBS_WEBSOCKET_OUTPUT_STOPPED";

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Where OBS gets the record folder and file name. Each is read fresh
        /// when an output starts — SimpleOutput::ConfigureRecording reads
        /// SimpleOutput/FilePath, AdvancedOutput::StartRecording reads
        /// AdvOut/RecFilePath (FFFilePath for a custom FFmpeg output), and both
        /// read Output/FilenameFormatting. See obs-studio
        /// frontend/utility/SimpleOutput.cpp and AdvancedOutput.cpp.
        ///
        /// Not SetRecordDirectory: it refuses while recording is active, so it
        /// can't be used to put the user's folder back once OBS has started.
        /// </summary>
        private static readonly string[][] PathKeys =
        {
            new[] { "SimpleOutput", "FilePath" },
            new[] { "AdvOut", "RecFilePath" },
            new[] { "AdvOut", "FFFilePath" }
        };
        private static readonly string[] NameKey = { "Output", "FilenameFormatting" };

        private class Pending
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public Dictionary<string, object> Response;
        }

        private readonly string _host;
        private readonly int _port;
        private readonly string _password;

        private readonly object _connectLock = new object();
        private readonly object _sendLock = new object();
        private readonly object _stateLock = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();

        private volatile ClientWebSocket _ws;
        private string _recordState = "";
        private string _currentPath;
        private string _lastStoppedPath;
        private volatile string _lastWarning;

        public string ObsVersion { get; private set; }
        public string WebSocketVersion { get; private set; }

        public ObsRecorder(string host, int port, string password)
        {
            _host = host;
            _port = port;
            _password = password ?? "";
            ObsVersion = "";
            WebSocketVersion = "";
        }

        public string Name { get { return "OBS"; } }

        public string LastWarning { get { return _lastWarning; } }

        public bool UsesSettings(int port, string password)
        {
            return _port == port && _password == (password ?? "");
        }

        public bool IsConnected
        {
            get
            {
                ClientWebSocket ws = _ws;
                return ws != null && ws.State == WebSocketState.Open;
            }
        }

        /// <summary>Tracks OBS's own state, so stopping from OBS's hotkey shows up too.</summary>
        public bool IsRecording
        {
            get
            {
                lock (_stateLock)
                {
                    return IsConnected
                        && (_recordState == StateStarted || _recordState == StatePaused
                            || _recordState == StateResumed || _recordState == StateStopping);
                }
            }
        }

        /// <summary>The file OBS is writing right now, from the STARTED event.</summary>
        public string CurrentPath { get { lock (_stateLock) return _currentPath; } }

        /// <summary>The finished file from the last STOPPED event, however it was stopped.</summary>
        public string LastStoppedPath { get { lock (_stateLock) return _lastStoppedPath; } }

        // ================= RECORDING =================

        public void Preflight()
        {
            EnsureConnected();

            // A real round trip rather than the socket's state: a hung OBS can
            // keep the socket open and never answer.
            Dictionary<string, object> status = Request("GetRecordStatus", null);
            lock (_stateLock)
            {
                _recordState = !IsTrue(status, "outputActive") ? StateStopped
                             : IsTrue(status, "outputPaused") ? StatePaused
                             : StateStarted;
            }

            if (IsTrue(status, "outputActive"))
                throw new ObsException("OBS is already recording. Stop it in OBS first — "
                                       + "DemoRipper won't take over a recording it didn't start.");
        }

        /// <summary>OBS starts in a few hundred ms and has nothing to build ahead.</summary>
        public void Prepare(string pathWithoutExtension) { }

        public void Cancel() { }

        public void Start(string pathWithoutExtension)
        {
            string folder = Path.GetDirectoryName(pathWithoutExtension);
            // OBS expands %-specifiers (%CCYY, %hh…) in the file name format.
            string name = Path.GetFileName(pathWithoutExtension).Replace('%', '-');
            if (string.IsNullOrEmpty(folder) || name.Length == 0)
                throw new ArgumentException("Start needs a folder and a file name.");

            _lastWarning = null;
            Directory.CreateDirectory(folder);
            EnsureConnected();

            if (IsTrue(Request("GetRecordStatus", null), "outputActive"))
                throw new ObsException("OBS is already recording. Stop it in OBS first — "
                                       + "DemoRipper won't take over a recording it didn't start.");

            var saved = new List<KeyValuePair<string[], string>>();
            foreach (string[] key in PathKeys)
                saved.Add(new KeyValuePair<string[], string>(key, GetProfileParameter(key)));
            saved.Add(new KeyValuePair<string[], string>(NameKey, GetProfileParameter(NameKey)));

            try
            {
                foreach (string[] key in PathKeys) SetProfileParameter(key, folder);
                SetProfileParameter(NameKey, name);

                lock (_stateLock) { _recordState = ""; _currentPath = null; _lastStoppedPath = null; }
                Request("StartRecord", null);

                if (!WaitForState(StateStarted, StartTimeout))
                    throw new ObsException("OBS accepted the start command but never reported that recording began.");
            }
            finally
            {
                // OBS read these when the output started, so putting them back
                // now leaves this recording where it is and the profile as it was.
                RestoreProfile(saved);
            }
        }

        public string Stop()
        {
            EnsureConnected();

            string reported;
            try
            {
                reported = NormalizePath(Get(Request("StopRecord", null), "outputPath") as string);
            }
            catch (ObsException ex) when (ex.Code == CodeOutputNotRunning)
            {
                // Already stopped from inside OBS.
                return LastStoppedPath;
            }

            // StopRecord answers before the file is closed (RequestHandler_Record.cpp:
            // "TODO: Call signal directly to perform blocking wait"). STOPPED only
            // arrives once the muxer has finished, which is when moving it is safe.
            if (!WaitForState(StateStopped, StopTimeout))
                throw new ObsException("OBS is still finishing the file after "
                                       + StopTimeout.TotalSeconds + " seconds. Check OBS.");

            string stopped = LastStoppedPath ?? reported;

            // Auto-remux is a second job, and STOPPED is not its verdict.
            string note;
            string remuxed = WaitForRemux(stopped, AppearMs, CompleteMs,
                                          () => Environment.TickCount, Thread.Sleep, out note);
            if (note != null) _lastWarning = note;
            if (remuxed == null) return stopped;

            lock (_stateLock) _lastStoppedPath = remuxed;
            return remuxed;
        }

        // How long to give the remux. It starts within a moment of the file closing,
        // so a short wait separates "auto-remux is off" from "it is working on it";
        // once the file exists we wait longer, because a long recording takes longer.
        private const int AppearMs = 2500;
        private const int CompleteMs = 10000;
        private const int PollMs = 150;

        /// <summary>Containers OBS offers to remux into MP4 after recording.</summary>
        private static readonly string[] Remuxable = { ".mkv", ".flv", ".mov", ".ts", ".m3u8" };

        /// <summary>
        /// Waits for OBS's auto-remux, and says which way it went.
        ///
        /// The remux has no verdict to wait on: obs-websocket 5.x has no remuxing
        /// output state, and RecordFileChanged is about split recording, not this
        /// (protocol.md). The file is the only observable, so this waits for the
        /// sibling .mp4 to appear and stop growing.
        ///
        /// Returns the remuxed file once it is finished, otherwise null. <paramref
        /// name="note"/> is a line for the take log whenever the answer is not the
        /// plain "there was no remux", so a file left behind is never silent.
        /// </summary>
        public static string WaitForRemux(string recordedPath, int appearMs, int completeMs,
                                          Func<int> nowMs, Action<int> sleep, out string note)
        {
            note = null;
            if (string.IsNullOrEmpty(recordedPath)) return null;

            string ext = Path.GetExtension(recordedPath);
            if (Array.IndexOf(Remuxable, ext.ToLowerInvariant()) < 0) return null;

            string target = Path.ChangeExtension(recordedPath, ".mp4");
            if (string.Equals(target, recordedPath, StringComparison.OrdinalIgnoreCase)) return null;

            int start = nowMs();
            long lastSize = -1;
            int appeared = -1;

            while (true)
            {
                long size = -1;
                try { var f = new FileInfo(target); if (f.Exists) size = f.Length; }
                catch (IOException) { size = -1; }   // being written; treat as not settled

                if (size >= 0)
                {
                    if (appeared < 0) appeared = nowMs();
                    // Two equal readings a poll apart, and not still empty: the muxer is done.
                    if (size > 0 && size == lastSize)
                    {
                        note = "OBS remuxed the take to " + Path.GetFileName(target) + ".";
                        return target;
                    }
                    lastSize = size;
                }

                int waited = nowMs() - start;
                if (appeared < 0 && waited >= appearMs) return null;      // auto-remux is off: nothing to say
                if (appeared >= 0 && waited >= completeMs)
                {
                    note = "OBS is still remuxing " + Path.GetFileName(target) + " after "
                           + (completeMs / 1000) + " s, so only the recording itself was saved. "
                           + "The .mp4 stays in the takes folder — move it in by hand when OBS has finished.";
                    return null;
                }
                sleep(PollMs);
            }
        }

        public string GetRecordDirectory()
        {
            return Get(Request("GetRecordDirectory", null), "recordDirectory") as string ?? "";
        }

        private string GetProfileParameter(string[] key)
        {
            return Get(Request("GetProfileParameter", new Dictionary<string, object>
            {
                { "parameterCategory", key[0] },
                { "parameterName", key[1] }
            }), "parameterValue") as string;
        }

        /// <summary>Null deletes the value, which puts OBS back on its default.</summary>
        private void SetProfileParameter(string[] key, string value)
        {
            Request("SetProfileParameter", new Dictionary<string, object>
            {
                { "parameterCategory", key[0] },
                { "parameterName", key[1] },
                { "parameterValue", value }
            });
        }

        private void RestoreProfile(List<KeyValuePair<string[], string>> saved)
        {
            var failed = new List<string>();
            foreach (var kv in saved)
            {
                try { SetProfileParameter(kv.Key, kv.Value); }
                catch { failed.Add(kv.Key[0] + "/" + kv.Key[1]); }
            }

            if (failed.Count > 0)
                _lastWarning = "Couldn't put back OBS's " + string.Join(", ", failed)
                               + " — check the recording path in OBS Settings → Output.";
        }

        private bool WaitForState(string state, TimeSpan timeout)
        {
            DateTime until = DateTime.UtcNow + timeout;
            lock (_stateLock)
            {
                while (_recordState != state)
                {
                    if (!IsConnected) return false;
                    TimeSpan left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(_stateLock, left);
                }
                return true;
            }
        }

        // ================= CONNECTION =================

        /// <summary>No-op when already connected. Safe to call before every operation.</summary>
        public void EnsureConnected()
        {
            lock (_connectLock)
            {
                if (IsConnected) return;
                Teardown();

                Uri uri = new Uri("ws://" + _host + ":" + _port.ToString(CultureInfo.InvariantCulture));
                var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("obswebsocket.json");

                try { Await(ws.ConnectAsync(uri, CancellationToken.None), ConnectTimeout); }
                catch (Exception ex)
                {
                    ws.Dispose();
                    throw new ObsException("Couldn't reach OBS at " + uri + ". Is OBS open, with "
                                           + "Tools → WebSocket Server Settings → Enable WebSocket server ticked? ("
                                           + ex.Message + ")");
                }

                try
                {
                    Handshake(ws);
                }
                catch (Exception ex)
                {
                    ws.Abort();
                    ws.Dispose();
                    if (ex is ObsException) throw;
                    throw new ObsException("OBS didn't finish the WebSocket handshake: " + ex.Message);
                }

                _ws = ws;
                Thread reader = new Thread(() => ReadLoop(ws));
                reader.IsBackground = true;
                reader.Name = "OBS websocket";
                reader.Start();

                // OBS may already have been recording before we connected.
                Dictionary<string, object> status = Request("GetRecordStatus", null);
                lock (_stateLock)
                {
                    _recordState = !IsTrue(status, "outputActive") ? StateStopped
                                 : IsTrue(status, "outputPaused") ? StatePaused
                                 : StateStarted;
                }
            }
        }

        /// <summary>protocol.md, "Connection steps": Hello → Identify → Identified.</summary>
        private void Handshake(ClientWebSocket ws)
        {
            Dictionary<string, object> hello = ReadHandshakeMessage(ws, OpHello);
            ObsVersion = Get(hello, "obsStudioVersion") as string ?? "";
            WebSocketVersion = Get(hello, "obsWebSocketVersion") as string ?? "";

            var identify = new Dictionary<string, object>
            {
                { "rpcVersion", RpcVersion },
                { "eventSubscriptions", SubscribeOutputs }
            };

            var auth = Get(hello, "authentication") as Dictionary<string, object>;
            if (auth != null)
            {
                if (_password.Length == 0)
                    throw new ObsException("OBS wants a WebSocket password. Copy it from "
                                           + "Tools → WebSocket Server Settings → Show Connect Info.");

                // protocol.md, "Creating an authentication string"
                string secret = Sha256Base64(_password + (Get(auth, "salt") as string));
                identify["authentication"] = Sha256Base64(secret + (Get(auth, "challenge") as string));
            }

            Send(ws, OpIdentify, identify);
            ReadHandshakeMessage(ws, OpIdentified);
        }

        private static Dictionary<string, object> ReadHandshakeMessage(ClientWebSocket ws, int expectedOp)
        {
            string text = Receive(ws, ConnectTimeout);
            if (text == null)
            {
                int code = ws.CloseStatus.HasValue ? (int)ws.CloseStatus.Value : 0;
                if (code == CloseAuthenticationFailed)
                    throw new ObsException("OBS rejected the WebSocket password. Copy it again from "
                                           + "Tools → WebSocket Server Settings → Show Connect Info.");
                if (code == CloseUnsupportedRpcVersion)
                    throw new ObsException("This OBS needs a newer obs-websocket RPC version than "
                                           + RpcVersion + ".");
                throw new ObsException("OBS closed the connection (" + code + " "
                                       + ws.CloseStatusDescription + ").");
            }

            var msg = FromJson(text);
            if (msg == null || ToInt(Get(msg, "op")) != expectedOp)
                throw new ObsException("Unexpected message from OBS while connecting.");
            return Get(msg, "d") as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private void ReadLoop(ClientWebSocket ws)
        {
            try
            {
                string text;
                while ((text = Receive(ws, Timeout.InfiniteTimeSpan)) != null)
                {
                    // One malformed message shouldn't take the connection down.
                    try { Dispatch(text); }
                    catch { }
                }
            }
            catch { }   // socket dropped
            finally
            {
                if (ws == _ws)
                {
                    lock (_pending) foreach (Pending p in _pending.Values) p.Done.Set();
                }
                lock (_stateLock) Monitor.PulseAll(_stateLock);
            }
        }

        private void Dispatch(string text)
        {
            var msg = FromJson(text);
            if (msg == null) return;

            int op = ToInt(Get(msg, "op"));
            var d = Get(msg, "d") as Dictionary<string, object>;
            if (d == null) return;

            if (op == OpRequestResponse)
            {
                string id = Get(d, "requestId") as string ?? "";
                lock (_pending)
                {
                    Pending p;
                    if (_pending.TryGetValue(id, out p)) { p.Response = d; p.Done.Set(); }
                }
            }
            else if (op == OpEvent && (Get(d, "eventType") as string) == "RecordStateChanged")
            {
                var data = Get(d, "eventData") as Dictionary<string, object>;
                string state = Get(data, "outputState") as string ?? "";
                string path = NormalizePath(Get(data, "outputPath") as string);

                lock (_stateLock)
                {
                    _recordState = state;
                    if (!string.IsNullOrEmpty(path))
                    {
                        if (state == StateStarted) _currentPath = path;
                        else if (state == StateStopped) _lastStoppedPath = path;
                    }
                    Monitor.PulseAll(_stateLock);
                }
            }
        }

        private Dictionary<string, object> Request(string type, Dictionary<string, object> data)
        {
            EnsureConnected();

            string id = Guid.NewGuid().ToString("N");
            Pending p = new Pending();
            lock (_pending) _pending[id] = p;

            try
            {
                var d = new Dictionary<string, object> { { "requestType", type }, { "requestId", id } };
                if (data != null) d["requestData"] = data;
                Send(_ws, OpRequest, d);

                if (!p.Done.Wait(RequestTimeout))
                    throw new ObsException("OBS didn't answer " + type + ".");
                if (p.Response == null)
                    throw new ObsException("Lost the connection to OBS during " + type + ".");

                var status = Get(p.Response, "requestStatus") as Dictionary<string, object>;
                if (!IsTrue(status, "result"))
                {
                    int code = ToInt(Get(status, "code"));
                    string comment = Get(status, "comment") as string;
                    throw new ObsException(type + " failed (code " + code
                                           + (comment != null ? ": " + comment : "") + ").", code);
                }

                return Get(p.Response, "responseData") as Dictionary<string, object>
                       ?? new Dictionary<string, object>();
            }
            finally
            {
                lock (_pending) _pending.Remove(id);
            }
        }

        private void Send(ClientWebSocket ws, int op, Dictionary<string, object> d)
        {
            if (ws == null) throw new ObsException("Not connected to OBS.");

            byte[] bytes = Encoding.UTF8.GetBytes(ToJson(new Dictionary<string, object> { { "op", op }, { "d", d } }));

            // ClientWebSocket supports one send at a time.
            lock (_sendLock)
                Await(ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                                   CancellationToken.None), RequestTimeout);
        }

        /// <summary>One whole text message, or null once the server has closed.</summary>
        private static string Receive(ClientWebSocket ws, TimeSpan timeout)
        {
            byte[] buf = new byte[16 * 1024];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult r = Await(
                        ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None), timeout);
                    if (r.MessageType == WebSocketMessageType.Close) return null;

                    ms.Write(buf, 0, r.Count);
                    if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
            }
        }

        public void Dispose()
        {
            lock (_connectLock) Teardown();
        }

        private void Teardown()
        {
            ClientWebSocket ws = _ws;
            _ws = null;
            if (ws == null) return;

            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000);
            }
            catch { }
            try { ws.Abort(); ws.Dispose(); }
            catch { }
        }

        // ================= HELPERS =================

        private static void Await(Task t, TimeSpan timeout)
        {
            try
            {
                if (!t.Wait(timeout)) throw new TimeoutException("timed out");
            }
            catch (AggregateException ex)
            {
                throw ex.GetBaseException();
            }
        }

        private static T Await<T>(Task<T> t, TimeSpan timeout)
        {
            Await((Task)t, timeout);
            return t.Result;
        }

        /// <summary>
        /// OBS joins the record folder and file name with '/', so paths arrive as
        /// "C:\clips\_takes/take 2026-….mp4". Normalised here so callers can compare
        /// them with paths from System.IO.
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static string Sha256Base64(string s)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        // JavaScriptSerializer isn't documented as thread-safe, and the reader
        // thread parses while request threads serialise, so one per call.
        private static string ToJson(object o)
        {
            return new JavaScriptSerializer().Serialize(o);
        }

        private static Dictionary<string, object> FromJson(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .DeserializeObject(text) as Dictionary<string, object>;
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static bool IsTrue(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v is bool && (bool)v;
        }

        private static int ToInt(object v)
        {
            return v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
    }

    public sealed class ObsException : Exception
    {
        /// <summary>obs-websocket RequestStatus code, or 0 when it isn't from a request.</summary>
        public readonly int Code;

        public ObsException(string message, int code = 0) : base(message)
        {
            Code = code;
        }
    }
}
