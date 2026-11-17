using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MioReel.Core.Abstractions;
using MioReel.Core.Models;

namespace MioReel.Infrastructure.Services.MediaProxy;

/// <summary>
/// 本地回环 HTTP Range 视频流代理与客户端 CENC/CBCS 解密服务。
/// 将需要自定义 HTTP 头及 decryption_key 的加密流透明转换为 FrameFlux 可直接播放的本地 MP4 地址。
/// </summary>
public sealed class LocalMediaProxyService : IMediaStreamProxyService
{
    private readonly ConcurrentDictionary<string, HttpMediaStreamSession> _sessions = new(StringComparer.Ordinal);
    private readonly HttpClient _mediaHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };
    private readonly object _startLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    private TcpListener? _listener;
    private int _port;
    private bool _disposed;

    public async Task<Uri> PreparePlayableUriAsync(ResolvedStreamInfo stream, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(stream.PlayUrl))
            throw new ArgumentException("PlayUrl 不能为空", nameof(stream));

        EnsureServerStarted();

        byte[]? key = null;
        if (!string.IsNullOrWhiteSpace(stream.DecryptionKey)
            && !string.Equals(stream.Encryption, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (!CencCrypto.TryParseKeyHex(stream.DecryptionKey, out key))
                throw new GatewayException("视频流解密密钥格式无效", failureKind: GatewayFailureKind.InvalidResponse);
        }

        var session = new HttpMediaStreamSession(_mediaHttp, stream.PlayUrl, stream.PlayHeaders, key);
        try
        {
            await session.InitializeAsync(ct);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = session;
        return new Uri($"http://127.0.0.1:{_port}/stream/{id}.mp4");
    }

    public void ReleaseSession(Uri? proxyUri)
    {
        if (proxyUri is null) return;
        var path = proxyUri.AbsolutePath;
        if (!path.StartsWith("/stream/", StringComparison.Ordinal)) return;
        var id = path["/stream/".Length..];
        if (id.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            id = id[..^4];

        if (_sessions.TryRemove(id, out var session))
            session.Dispose();
    }

    private void EnsureServerStarted()
    {
        if (_listener is not null) return;
        lock (_startLock)
        {
            if (_listener is not null) return;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _ = Task.Run(() => AcceptLoopAsync(listener, _shutdownCts.Token));
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var netStream = client.GetStream();

            while (!ct.IsCancellationRequested)
            {
                var req = await ReadHttpRequestAsync(netStream, ct);
                if (req is null)
                    break;

                var (method, path, rangeHeader, keepAlive) = req.Value;
                var session = ResolveSessionFromPath(path);
                if (session is null)
                {
                    await WriteSimpleResponseAsync(netStream, 404, "Not Found", keepAlive, ct);
                    if (!keepAlive) break;
                    continue;
                }

                var totalLen = session.TotalLength;
                if (!TryParseRangeHeader(rangeHeader, totalLen, out var hasRange, out var start, out var end))
                {
                    await WriteRangeNotSatisfiableAsync(netStream, totalLen, keepAlive, ct);
                    if (!keepAlive) break;
                    continue;
                }

                var contentLen = end - start + 1;
                var headerText = hasRange
                    ? $"HTTP/1.1 206 Partial Content\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Range: bytes {start}-{end}/{totalLen}\r\nContent-Length: {contentLen}\r\nConnection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Length: {contentLen}\r\nConnection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n";

                var headerBytes = Encoding.ASCII.GetBytes(headerText);
                await netStream.WriteAsync(headerBytes, ct);

                if (method != "HEAD")
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    try
                    {
                        var remaining = contentLen;
                        var cursor = start;
                        while (remaining > 0 && !ct.IsCancellationRequested)
                        {
                            var toRead = (int)Math.Min(buffer.Length, remaining);
                            var read = await session.ReadAsync(cursor, buffer.AsMemory(0, toRead), ct);
                            if (read <= 0)
                                return;

                            await netStream.WriteAsync(buffer.AsMemory(0, read), ct);
                            cursor += read;
                            remaining -= read;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                await netStream.FlushAsync(ct);
                if (!keepAlive)
                    break;
            }
        }
    }

    private HttpMediaStreamSession? ResolveSessionFromPath(string path)
    {
        if (!path.StartsWith("/stream/", StringComparison.Ordinal))
            return null;

        var id = path["/stream/".Length..];
        var qIdx = id.IndexOf('?');
        if (qIdx >= 0) id = id[..qIdx];
        if (id.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            id = id[..^4];

        return _sessions.TryGetValue(id, out var s) ? s : null;
    }

    private static bool TryParseRangeHeader(
        string? rangeHeader,
        long totalLen,
        out bool hasRange,
        out long start,
        out long end)
    {
        hasRange = false;
        start = 0;
        end = Math.Max(0, totalLen - 1);

        if (string.IsNullOrWhiteSpace(rangeHeader))
            return true;

        var trimmed = rangeHeader.Trim();
        if (!trimmed.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return true;

        hasRange = true;
        var spec = trimmed["bytes=".Length..].Trim();
        var commaIdx = spec.IndexOf(',');
        if (commaIdx >= 0) spec = spec[..commaIdx].Trim();

        var dashIdx = spec.IndexOf('-');
        if (dashIdx < 0)
            return false;

        var left = spec[..dashIdx].Trim();
        var right = spec[(dashIdx + 1)..].Trim();

        if (string.IsNullOrEmpty(left))
        {
            if (!long.TryParse(right, out var suffix) || suffix <= 0)
                return false;
            start = Math.Max(0, totalLen - suffix);
            end = totalLen - 1;
            return start <= end;
        }

        if (!long.TryParse(left, out start) || start < 0 || start >= totalLen)
            return false;

        if (string.IsNullOrEmpty(right))
        {
            end = totalLen - 1;
            return true;
        }

        if (!long.TryParse(right, out end) || end < start)
            return false;

        end = Math.Min(end, totalLen - 1);
        return true;
    }

    private static async Task<(string Method, string Path, string? Range, bool KeepAlive)?> ReadHttpRequestAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var raw = new List<byte>(1024);
        var buf = new byte[1];
        while (raw.Count < 16384)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
            }
            catch
            {
                return null;
            }
            if (n == 0)
                return null;

            raw.Add(buf[0]);
            var count = raw.Count;
            if (count >= 4 && raw[count - 4] == '\r' && raw[count - 3] == '\n' && raw[count - 2] == '\r' && raw[count - 1] == '\n')
                break;
        }

        var text = Encoding.ASCII.GetString(raw.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var reqParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (reqParts.Length < 2)
            return null;

        var method = reqParts[0].ToUpperInvariant();
        var path = reqParts[1];
        var keepAlive = reqParts.Length >= 3 && reqParts[2].Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase);
        string? range = null;

        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            var name = lines[i][..colon].Trim();
            var val = lines[i][(colon + 1)..].Trim();
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase))
                range = val;
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                keepAlive = !val.Equals("close", StringComparison.OrdinalIgnoreCase);
        }

        return (method, path, range, keepAlive);
    }

    private static Task WriteSimpleResponseAsync(NetworkStream stream, int code, string reason, bool keepAlive, CancellationToken ct)
    {
        var resp = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n");
        return stream.WriteAsync(resp, ct).AsTask();
    }

    private static Task WriteRangeNotSatisfiableAsync(NetworkStream stream, long totalLen, bool keepAlive, CancellationToken ct)
    {
        var resp = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{totalLen}\r\nContent-Length: 0\r\nConnection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n");
        return stream.WriteAsync(resp, ct).AsTask();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdownCts.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var (_, session) in _sessions)
            session.Dispose();
        _sessions.Clear();
        _mediaHttp.Dispose();
        _shutdownCts.Dispose();
    }
}
