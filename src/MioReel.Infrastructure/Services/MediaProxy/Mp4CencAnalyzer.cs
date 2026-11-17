using System.Buffers.Binary;
using System.Text;

namespace MioReel.Infrastructure.Services.MediaProxy;

internal sealed record TrackEncryptionInfo(
    int SampleDescriptionIndex,
    string OriginalFormat,
    string SchemeType,
    byte CryptByteBlock,
    byte SkipByteBlock,
    byte IsProtected,
    byte PerSampleIvSize,
    byte[] ConstantIv);

internal sealed record SanitizedMoovResult(
    long MoovOffset,
    byte[] SanitizedMoovBytes,
    bool IsEncrypted,
    bool IsFragmented,
    EncryptedSampleSpan[] SortedEncryptedSamples);

internal static class Mp4CencAnalyzer
{
    private readonly record struct BoxSlice(string Type, int Offset, int HeaderSize, int Size);
    private readonly record struct SencEntry(byte[] Iv, SubsampleSpan[]? Subsamples);

    public static bool TryFindTopLevelBox(
        ReadOnlySpan<byte> data,
        string targetType,
        out long boxOffset,
        out long boxSize)
    {
        boxOffset = 0;
        boxSize = 0;
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            if (!TryReadBoxHeader(data, pos, data.Length, out var type, out _, out var size) || size <= 0)
                return false;

            if (type == targetType)
            {
                boxOffset = pos;
                boxSize = size;
                return true;
            }

            if (pos + size > int.MaxValue)
                return false;
            pos += (int)size;
        }
        return false;
    }

    public static bool TryReadTopLevelBoxHeader(
        ReadOnlySpan<byte> headerBytes,
        out string type,
        out int headerSize,
        out long totalSize)
    {
        return TryReadBoxHeader(headerBytes, 0, headerBytes.Length, out type, out headerSize, out totalSize);
    }

    public static SanitizedMoovResult AnalyzeAndSanitizeMoov(long moovOffset, byte[] rawMoov)
    {
        var moov = (byte[])rawMoov.Clone();
        if (!TryReadBoxHeader(moov, 0, moov.Length, out var rootType, out var rootHeader, out _) || rootType != "moov")
            return new SanitizedMoovResult(moovOffset, moov, false, false, []);

        var isFragmented = false;
        var encryptedSamples = new List<EncryptedSampleSpan>();
        var anyEncrypted = false;

        foreach (var child in EnumerateChildBoxes(moov, rootHeader, moov.Length))
        {
            if (child.Type == "mvex")
            {
                isFragmented = true;
            }
            else if (child.Type == "trak")
            {
                if (ProcessTrak(moov, child, encryptedSamples))
                    anyEncrypted = true;
            }
        }

        if (encryptedSamples.Count > 1)
            encryptedSamples.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));

        return new SanitizedMoovResult(
            moovOffset,
            moov,
            anyEncrypted,
            isFragmented,
            [.. encryptedSamples]);
    }

    private static bool ProcessTrak(byte[] moov, BoxSlice trakBox, List<EncryptedSampleSpan> outputSamples)
    {
        var trakChildren = EnumerateChildBoxes(moov, trakBox.Offset + trakBox.HeaderSize, trakBox.Offset + trakBox.Size);
        BoxSlice? mdiaBox = null;
        BoxSlice? trakSenc = null;

        foreach (var child in trakChildren)
        {
            if (child.Type == "mdia") mdiaBox = child;
            else if (child.Type == "senc") trakSenc = child;
        }

        if (mdiaBox is null) return false;

        var minfBox = FindChild(moov, mdiaBox.Value, "minf");
        if (minfBox is null) return false;

        var stblBox = FindChild(moov, minfBox.Value, "stbl");
        if (stblBox is null) return false;

        var stblChildren = EnumerateChildBoxes(moov, stblBox.Value.Offset + stblBox.Value.HeaderSize, stblBox.Value.Offset + stblBox.Value.Size);
        BoxSlice? stsdBox = null;
        BoxSlice? stszBox = null;
        BoxSlice? stz2Box = null;
        BoxSlice? stscBox = null;
        BoxSlice? stcoBox = null;
        BoxSlice? co64Box = null;
        BoxSlice? stblSenc = null;

        foreach (var child in stblChildren)
        {
            switch (child.Type)
            {
                case "stsd": stsdBox = child; break;
                case "stsz": stszBox = child; break;
                case "stz2": stz2Box = child; break;
                case "stsc": stscBox = child; break;
                case "stco": stcoBox = child; break;
                case "co64": co64Box = child; break;
                case "senc": stblSenc = child; break;
                case "saiz" or "saio":
                    MarkBoxAsFree(moov, child);
                    break;
            }
        }

        if (stsdBox is null) return false;

        var encryptionEntries = ParseAndSanitizeStsd(moov, stsdBox.Value);
        if (encryptionEntries.Count == 0)
            return false;

        var primaryEnc = encryptionEntries.Values.First();
        var sencBox = stblSenc ?? trakSenc;
        SencEntry[]? sencEntries = null;
        if (sencBox is not null)
        {
            sencEntries = ParseSenc(moov, sencBox.Value, primaryEnc.PerSampleIvSize);
            MarkBoxAsFree(moov, sencBox.Value);
        }

        var sampleSizes = stszBox is not null
            ? ParseStsz(moov, stszBox.Value)
            : (stz2Box is not null ? ParseStz2(moov, stz2Box.Value) : []);
        if (sampleSizes.Length == 0 || stscBox is null)
            return true;

        var chunkOffsets = stcoBox is not null
            ? ParseStco(moov, stcoBox.Value)
            : (co64Box is not null ? ParseCo64(moov, co64Box.Value) : []);
        if (chunkOffsets.Length == 0)
            return true;

        var stscRuns = ParseStsc(moov, stscBox.Value);
        if (stscRuns.Count == 0)
            return true;

        var sampleIndex = 0;
        var runIdx = 0;
        for (var chunkNum = 1; chunkNum <= chunkOffsets.Length && sampleIndex < sampleSizes.Length; chunkNum++)
        {
            while (runIdx + 1 < stscRuns.Count && chunkNum >= stscRuns[runIdx + 1].FirstChunk)
                runIdx++;

            var run = stscRuns[runIdx];
            var fileOffset = chunkOffsets[chunkNum - 1];
            encryptionEntries.TryGetValue(run.SampleDescriptionIndex, out var encInfo);

            for (var s = 0; s < run.SamplesPerChunk && sampleIndex < sampleSizes.Length; s++)
            {
                var size = sampleSizes[sampleIndex];
                if (encInfo is not null && encInfo.IsProtected == 1 && size > 0)
                {
                    var iv = Array.Empty<byte>();
                    SubsampleSpan[]? subs = null;
                    if (sencEntries is not null && sampleIndex < sencEntries.Length)
                    {
                        iv = sencEntries[sampleIndex].Iv;
                        subs = sencEntries[sampleIndex].Subsamples;
                    }
                    if (iv.Length == 0 && encInfo.ConstantIv.Length > 0)
                        iv = encInfo.ConstantIv;

                    if (iv.Length > 0)
                    {
                        outputSamples.Add(new EncryptedSampleSpan(
                            fileOffset,
                            size,
                            iv,
                            subs,
                            encInfo.SchemeType,
                            encInfo.CryptByteBlock,
                            encInfo.SkipByteBlock));
                    }
                }

                fileOffset += size;
                sampleIndex++;
            }
        }

        return true;
    }

    private static Dictionary<int, TrackEncryptionInfo> ParseAndSanitizeStsd(byte[] moov, BoxSlice stsdBox)
    {
        var result = new Dictionary<int, TrackEncryptionInfo>();
        var payloadStart = stsdBox.Offset + stsdBox.HeaderSize + 8; // FullBox(4) + entry_count(4)
        if (payloadStart > stsdBox.Offset + stsdBox.Size)
            return result;

        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stsdBox.Offset + stsdBox.HeaderSize + 4, 4));
        var oldEnd = stsdBox.Offset + stsdBox.Size;

        // Collect all sample entries, sanitize encrypted ones in a staging buffer, then pad trailing removed bytes with a 'free' box right after shrunk stsd
        var writeCursor = payloadStart;
        var readCursor = payloadStart;
        var totalRemoved = 0;

        for (var idx = 1; idx <= entryCount && readCursor + 8 <= oldEnd; idx++)
        {
            if (!TryReadBoxHeader(moov, readCursor, oldEnd, out var entryType, out var entryHeader, out var rawEntrySize) || rawEntrySize <= 0)
                break;

            var entrySize = (int)rawEntrySize;
            var entryEnd = Math.Min(oldEnd, readCursor + entrySize);

            if (entryType is "encv" or "enca")
            {
                var fixedHeaderLen = GetSampleEntryFixedHeaderSize(moov, readCursor, entryHeader, entryType);
                var childStart = readCursor + fixedHeaderLen;
                if (childStart < entryEnd)
                {
                    var sinfBox = EnumerateChildBoxes(moov, childStart, entryEnd).FirstOrDefault(b => b.Type == "sinf");
                    if (sinfBox.Size >= 8 && TryParseSinf(moov, sinfBox, idx, out var info))
                    {
                        result[idx] = info;

                        // Rewrite SampleEntry into temporary slice without sinf
                        var newEntryLen = entrySize - sinfBox.Size;
                        var entryCopy = new byte[newEntryLen];
                        var preLen = sinfBox.Offset - readCursor;
                        Buffer.BlockCopy(moov, readCursor, entryCopy, 0, preLen);
                        var postOffset = sinfBox.Offset + sinfBox.Size;
                        var postLen = entryEnd - postOffset;
                        if (postLen > 0)
                            Buffer.BlockCopy(moov, postOffset, entryCopy, preLen, postLen);

                        BinaryPrimitives.WriteUInt32BigEndian(entryCopy.AsSpan(0, 4), (uint)newEntryLen);
                        Encoding.ASCII.GetBytes(info.OriginalFormat.PadRight(4).AsSpan(0, 4), entryCopy.AsSpan(4, 4));

                        Buffer.BlockCopy(entryCopy, 0, moov, writeCursor, newEntryLen);
                        writeCursor += newEntryLen;
                        totalRemoved += sinfBox.Size;
                        readCursor = entryEnd;
                        continue;
                    }
                }
            }

            if (writeCursor != readCursor)
                Buffer.BlockCopy(moov, readCursor, moov, writeCursor, entryEnd - readCursor);
            writeCursor += (entryEnd - readCursor);
            readCursor = entryEnd;
        }

        if (totalRemoved >= 8)
        {
            var newStsdSize = stsdBox.Size - totalRemoved;
            BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(stsdBox.Offset, 4), (uint)newStsdSize);

            // Fill freed gap immediately after stsd with a valid ISO-BMFF 'free' box so stbl total size & stco offsets stay untouched
            var freeOffset = stsdBox.Offset + newStsdSize;
            Array.Clear(moov, freeOffset, totalRemoved);
            BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(freeOffset, 4), (uint)totalRemoved);
            Encoding.ASCII.GetBytes("free", moov.AsSpan(freeOffset + 4, 4));
        }

        return result;
    }

    private static int GetSampleEntryFixedHeaderSize(byte[] moov, int entryOffset, int headerSize, string type)
    {
        if (type == "encv")
            return headerSize + 78; // 8 SampleEntry + 70 VisualSampleEntry

        // AudioSampleEntry ('enca'): version at entryOffset + headerSize + 8
        var verOffset = entryOffset + headerSize + 8;
        if (verOffset + 2 <= moov.Length)
        {
            var ver = BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(verOffset, 2));
            return ver switch
            {
                1 => headerSize + 28 + 16,
                2 => headerSize + 28 + 36,
                _ => headerSize + 28
            };
        }
        return headerSize + 28;
    }

    private static bool TryParseSinf(byte[] moov, BoxSlice sinfBox, int sampleDescIndex, out TrackEncryptionInfo info)
    {
        info = null!;
        var originalFormat = string.Empty;
        var schemeType = "cenc";
        byte cryptBlocks = 0;
        byte skipBlocks = 0;
        byte isProtected = 1;
        byte ivSize = 8;
        var constantIv = Array.Empty<byte>();

        var children = EnumerateChildBoxes(moov, sinfBox.Offset + sinfBox.HeaderSize, sinfBox.Offset + sinfBox.Size);
        foreach (var child in children)
        {
            if (child.Type == "frma" && child.Size >= 12)
            {
                originalFormat = Encoding.ASCII.GetString(moov, child.Offset + child.HeaderSize, 4);
            }
            else if (child.Type == "schm" && child.Size >= 16)
            {
                schemeType = Encoding.ASCII.GetString(moov, child.Offset + child.HeaderSize + 4, 4);
            }
            else if (child.Type == "schi")
            {
                var tenc = FindChild(moov, child, "tenc");
                if (tenc is not null && tenc.Value.Offset + tenc.Value.HeaderSize + 24 <= moov.Length)
                {
                    var p = tenc.Value.Offset + tenc.Value.HeaderSize;
                    var version = moov[p];
                    if (version >= 1)
                    {
                        var pattern = moov[p + 5];
                        cryptBlocks = (byte)(pattern >> 4);
                        skipBlocks = (byte)(pattern & 0x0F);
                    }
                    isProtected = moov[p + 6];
                    ivSize = moov[p + 7];
                    if (isProtected == 1 && ivSize == 0 && p + 25 <= tenc.Value.Offset + tenc.Value.Size)
                    {
                        var constLen = moov[p + 24];
                        if (constLen > 0 && p + 25 + constLen <= tenc.Value.Offset + tenc.Value.Size)
                            constantIv = moov.AsSpan(p + 25, constLen).ToArray();
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(originalFormat))
            return false;

        info = new TrackEncryptionInfo(
            sampleDescIndex,
            originalFormat,
            schemeType,
            cryptBlocks,
            skipBlocks,
            isProtected,
            ivSize,
            constantIv);
        return true;
    }

    private static SencEntry[] ParseSenc(byte[] moov, BoxSlice sencBox, byte perSampleIvSize)
    {
        var start = sencBox.Offset + sencBox.HeaderSize;
        var end = sencBox.Offset + sencBox.Size;
        if (start + 8 > end)
            return [];

        var flags = (moov[start + 1] << 16) | (moov[start + 2] << 8) | moov[start + 3];
        var hasSubsamples = (flags & 0x02) != 0;
        var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(start + 4, 4));
        var pos = start + 8;

        var entries = new SencEntry[count];
        for (var i = 0; i < count && pos <= end; i++)
        {
            byte[] iv = [];
            if (perSampleIvSize > 0)
            {
                if (pos + perSampleIvSize > end) break;
                iv = moov.AsSpan(pos, perSampleIvSize).ToArray();
                pos += perSampleIvSize;
            }

            SubsampleSpan[]? subs = null;
            if (hasSubsamples)
            {
                if (pos + 2 > end) break;
                var subCount = BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(pos, 2));
                pos += 2;
                subs = new SubsampleSpan[subCount];
                for (var j = 0; j < subCount && pos + 6 <= end; j++)
                {
                    var clear = BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(pos, 2));
                    var prot = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(pos + 2, 4));
                    subs[j] = new SubsampleSpan(clear, prot);
                    pos += 6;
                }
            }

            entries[i] = new SencEntry(iv, subs);
        }
        return entries;
    }

    private static int[] ParseStsz(byte[] moov, BoxSlice stszBox)
    {
        var p = stszBox.Offset + stszBox.HeaderSize;
        var end = stszBox.Offset + stszBox.Size;
        if (p + 12 > end) return [];

        var fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 4, 4));
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 8, 4));
        if (count <= 0 || count > 2_000_000) return [];

        var sizes = new int[count];
        if (fixedSize > 0)
        {
            Array.Fill(sizes, fixedSize);
            return sizes;
        }

        var cursor = p + 12;
        for (var i = 0; i < count && cursor + 4 <= end; i++, cursor += 4)
            sizes[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(cursor, 4));
        return sizes;
    }

    private static int[] ParseStz2(byte[] moov, BoxSlice stz2Box)
    {
        var p = stz2Box.Offset + stz2Box.HeaderSize;
        var end = stz2Box.Offset + stz2Box.Size;
        if (p + 12 > end) return [];

        var fieldSize = moov[p + 7];
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 8, 4));
        if (count <= 0 || count > 2_000_000) return [];

        var sizes = new int[count];
        var cursor = p + 12;
        if (fieldSize == 16)
        {
            for (var i = 0; i < count && cursor + 2 <= end; i++, cursor += 2)
                sizes[i] = BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(cursor, 2));
        }
        else if (fieldSize == 8)
        {
            for (var i = 0; i < count && cursor < end; i++, cursor++)
                sizes[i] = moov[cursor];
        }
        else if (fieldSize == 4)
        {
            for (var i = 0; i < count && cursor < end; i++)
            {
                var b = moov[cursor];
                sizes[i] = (i & 1) == 0 ? (b >> 4) : (b & 0x0F);
                if ((i & 1) == 1) cursor++;
            }
        }
        return sizes;
    }

    private static long[] ParseStco(byte[] moov, BoxSlice stcoBox)
    {
        var p = stcoBox.Offset + stcoBox.HeaderSize;
        var end = stcoBox.Offset + stcoBox.Size;
        if (p + 8 > end) return [];

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 4, 4));
        if (count <= 0 || count > 2_000_000) return [];

        var offsets = new long[count];
        var cursor = p + 8;
        for (var i = 0; i < count && cursor + 4 <= end; i++, cursor += 4)
            offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(cursor, 4));
        return offsets;
    }

    private static long[] ParseCo64(byte[] moov, BoxSlice co64Box)
    {
        var p = co64Box.Offset + co64Box.HeaderSize;
        var end = co64Box.Offset + co64Box.Size;
        if (p + 8 > end) return [];

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 4, 4));
        if (count <= 0 || count > 2_000_000) return [];

        var offsets = new long[count];
        var cursor = p + 8;
        for (var i = 0; i < count && cursor + 8 <= end; i++, cursor += 8)
            offsets[i] = (long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(cursor, 8));
        return offsets;
    }

    private readonly record struct StscEntry(int FirstChunk, int SamplesPerChunk, int SampleDescriptionIndex);

    private static List<StscEntry> ParseStsc(byte[] moov, BoxSlice stscBox)
    {
        var p = stscBox.Offset + stscBox.HeaderSize;
        var end = stscBox.Offset + stscBox.Size;
        var runs = new List<StscEntry>();
        if (p + 8 > end) return runs;

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 4, 4));
        var cursor = p + 8;
        for (var i = 0; i < count && cursor + 12 <= end; i++, cursor += 12)
        {
            runs.Add(new StscEntry(
                (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(cursor, 4)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(cursor + 4, 4)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(cursor + 8, 4))));
        }
        return runs;
    }

    private static void MarkBoxAsFree(byte[] moov, BoxSlice box)
    {
        if (box.Size >= 8 && box.Offset + 8 <= moov.Length)
            Encoding.ASCII.GetBytes("free", moov.AsSpan(box.Offset + 4, 4));
    }

    private static BoxSlice? FindChild(byte[] data, BoxSlice container, string type)
    {
        foreach (var child in EnumerateChildBoxes(data, container.Offset + container.HeaderSize, container.Offset + container.Size))
        {
            if (child.Type == type)
                return child;
        }
        return null;
    }

    private static List<BoxSlice> EnumerateChildBoxes(byte[] data, int start, int end)
    {
        var list = new List<BoxSlice>();
        var pos = start;
        var limit = Math.Min(end, data.Length);
        while (pos + 8 <= limit)
        {
            if (!TryReadBoxHeader(data, pos, limit, out var type, out var hdr, out var size) || size <= 0)
                break;
            var clampedSize = (int)Math.Min(size, limit - pos);
            list.Add(new BoxSlice(type, pos, hdr, clampedSize));
            pos += clampedSize;
        }
        return list;
    }

    private static bool TryReadBoxHeader(
        ReadOnlySpan<byte> data,
        int offset,
        int limit,
        out string type,
        out int headerSize,
        out long totalSize)
    {
        type = string.Empty;
        headerSize = 8;
        totalSize = 0;
        if (offset + 8 > limit)
            return false;

        var rawSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
        if (rawSize == 1)
        {
            if (offset + 16 > limit)
                return false;
            headerSize = 16;
            totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8));
            return totalSize >= 16;
        }

        if (rawSize == 0)
        {
            totalSize = limit - offset;
            return totalSize >= 8;
        }

        totalSize = rawSize;
        return totalSize >= 8;
    }
}
