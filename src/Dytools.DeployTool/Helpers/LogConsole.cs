using System.Text;
using System.Text.RegularExpressions;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Turns console output into something readable as a log file when nobody is watching a console.
///
/// The agent's poll script is just <c>... >> agent.log 2>&amp;1</c>, so agent.log is whatever this
/// process printed - which, untouched, has three problems for a file that gets read days later:
/// escape sequences from child processes render as garbage, every line looks like it happened at
/// the same moment, and on Windows a redirected console writes the OEM code page, so the box
/// drawing comes out as U/³/À.
///
/// All three are fixed here rather than in the poll script, because the script is written once at
/// install time and frozen - a peer installed last year runs last year's copy.
///
/// Only installed when output is actually redirected. An interactive run keeps its colours, its
/// alignment, and no timestamp on every line.
/// </summary>
public static partial class LogConsole
{
    /// <summary>True once console output has been redirected through the log writer.</summary>
    public static bool IsActive { get; private set; }

    /// <summary>
    /// Wraps stdout and stderr when either is redirected. Safe to call more than once.
    /// </summary>
    public static void InstallIfRedirected()
    {
        if (IsActive || (!Console.IsOutputRedirected && !Console.IsErrorRedirected))
            return;

        // Before wrapping: assigning OutputEncoding replaces Console.Out and Console.Error, which
        // would throw away writers installed first. UTF-8 without a BOM, since the log is appended
        // to on every poll and a BOM mid-file is just two more stray characters.
        try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
        catch (IOException) { /* No console attached to reconfigure; the redirect still works. */ }

        // One gate shared by both writers: stdout and stderr land in the same file, and a line
        // relayed from a child process arrives on a threadpool thread. Without it, two half-lines
        // can interleave into one unreadable one.
        var gate = new object();

        var installed = new List<LineWriter>(2);

        if (Console.IsOutputRedirected)
        {
            var writer = new LineWriter(Console.Out, gate);
            installed.Add(writer);
            Console.SetOut(writer);
        }

        if (Console.IsErrorRedirected)
        {
            var writer = new LineWriter(Console.Error, gate);
            installed.Add(writer);
            Console.SetError(writer);
        }

        IsActive = true;

        // A line still in the buffer at exit - anything written without a trailing newline -
        // would otherwise be dropped, and that is exactly the case a crash produces. Note this
        // cannot go through Flush(), which by design leaves the buffer alone.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var writer in installed) writer.EmitPending();
        };
    }

    /// <summary>
    /// A log writer over an arbitrary sink. Exists so the line handling can be tested without
    /// redirecting the test host's own console out from under the test runner.
    /// </summary>
    public static TextWriter CreateWriter(TextWriter inner) => new LineWriter(inner, new object());

    /// <summary>
    /// Removes ANSI escape sequences: OSC (title-setting) first, because its introducer would
    /// otherwise match the single-character form and leave the payload behind, then CSI (colour,
    /// cursor movement, line clearing), then the remaining two-character escapes. BEL goes too -
    /// progress spinners emit it, and it is noise in a file.
    /// </summary>
    public static string StripAnsi(string text)
        => text.Contains('\x1B') || text.Contains('\x07')
            ? AnsiPattern().Replace(text, string.Empty)
            : text;

    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-?]*[ -/]*[@-~]|\x1B[@-Z\\-_]|\x07")]
    private static partial Regex AnsiPattern();

    /// <summary>
    /// Buffers until a newline, then writes one clean, stamped line.
    ///
    /// Line-buffered rather than per-write because a timestamp is only meaningful per line, and
    /// because an escape sequence can be split across two Write calls - stripping each write in
    /// isolation would miss it.
    /// </summary>
    private sealed class LineWriter(TextWriter inner, object gate) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            lock (gate)
            {
                // \r is dropped rather than buffered: it arrives as part of CRLF, and on its own
                // from progress bars redrawing a line, where only the final state is wanted.
                if (value == '\n') Emit();
                else if (value != '\r') _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        /// <summary>
        /// Deliberately does not emit the partial line. Console auto-flushes after every write,
        /// so flushing the buffer here would stamp and break every line at its first fragment.
        /// The tail is handled at process exit instead.
        /// </summary>
        public override void Flush() => inner.Flush();

        /// <summary>Writes a buffered line that never got its newline. Used at process exit.</summary>
        public void EmitPending()
        {
            lock (gate)
                if (_line.Length > 0) Emit();

            inner.Flush();
        }

        private void Emit()
        {
            var text = StripAnsi(_line.ToString()).TrimEnd();
            _line.Clear();

            // Blank lines stay blank. They are the only separator between runs in the file, and a
            // bare timestamp on an empty line reads as a line that is missing its content.
            inner.WriteLine(text.Length == 0 ? string.Empty : $"{DateTime.Now:HH:mm:ss.fff}  {text}");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) EmitPending();
            base.Dispose(disposing);
        }
    }
}
