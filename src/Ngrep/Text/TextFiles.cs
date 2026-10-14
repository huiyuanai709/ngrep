using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ngrep.Text;

internal static class TextFiles
{
    public const int BinaryProbeLength = 8192;
    public const long MaxFileBytes = 32L * 1024L * 1024L;

    public static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        var n = Math.Min(bytes.Length, BinaryProbeLength);
        return bytes[..n].Contains((byte)0);
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static PooledBytes Read(string path) => Read(path, stop: null);

    /// <summary>
    /// <paramref name="stop"/> is consulted after each chunk, once the leading bytes
    /// have passed the NUL probe. Returning true ends the read before EOF. A miss
    /// still reads the whole file. The span passed to <paramref name="stop"/> is the
    /// prefix read so far; <c>endOfFile</c> is true on the last chunk.
    /// </summary>
    public static PooledBytes Read(string path, ReadStop? stop)
    {
        byte[]? buffer = null;
        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);
            var lengthLong = RandomAccess.GetLength(handle);
            if (lengthLong > MaxFileBytes)
            {
                return PooledBytes.TooLarge();
            }

            var length = (int)lengthLong;
            if (length == 0)
            {
                return PooledBytes.Ok(null, 0);
            }

            buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                // A full read is one syscall into the pooled buffer. -l checks each chunk
                // so a hit can stop, and the chunk is large enough that a miss is not
                // a long series of small reads.
                var chunk = stop == null ? length : 1 << 20;
                var offset = 0;
                var checkedBinary = false;
                while (offset < length)
                {
                    var end = Math.Min(length, offset + chunk);
                    if (!Fill(handle, buffer, ref offset, end))
                    {
                        break;
                    }

                    if (!checkedBinary && (offset >= BinaryProbeLength || offset >= length))
                    {
                        checkedBinary = true;
                        if (IsBinary(buffer.AsSpan(0, offset)))
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = null;
                            return PooledBytes.Binary();
                        }
                    }

                    if (stop != null && checkedBinary && stop(buffer.AsSpan(0, offset), offset >= length))
                    {
                        var owned = buffer;
                        buffer = null;
                        return PooledBytes.Ok(owned, offset);
                    }
                }

                if (!checkedBinary && IsBinary(buffer.AsSpan(0, Math.Min(offset, BinaryProbeLength))))
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = null;
                    return PooledBytes.Binary();
                }

                var done = buffer;
                buffer = null;
                return PooledBytes.Ok(done, offset);
            }
            catch
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = null;
                }

                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return PooledBytes.Unreadable();
        }
    }

    private static bool Fill(SafeFileHandle handle, byte[] buffer, ref int offset, int end)
    {
        while (offset < end)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(offset, end - offset), offset);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}

internal delegate bool ReadStop(ReadOnlySpan<byte> contents, bool endOfFile);

internal enum ReadKind
{
    Ok,
    TooLarge,
    Binary,
    Unreadable,
}

internal sealed class PooledBytes : IDisposable
{
    private byte[]? _buffer;

    private PooledBytes(ReadKind kind, byte[]? buffer, int length)
    {
        Kind = kind;
        _buffer = buffer;
        Length = length;
    }

    public ReadKind Kind { get; }

    public int Length { get; }

    public ReadOnlySpan<byte> Span => _buffer == null ? [] : _buffer.AsSpan(0, Length);

    public static PooledBytes Ok(byte[]? buffer, int length) => new(ReadKind.Ok, buffer, length);

    public static PooledBytes TooLarge() => new(ReadKind.TooLarge, null, 0);

    public static PooledBytes Binary() => new(ReadKind.Binary, null, 0);

    public static PooledBytes Unreadable() => new(ReadKind.Unreadable, null, 0);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer != null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal static class LineMap
{
    public static int[] Build(string text)
    {
        var starts = new List<int> { 0 };
        SimdNewline.Append(text, starts);
        return starts.ToArray();
    }

    /// <summary>
    /// Real lines in <paramref name="text"/>. A trailing newline does not create an extra empty line.
    /// An empty file has zero lines.
    /// </summary>
    public static int LineCount(string text, int[] lineStarts)
    {
        if (text.Length == 0 || lineStarts.Length == 0)
        {
            return 0;
        }

        if (text[^1] == '\n')
        {
            return lineStarts.Length - 1;
        }

        return lineStarts.Length;
    }

    public static (int Line, int Column) Map(int[] lineStarts, int index)
    {
        if (lineStarts.Length == 0)
        {
            return (1, index + 1);
        }

        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lineStarts[mid] <= index)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var lineIndex = Math.Max(0, hi);
        return (lineIndex + 1, index - lineStarts[lineIndex] + 1);
    }

    public static string LineText(string text, int[] lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Length)
        {
            return "";
        }

        var start = lineStarts[line - 1];
        var exclusive = line < lineStarts.Length ? lineStarts[line] - 1 : text.Length;
        if (exclusive > start && text[exclusive - 1] == '\r')
        {
            exclusive--;
        }

        if (exclusive < start)
        {
            exclusive = start;
        }

        return text[start..exclusive];
    }
}
