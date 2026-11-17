using System.Net;
using System.Net.Http.Headers;

namespace MioReel.Infrastructure.Services.MediaProxy;

internal sealed class HttpMediaStreamSession : IDisposable
{
    // Keep the opening media data ready for immediate playback, including speculative next episodes.
    private const int InitialProbeBytes = 2 * 1024 * 1024;
    private const int ReadAheadWindowBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _playUrl;
    private readonly IReadOnlyDictionary<string, string> _playHeaders;
    private readonly byte[]? _decryptionKey;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();

    private SanitizedMoovResult? _moov;
    private long _cacheStart = -1;
    private int _cacheValidLength;
    private byte[] _cacheData = [];
    private byte[] _openingData = [];
    private int _openingValidLength;
    private long _tailStart;
    private byte[] _tailData = [];
    private int _tailValidLength;
    private bool _disposed;

    public HttpMediaStreamSession(
        HttpClient http,
        string playUrl,
        IReadOnlyDictionary<string, string> playHeaders,
        byte[]? decryptionKey)
    {
        _http = http;
        _playUrl = playUrl;
        _playHeaders = playHeaders;
        _decryptionKey = decryptionKey;
    }

    public long TotalLength { get; private set; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
        var (initialBytes, totalLen, _) = await FetchUpstreamRangeAsync(0, InitialProbeBytes - 1, linked.Token);
        TotalLength = totalLen > 0 ? totalLen : initialBytes.Length;
        if (TotalLength <= 0 || initialBytes.Length == 0)
            throw new InvalidOperationException("上游视频流内容为空，无法读取媒体头");

        _cacheStart = 0;
        _cacheData = initialBytes;
        _cacheValidLength = initialBytes.Length;
        _openingData = initialBytes;
        _openingValidLength = initialBytes.Length;

        if (_decryptionKey is not { Length: 16 })
        {
            await PrepareTailAsync(linked.Token);
            return;
        }

        // Locate 'moov' box (usually at 0x20 with faststart; otherwise walk top-level boxes)
        var (moovOffset, rawMoov) = await LocateAndReadMoovAsync(initialBytes, linked.Token);
        if (rawMoov.Length > 0)
        {
            _moov = Mp4CencAnalyzer.AnalyzeAndSanitizeMoov(moovOffset, rawMoov);

            // Only expose complete decrypted samples in initial window; any sample straddling InitialProbeBytes stays uncached so ReadAsync fetches it in full
            if (_moov.IsEncrypted)
            {
                _cacheValidLength = ApplyMoovAndDecryptWindow(_cacheStart, _cacheData);
                _openingValidLength = _cacheValidLength;
            }
        }
        await PrepareTailAsync(linked.Token);
    }

    private async Task PrepareTailAsync(CancellationToken ct)
    {
        if (TotalLength <= _openingValidLength) return;
        var start = Math.Max(0, TotalLength - 256 * 1024);
        var end = TotalLength - 1;
        if (_moov is { IsEncrypted: true } moov)
            ExpandRangeToSampleBoundaries(moov.SortedEncryptedSamples, TotalLength, ref start, ref end);
        try
        {
            var (data, _, actualStart) = await FetchUpstreamRangeAsync(start, end, ct);
            _tailStart = actualStart;
            _tailData = data;
            _tailValidLength = _moov is { IsEncrypted: true } ? ApplyMoovAndDecryptWindow(actualStart, data) : data.Length;
        }
        catch (HttpRequestException)
        {
            // Tail probing is optional; an upstream without usable tail ranges can still play.
        }
    }

