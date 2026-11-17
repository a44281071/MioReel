using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MioReel.Infrastructure.Services.MediaProxy;

internal readonly record struct SubsampleSpan(ushort ClearBytes, uint ProtectedBytes);

internal readonly record struct EncryptedSampleSpan(
    long FileOffset,
    int Size,
    byte[] Iv,
    SubsampleSpan[]? Subsamples,
    string Scheme,
    byte CryptByteBlock,
    byte SkipByteBlock);

internal static class CencCrypto
{
    public static void DecryptSampleInPlace(
        Span<byte> sampleData,
        ReadOnlySpan<byte> key,
        in EncryptedSampleSpan meta)
    {
        if (sampleData.IsEmpty || key.Length != 16)
            return;

        if (meta.Iv.Length == 0 && meta.Scheme is not ("cbcs" or "cbc1"))
            return;

        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        switch (meta.Scheme)
        {
            case "cenc":
                DecryptCtr(sampleData, encryptor, meta.Iv, meta.Subsamples, cryptBlocks: 0, skipBlocks: 0);
                break;
            case "cens":
                DecryptCtr(sampleData, encryptor, meta.Iv, meta.Subsamples, meta.CryptByteBlock, meta.SkipByteBlock);
                break;
            case "cbcs":
                DecryptCbc(sampleData, key, meta.Iv, meta.Subsamples, meta.CryptByteBlock, meta.SkipByteBlock);
                break;
            case "cbc1":
                DecryptCbc(sampleData, key, meta.Iv, meta.Subsamples, cryptBlocks: 0, skipBlocks: 0);
                break;
        }
    }

    private static void DecryptCtr(
        Span<byte> sampleData,
        ICryptoTransform ecbEncryptor,
        byte[] iv,
        SubsampleSpan[]? subsamples,
        byte cryptBlocks,
        byte skipBlocks)
    {
        Span<byte> counter = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[16];
        if (iv.Length >= 16)
        {
            iv.AsSpan(0, 16).CopyTo(counter);
        }
        else
        {
            counter.Clear();
            iv.CopyTo(counter);
        }

        var useEightByteCounter = iv.Length <= 8;
        var keystreamOffset = 16; // Force initial counter encryption

        if (subsamples is { Length: > 0 })
        {
            var pos = 0;
            foreach (var sub in subsamples)
            {
                pos += sub.ClearBytes;
                if (pos >= sampleData.Length) break;

                var encLen = (int)Math.Min(sub.ProtectedBytes, (uint)(sampleData.Length - pos));
                if (encLen > 0)
                {
                    ApplyCtrRange(
                        sampleData.Slice(pos, encLen),
                        ecbEncryptor,
                        counter,
                        keystream,
                        ref keystreamOffset,
                        useEightByteCounter,
                        cryptBlocks,
                        skipBlocks);
                    pos += encLen;
                }
            }
            return;
        }

        ApplyCtrRange(
            sampleData,
            ecbEncryptor,
            counter,
            keystream,
            ref keystreamOffset,
            useEightByteCounter,
            cryptBlocks,
            skipBlocks);
    }

    private static void ApplyCtrRange(
        Span<byte> data,
        ICryptoTransform ecbEncryptor,
        Span<byte> counter,
        Span<byte> keystream,
        ref int keystreamOffset,
        bool useEightByteCounter,
        byte cryptBlocks,
        byte skipBlocks)
    {
        if (cryptBlocks > 0 && skipBlocks > 0)
        {
            // ISO 23001-7 'cens': stripe pattern only on whole 16-byte blocks
            var blockPos = 0;
            var blockCount = data.Length / 16;
            while (blockPos < blockCount)
            {
                var cryptLen = Math.Min((int)cryptBlocks, blockCount - blockPos);
                XorCtrBytes(
                    data.Slice(blockPos * 16, cryptLen * 16),
                    ecbEncryptor,
                    counter,
                    keystream,
                    ref keystreamOffset,
                    useEightByteCounter);
                blockPos += cryptLen + skipBlocks;
            }
            return;
        }

        XorCtrBytes(data, ecbEncryptor, counter, keystream, ref keystreamOffset, useEightByteCounter);
    }

