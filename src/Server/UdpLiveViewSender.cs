using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// Sends live view JPEG frames over UDP to the connected client.
/// Each datagram: [seqNo: 4 bytes LE][JPEG bytes].
/// For frames exceeding the fragment threshold, splits into chunks:
///   [seqNo: 4B LE][chunkIndex: 1B][chunkCount: 1B][JPEG chunk bytes]
/// </summary>
public class UdpLiveViewSender : IDisposable
{
    private readonly Camera _camera;
    private UdpClient? _udp;
    private IPEndPoint? _target;
    private Thread? _thread;
    private volatile bool _running;
    private uint _seqNo;

    /// <summary>
    /// Maximum payload per UDP datagram before fragmentation kicks in.
    /// Stay well under typical MTU to avoid IP fragmentation on most networks.
    /// 60000 bytes allows most JPEG frames to go unfragmented (they're usually 20-60 KB).
    /// </summary>
    private const int MAX_DATAGRAM_PAYLOAD = 60000;

    /// <summary>
    /// Maximum chunk data size when fragmenting (leaves room for the 6-byte header).
    /// </summary>
    private const int MAX_CHUNK_DATA = MAX_DATAGRAM_PAYLOAD - 6;

    /// <summary>
    /// Target frame interval in milliseconds. ~30 fps = 33ms.
    /// Actual rate is limited by camera preview speed.
    /// </summary>
    public int TargetFrameIntervalMs { get; set; } = 33;

    public bool IsStreaming => _running;

    public UdpLiveViewSender(Camera camera)
    {
        _camera = camera;
    }

    /// <summary>
    /// Start streaming live view frames to the given client endpoint.
    /// </summary>
    public void Start(IPAddress clientIp, int clientPort)
    {
        if (_running) return;

        _target = new IPEndPoint(clientIp, clientPort);
        _udp = new UdpClient();
        _seqNo = 0;
        _running = true;

        _thread = new Thread(StreamLoop) { IsBackground = true, Name = "UdpLiveView" };
        _thread.Start();

        Logger.Notice($"UDP live view streaming started → {_target}");
    }

    /// <summary>
    /// Stop streaming.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(2000);
        _udp?.Dispose();
        _udp = null;
        _target = null;
        Logger.Notice("UDP live view streaming stopped.");
    }

    public void Dispose()
    {
        Stop();
    }

    private void StreamLoop()
    {
        while (_running)
        {
            try
            {
                byte[] frame = _camera.GetLiveViewBytes();
                if (frame.Length == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                _seqNo++;
                SendFrame(frame, _seqNo);

                if (TargetFrameIntervalMs > 0)
                    Thread.Sleep(TargetFrameIntervalMs);
            }
            catch (Exception ex)
            {
                if (!_running) break;
                Logger.Error($"UDP live view error: {ex.Message}");

                if (ex.Message.Contains("GP_ERROR_IO_USB_FIND") || ex.Message.Contains("GP_ERROR_MODEL_NOT_FOUND"))
                {
                    Logger.Notice("Camera lost during live view. Stopping stream.");
                    _running = false;
                    break;
                }

                Thread.Sleep(100);
            }
        }
    }

    private void SendFrame(byte[] jpeg, uint seqNo)
    {
        if (_udp == null || _target == null) return;

        // Small enough for a single datagram (with 4-byte header)
        if (jpeg.Length + 4 <= MAX_DATAGRAM_PAYLOAD)
        {
            var datagram = new byte[4 + jpeg.Length];
            BitConverter.GetBytes(seqNo).CopyTo(datagram, 0); // LE
            jpeg.CopyTo(datagram, 4);
            _udp.Send(datagram, datagram.Length, _target);
        }
        else
        {
            // Fragment into chunks
            int chunkCount = (jpeg.Length + MAX_CHUNK_DATA - 1) / MAX_CHUNK_DATA;
            if (chunkCount > 255) chunkCount = 255; // Safety cap

            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * MAX_CHUNK_DATA;
                int len = Math.Min(MAX_CHUNK_DATA, jpeg.Length - offset);

                var datagram = new byte[6 + len];
                BitConverter.GetBytes(seqNo).CopyTo(datagram, 0);
                datagram[4] = (byte)i;
                datagram[5] = (byte)chunkCount;
                Buffer.BlockCopy(jpeg, offset, datagram, 6, len);

                _udp.Send(datagram, datagram.Length, _target);
            }
        }
    }
}
