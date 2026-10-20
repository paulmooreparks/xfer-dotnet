using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ParksComputing.XferLang.Tool;

/// <summary>
/// Standard output or standard error, written as UTF-8 bytes through the operating system's
/// handle (DESIGN.md section 11). The console streams of <see cref="Console"/> are not used,
/// because on Windows the stream they give discards a failed write to a closed pipe, which is not
/// documented; a <see cref="FileStream"/> over the standard handle, which its documentation
/// covers, reports it as an <see cref="IOException"/>. A failed write is remembered: nothing more
/// is written to the stream, and the tool exits 2.
/// </summary>
internal sealed partial class StandardStream
{
    // Output is UTF-8 without a byte order mark, whatever the console's code page, and a lone
    // surrogate becomes U+FFFD, as it does when Node writes a string to a stream.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream? stream;

    private StandardStream(Stream? stream)
    {
        this.stream = stream;
        Failed = stream is null;
    }

    /// <summary>Whether a write has failed (or the stream could not be opened).</summary>
    public bool Failed { get; private set; }

    public static StandardStream Output() => Open(1, StdOutputHandle);

    public static StandardStream Error() => Open(2, StdErrorHandle);

    /// <summary>
    /// Under POSIX the default action of SIGPIPE is to end the process, which would make a write
    /// to a closed pipe exit by signal instead of with status 2. A registration that cancels the
    /// default action makes the write fail with EPIPE instead. PosixSignal has no member for
    /// SIGPIPE; its documentation allows a raw signal number, which is 13 on Linux and macOS.
    /// </summary>
    public static IDisposable? IgnoreBrokenPipes() =>
        OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create((PosixSignal)13, context => context.Cancel = true);

    public void Write(string text)
    {
        if (Failed)
        {
            return;
        }

        try
        {
            stream!.Write(Utf8.GetBytes(text));
            stream.Flush();
        }
        catch (IOException)
        {
            Failed = true;
        }
    }

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    private static StandardStream Open(int descriptor, int windowsHandle)
    {
        try
        {
            nint handle = OperatingSystem.IsWindows() ? GetStdHandle(windowsHandle) : descriptor;
            if (handle is 0 or -1)
            {
                return new StandardStream(null); // no such stream: every write fails
            }

            return new StandardStream(new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write, bufferSize: 0));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new StandardStream(null);
        }
    }
}
