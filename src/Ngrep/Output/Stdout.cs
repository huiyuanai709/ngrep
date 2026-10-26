using System.Text;

namespace Ngrep.Output;

/// <summary>
/// Buffers the process stdout. <see cref="Console.Out"/> is a <see cref="StreamWriter"/>
/// with <c>AutoFlush</c> on, so every <c>Console.Write</c> is a write(2) and a match line
/// made of a path, a line number, a column, and the text costs several syscalls. A 64 KiB
/// buffer turns a whole file's hits into one write. Installed from <c>Program.Main</c> only:
/// <see cref="Cli.NgrepCli.Run"/> leaves <see cref="Console.Out"/> alone so a host or a test
/// that replaced it keeps its writer.
/// </summary>
internal static class Stdout
{
    private const int BufferSize = 64 * 1024;

    private static bool _installed;
    private static Stream? _raw;

    /// <summary>True when raw UTF-8 bytes can be written without disturbing buffered text.</summary>
    public static bool CanWriteRaw => _installed;

    /// <summary>
    /// Writes bytes that are already UTF-8. Buffered text is flushed first so the ordering is
    /// the same as if everything had gone through the writer. A host that replaced
    /// <see cref="Console.Out"/> (a test, an embedder) gets the decoded string instead.
    /// </summary>
    public static void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (!_installed)
        {
            Console.Out.Write(Encoding.UTF8.GetString(bytes));
            return;
        }

        Flush();
        _raw ??= Console.OpenStandardOutput();
        _raw.Write(bytes);
    }

    /// <summary>
    /// Replaces <see cref="Console.Out"/>. Call after the output encoding is set, because
    /// setting the encoding rebuilds the console writer on a terminal. Flushes on process
    /// exit so a buffered tail is not lost.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), BufferSize)
        {
            AutoFlush = false,
        };
        Console.SetOut(writer);
        _installed = true;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
    }

    public static void Flush()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // A closed pipe (grep -q, head) is not a search failure.
        }
    }
}
