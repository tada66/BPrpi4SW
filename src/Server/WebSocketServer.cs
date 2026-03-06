using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Lightweight WebSocket server built on raw TcpListener.
/// Accepts a single client at a time (single-client model).
/// Text frames carry JSON messages; binary frames are not used (live view goes over UDP).
/// </summary>
public class WebSocketServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    private WebSocket? _ws;
    private TcpClient? _tcpClient;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _connectionLock = new();

    /// <summary>
    /// Fired when a text message is received from the client.
    /// </summary>
    public event Func<string, Task>? MessageReceived;

    /// <summary>
    /// Fired when a client connects. Provides the client's IP address.
    /// </summary>
    public event Action<IPAddress>? ClientConnected;

    /// <summary>
    /// Fired when the client disconnects.
    /// </summary>
    public event Action? ClientDisconnected;

    /// <summary>
    /// The connected client's remote IP, or null if no client is connected.
    /// </summary>
    public IPAddress? ClientAddress { get; private set; }

    /// <summary>
    /// Whether a client is currently connected.
    /// </summary>
    public bool HasClient => _ws?.State == WebSocketState.Open;

    public WebSocketServer(int port = 4400)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>
    /// Start listening for WebSocket connections.
    /// </summary>
    public void Start()
    {
        _listener.Start();
        Logger.Notice($"WebSocket server listening on port {_port}");
        _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
    }

    /// <summary>
    /// Send a JSON text message to the connected client.
    /// </summary>
    public async Task SendAsync(string json)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                _cts.Token
            );
        }
        catch (Exception ex)
        {
            Logger.Error($"WebSocket send error: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Send a Message object serialized as JSON.
    /// </summary>
    public Task SendMessageAsync(Message msg) => SendAsync(msg.Serialize());

    public void Dispose()
    {
        _cts.Cancel();
        CleanupConnection();
        _listener.Stop();
        _sendLock.Dispose();
        _cts.Dispose();
    }

    // ── Accept loop ──

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct);
                var remoteEp = tcp.Client.RemoteEndPoint as IPEndPoint;
                Logger.Info($"TCP connection from {remoteEp}");

                // Single-client: reject if one is already connected
                if (HasClient)
                {
                    Logger.Warn($"Rejecting connection from {remoteEp} — a client is already connected.");
                    tcp.Close();
                    continue;
                }

                // Attempt WebSocket handshake
                var stream = tcp.GetStream();
                if (!await TryHandshake(stream, ct))
                {
                    Logger.Warn($"WebSocket handshake failed from {remoteEp}");
                    tcp.Close();
                    continue;
                }

                // Create WebSocket from the upgraded stream
                var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(30)
                });

                lock (_connectionLock)
                {
                    _tcpClient = tcp;
                    _ws = ws;
                    ClientAddress = remoteEp?.Address;
                }

                Logger.Notice($"WebSocket client connected: {remoteEp}");
                ClientConnected?.Invoke(remoteEp!.Address);

                // Handle this client (blocks until disconnect)
                await HandleClient(ws, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Logger.Error($"Accept loop error: {ex.Message}");
            }
        }
    }

    // ── Client handling ──

    private async Task HandleClient(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024]; // 64 KB receive buffer
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();

                // Read a complete message (may span multiple frames)
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    Logger.Debug($"WS received: {text}");

                    if (MessageReceived != null)
                    {
                        try { await MessageReceived(text); }
                        catch (Exception ex) { Logger.Error($"Message handler error: {ex.Message}"); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Logger.Info($"WebSocket closed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Client handler error: {ex.Message}");
        }
        finally
        {
            Logger.Notice("WebSocket client disconnected.");
            CleanupConnection();
            ClientDisconnected?.Invoke();
        }
    }

    private void CleanupConnection()
    {
        lock (_connectionLock)
        {
            ClientAddress = null;

            if (_ws != null)
            {
                try { _ws.Dispose(); } catch { }
                _ws = null;
            }
            if (_tcpClient != null)
            {
                try { _tcpClient.Close(); } catch { }
                _tcpClient = null;
            }
        }
    }

    // ── HTTP WebSocket upgrade handshake ──

    private static async Task<bool> TryHandshake(NetworkStream stream, CancellationToken ct)
    {
        // Read HTTP request headers (up to 8 KB)
        var headerBuffer = new byte[8192];
        int totalRead = 0;
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (totalRead < headerBuffer.Length)
        {
            int read = await stream.ReadAsync(headerBuffer.AsMemory(totalRead, headerBuffer.Length - totalRead), linked.Token);
            if (read == 0) return false;
            totalRead += read;

            // Check for end of HTTP headers
            string partial = Encoding.ASCII.GetString(headerBuffer, 0, totalRead);
            if (partial.Contains("\r\n\r\n")) break;
        }

        string request = Encoding.ASCII.GetString(headerBuffer, 0, totalRead);

        // Verify this is a WebSocket upgrade request
        if (!request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            return false;

        // Extract Sec-WebSocket-Key
        var keyMatch = Regex.Match(request, @"Sec-WebSocket-Key:\s*(.+?)(\r\n|\r|\n)", RegexOptions.IgnoreCase);
        if (!keyMatch.Success) return false;

        string clientKey = keyMatch.Groups[1].Value.Trim();
        string acceptKey = ComputeAcceptKey(clientKey);

        // Send 101 Switching Protocols response
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
            "\r\n";

        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes, linked.Token);
        return true;
    }

    private static string ComputeAcceptKey(string clientKey)
    {
        const string magic = "258EAFA5-E914-47DA-95CA-5AB5DC76B98E";
        byte[] combined = Encoding.ASCII.GetBytes(clientKey + magic);
        byte[] hash = SHA1.HashData(combined);
        return Convert.ToBase64String(hash);
    }
}