    public async Task<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
    {
        if (offset < 0 || offset >= TotalLength || destination.IsEmpty)
            return 0;

        var requestedCount = (int)Math.Min(destination.Length, TotalLength - offset);
        var reqEnd = offset + requestedCount - 1;

        await _ioLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // FFmpeg may probe the tail before reading the opening samples. Keep the
            // preloaded prefix independently of the sliding cache so those probes cannot evict it.
            if (reqEnd < _openingValidLength)
            {
                _openingData.AsMemory((int)offset, requestedCount).CopyTo(destination);
                return requestedCount;
            }
            if (offset >= _tailStart && reqEnd < _tailStart + _tailValidLength)
            {
                _tailData.AsMemory((int)(offset - _tailStart), requestedCount).CopyTo(destination);
                return requestedCount;
            }

            // Fast path: entirely within sanitized moov
            if (_moov is { IsEncrypted: true } moov
                && offset >= moov.MoovOffset
                && reqEnd < moov.MoovOffset + moov.SanitizedMoovBytes.Length)
            {
                var srcOffset = (int)(offset - moov.MoovOffset);
                moov.SanitizedMoovBytes.AsMemory(srcOffset, requestedCount).CopyTo(destination);
                return requestedCount;
            }

            // Fast path: sliding cache hit (strictly within _cacheValidLength so partial boundary samples are never served raw)
            if (_cacheStart >= 0
                && offset >= _cacheStart
                && reqEnd < _cacheStart + _cacheValidLength)
            {
                var srcOffset = (int)(offset - _cacheStart);
                _cacheData.AsMemory(srcOffset, requestedCount).CopyTo(destination);
                return requestedCount;
            }

            // Cache miss: compute sample-aligned fetch window
            var fetchStart = offset;
            var fetchEnd = Math.Min(TotalLength - 1, Math.Max(reqEnd, offset + ReadAheadWindowBytes - 1));

            if (_moov is { IsEncrypted: true, SortedEncryptedSamples.Length: > 0 } encMoov)
            {
                ExpandRangeToSampleBoundaries(encMoov.SortedEncryptedSamples, TotalLength, ref fetchStart, ref fetchEnd);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var (fetched, _, actualStart) = await FetchUpstreamRangeAsync(fetchStart, fetchEnd, linked.Token);
            if (fetched.Length == 0)
                return 0;

            var validLen = fetched.Length;
            if (_moov is { IsEncrypted: true })
                validLen = ApplyMoovAndDecryptWindow(actualStart, fetched);

            _cacheStart = actualStart;
            _cacheData = fetched;
            _cacheValidLength = validLen;

            if (offset < _cacheStart || offset >= _cacheStart + _cacheValidLength)
                return 0;

            var avail = (int)Math.Min(requestedCount, _cacheStart + _cacheValidLength - offset);
            _cacheData.AsMemory((int)(offset - _cacheStart), avail).CopyTo(destination);
            return avail;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<(long Offset, byte[] Bytes)> LocateAndReadMoovAsync(byte[] initialBytes, CancellationToken ct)
    {
        if (Mp4CencAnalyzer.TryFindTopLevelBox(initialBytes, "moov", out var moovOff, out var moovSize))
        {
            return (moovOff, await ReadExactFileSpanAsync(moovOff, (int)moovSize, initialBytes, ct));
        }

        // Walk top-level box chain beyond initialBytes if moov is trailing or preceded by large box
        long cursor = 0;
        while (cursor + 8 <= TotalLength)
        {
            if (cursor + 16 <= initialBytes.Length)
            {
                var localOff = (int)cursor;
                if (!Mp4CencAnalyzer.TryReadTopLevelBoxHeader(initialBytes.AsSpan(localOff, 16), out var type, out _, out var size) || size <= 0)
                    break;
                if (type == "moov")
                    return (cursor, await ReadExactFileSpanAsync(cursor, (int)size, initialBytes, ct));
                cursor += size;
                continue;
            }

            var hdrEnd = Math.Min(TotalLength - 1, cursor + 15);
            var (bytes, _, _) = await FetchUpstreamRangeAsync(cursor, hdrEnd, ct);
            if (bytes.Length < 8)
                break;

            if (!Mp4CencAnalyzer.TryReadTopLevelBoxHeader(bytes, out var boxType, out _, out var boxSize) || boxSize <= 0)
                break;

            if (boxType == "moov")
            {
                if (boxSize > 64 * 1024 * 1024)
                    break;
                var (fullMoov, _, _) = await FetchUpstreamRangeAsync(cursor, cursor + boxSize - 1, ct);
                return (cursor, fullMoov);
            }

            cursor += boxSize;
        }

        return (0, []);
    }

    private async Task<byte[]> ReadExactFileSpanAsync(long offset, int length, byte[] initialBytes, CancellationToken ct)
    {
        if (offset + length <= initialBytes.Length)
            return initialBytes.AsSpan((int)offset, length).ToArray();

        var result = new byte[length];
        var copied = 0;
        if (offset < initialBytes.Length)
        {
            copied = initialBytes.Length - (int)offset;
            Buffer.BlockCopy(initialBytes, (int)offset, result, 0, copied);
        }

        var (rest, _, _) = await FetchUpstreamRangeAsync(offset + copied, offset + length - 1, ct);
        Buffer.BlockCopy(rest, 0, result, copied, Math.Min(rest.Length, length - copied));
        return result;
    }

    private int ApplyMoovAndDecryptWindow(long windowStart, byte[] windowData)
    {
        var windowEnd = windowStart + windowData.Length - 1;
        var validLength = windowData.Length;

        if (_moov is { IsEncrypted: true } moov)
        {
            var moovStart = moov.MoovOffset;
            var moovEnd = moovStart + moov.SanitizedMoovBytes.Length - 1;
            var ovStart = Math.Max(windowStart, moovStart);
            var ovEnd = Math.Min(windowEnd, moovEnd);
            if (ovStart <= ovEnd)
            {
                var len = (int)(ovEnd - ovStart + 1);
                Buffer.BlockCopy(
                    moov.SanitizedMoovBytes,
                    (int)(ovStart - moovStart),
                    windowData,
                    (int)(ovStart - windowStart),
                    len);
            }

            if (_decryptionKey is { Length: 16 } key)
            {
                var samples = moov.SortedEncryptedSamples;
                var idx = FindFirstOverlappingSample(samples, windowStart);
                for (; idx < samples.Length; idx++)
                {
                    ref readonly var sample = ref samples[idx];
                    if (sample.FileOffset > windowEnd)
                        break;

                    var sampleEnd = sample.FileOffset + sample.Size - 1;
                    if (sample.FileOffset >= windowStart && sampleEnd <= windowEnd)
                    {
                        var span = windowData.AsSpan((int)(sample.FileOffset - windowStart), sample.Size);
                        CencCrypto.DecryptSampleInPlace(span, key, in sample);
                    }
                    else
                    {
                        // Never expose a sample that was not decrypted in full inside this window
                        var boundary = (int)Math.Max(0, sample.FileOffset - windowStart);
                        if (boundary < validLength)
                            validLength = boundary;
                    }
                }
            }
        }

        return validLength;
    }

    private static void ExpandRangeToSampleBoundaries(
        EncryptedSampleSpan[] samples,
        long totalLength,
        ref long fetchStart,
        ref long fetchEnd)
    {
        var firstIdx = FindFirstOverlappingSample(samples, fetchStart);
        if (firstIdx < samples.Length && samples[firstIdx].FileOffset < fetchStart)
            fetchStart = samples[firstIdx].FileOffset;

        var lastIdx = FindFirstOverlappingSample(samples, fetchEnd);
        if (lastIdx < samples.Length && samples[lastIdx].FileOffset <= fetchEnd)
        {
            var sampleEnd = Math.Min(totalLength - 1, samples[lastIdx].FileOffset + samples[lastIdx].Size - 1);
            if (sampleEnd > fetchEnd)
                fetchEnd = sampleEnd;
        }
    }

    private static int FindFirstOverlappingSample(EncryptedSampleSpan[] samples, long targetOffset)
    {
        var lo = 0;
        var hi = samples.Length - 1;
        var result = samples.Length;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var sampleEnd = samples[mid].FileOffset + samples[mid].Size - 1;
            if (sampleEnd >= targetOffset)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return result;
    }

    private async Task<(byte[] Bytes, long TotalLength, long ActualStart)> FetchUpstreamRangeAsync(long start, long endInclusive, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _playUrl);
        foreach (var (k, v) in _playHeaders)
        {
            // Never attach Referer per gateway media CDN specification
            if (string.Equals(k, "Referer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(v))
                continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        req.Headers.Range = new RangeHeaderValue(start, endInclusive);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            throw new HttpRequestException($"视频流请求失败 (HTTP {(int)resp.StatusCode})");

        long totalLen = 0;
        if (resp.Content.Headers.ContentRange?.Length is { } crLen)
            totalLen = crLen;
        else if (resp.StatusCode == HttpStatusCode.OK && resp.Content.Headers.ContentLength is { } clLen)
            totalLen = clLen;

        var actualStart = resp.Content.Headers.ContentRange?.From
            ?? (resp.StatusCode == HttpStatusCode.OK ? 0L : start);

        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        return (data, totalLen, actualStart);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionCts.Cancel();
        _sessionCts.Dispose();
        _ioLock.Dispose();
        _cacheData = [];
        _openingData = [];
        _tailData = [];
    }
}