    private static void XorCtrBytes(
        Span<byte> data,
        ICryptoTransform ecbEncryptor,
        Span<byte> counter,
        Span<byte> keystream,
        ref int keystreamOffset,
        bool useEightByteCounter)
    {
        var blockBuf = new byte[16];
        var outBuf = new byte[16];

        for (var i = 0; i < data.Length; i++)
        {
            if (keystreamOffset >= 16)
            {
                counter.CopyTo(blockBuf);
                ecbEncryptor.TransformBlock(blockBuf, 0, 16, outBuf, 0);
                outBuf.CopyTo(keystream);
                IncrementCounter(counter, useEightByteCounter);
                keystreamOffset = 0;
            }

            data[i] ^= keystream[keystreamOffset++];
        }
    }

    private static void IncrementCounter(Span<byte> counter, bool eightByteCounter)
    {
        var start = eightByteCounter ? 8 : 0;
        for (var i = 15; i >= start; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    private static void DecryptCbc(
        Span<byte> sampleData,
        ReadOnlySpan<byte> key,
        byte[] iv,
        SubsampleSpan[]? subsamples,
        byte cryptBlocks,
        byte skipBlocks)
    {
        var fullIv = new byte[16];
        if (iv.Length >= 16)
            iv.AsSpan(0, 16).CopyTo(fullIv);
        else
            iv.CopyTo(fullIv);

        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        if (subsamples is { Length: > 0 })
        {
            var pos = 0;
            foreach (var sub in subsamples)
            {
                pos += sub.ClearBytes;
                if (pos >= sampleData.Length) break;

                var encLen = (int)Math.Min(sub.ProtectedBytes, (uint)(sampleData.Length - pos));
                if (encLen >= 16)
                {
                    DecryptCbcProtectedSpan(sampleData.Slice(pos, encLen), aes, fullIv, cryptBlocks, skipBlocks);
                }
                pos += encLen;
            }
            return;
        }

        if (sampleData.Length >= 16)
            DecryptCbcProtectedSpan(sampleData, aes, fullIv, cryptBlocks, skipBlocks);
    }

    private static void DecryptCbcProtectedSpan(
        Span<byte> data,
        Aes aes,
        byte[] iv,
        byte cryptBlocks,
        byte skipBlocks)
    {
        var totalFullBlocks = data.Length / 16;
        if (totalFullBlocks == 0)
            return;

        // Per ISO 23001-7 §10.4.2, CBC chaining continues across crypt stripes within one subsample, resetting only on subsample boundary
        var currentIv = (byte[])iv.Clone();
        aes.IV = currentIv;
        using var decryptor = aes.CreateDecryptor();

        if (cryptBlocks == 0 && skipBlocks == 0)
        {
            var byteLen = totalFullBlocks * 16;
            var buf = data[..byteLen].ToArray();
            var plain = new byte[byteLen];
            decryptor.TransformBlock(buf, 0, byteLen, plain, 0);
            plain.CopyTo(data[..byteLen]);
            return;
        }

        var inBlock = new byte[16];
        var outBlock = new byte[16];
        var blockIndex = 0;
        while (blockIndex < totalFullBlocks)
        {
            var count = Math.Min((int)cryptBlocks, totalFullBlocks - blockIndex);
            for (var c = 0; c < count; c++)
            {
                var off = (blockIndex + c) * 16;
                data.Slice(off, 16).CopyTo(inBlock);
                decryptor.TransformBlock(inBlock, 0, 16, outBlock, 0);
                outBlock.CopyTo(data.Slice(off, 16));
            }
            blockIndex += count + skipBlocks;
        }
    }

    public static bool TryParseKeyHex(string? hex, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        var clean = hex.Trim();
        if (clean.Length != 32)
            return false;
        try
        {
            key = Convert.FromHexString(clean);
            return key.Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
