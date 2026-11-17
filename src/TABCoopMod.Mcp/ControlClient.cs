using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TABCoopMod.Mcp
{
    /// <summary>
    /// Blocking client for the in-process TABCoopMod control server (loopback TCP, newline JSON).
    /// Reconnects lazily; one request/response at a time (serialized by the caller).
    /// </summary>
    public sealed class ControlClient : IDisposable
    {
        private readonly int _port;
        private readonly object _lock = new object();
        private TcpClient _client;
        private StreamReader _reader;
        private StreamWriter _writer;

        public ControlClient(int port)
        {
            _port = port;
        }

        public int Port => _port;

        public string Send(long id, string tool, JsonNode args)
        {
            lock (_lock)
            {
                EnsureConnected();

                var request = new JsonObject
                {
                    ["id"] = id,
                    ["tool"] = tool,
                    // Deep-clone: the args node is still parented to the incoming request document.
                    ["args"] = args?.DeepClone() ?? new JsonObject()
                };

                _writer.WriteLine(request.ToJsonString());
                string line = _reader.ReadLine();
                if (line == null)
                {
                    CloseConnection();
                    throw new IOException("control server closed the connection");
                }
                return line;
            }
        }

        private void EnsureConnected()
        {
            if (_client != null && _client.Connected) return;
            CloseConnection();

            var client = new TcpClient { NoDelay = true, ReceiveTimeout = 15000 };
            client.Connect("127.0.0.1", _port);
            _client = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        private void CloseConnection()
        {
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _reader = null;
            _writer = null;
            _client = null;
        }

        public void Dispose() => CloseConnection();
    }
}
