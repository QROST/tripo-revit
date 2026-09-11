using System.Buffers.Binary;
using System.Security.Cryptography;
using Tripo.Bridge;

namespace Tripo.Catalog;

internal static class CatalogStorage
{
    public static void RejectLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            // macOS /tmp itself is commonly a symlink; callers should use its resolved path.
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Catalog paths must not traverse symbolic links or reparse points.");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"Input exceeds the {maximumBytes} byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static async Task<string> WriteImageAsync(string directory, byte[] bytes, string mediaType, CancellationToken token)
    {
        string extension = mediaType switch { "image/png" => ".png", "image/jpeg" => ".jpg", _ => ".webp" };
        string path = Path.Combine(directory, $"{Hash(bytes)}{extension}");
        if (!File.Exists(path))
        {
            await using FileStream stream = ImageTransferStore.CreatePrivateFile(path);
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        }
        return path;
    }

    public static (string MediaType, int Width, int Height)? InspectImage(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return ("image/png", BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4)));
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (data.Length >= 30 && data.Slice(12, 4).SequenceEqual("VP8X"u8))
                return ("image/webp", 1 + Read24(data.Slice(24, 3)), 1 + Read24(data.Slice(27, 3)));
            if (data.Length >= 25 && data.Slice(12, 4).SequenceEqual("VP8L"u8) && data[20] == 0x2f)
            {
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
                return ("image/webp", (int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
            }
            if (data.Length >= 30 && data.Slice(12, 4).SequenceEqual("VP8 "u8) && data.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 1, 0x2a }))
                return ("image/webp", BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3fff);
        }
        if (data.Length >= 4 && data[0] == 255 && data[1] == 216)
        {
            int offset = 2;
            while (offset + 3 < data.Length)
            {
                if (data[offset++] != 255) break;
                while (offset < data.Length && data[offset] == 255) offset++;
                if (offset >= data.Length) break;
                byte marker = data[offset++];
                if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (marker is 0xda or 0xd9 || offset + 2 > data.Length) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                if (length < 2 || offset + length > data.Length) break;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && length >= 8)
                    return ("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2)));
                offset += length;
            }
        }
        return null;
    }

    private static int Read24(ReadOnlySpan<byte> value) => value[0] | (value[1] << 8) | (value[2] << 16);
}
