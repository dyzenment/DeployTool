using System.Text.RegularExpressions;
using Dytools.DeployTool.Helpers;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers what makes agent.log readable: no escape sequences, one timestamp per line.
///
/// The writer is exercised through Console.SetOut rather than directly, because the thing being
/// asserted is what a caller's plain Console.WriteLine ends up as in the file - which is the only
/// way any of this code is ever reached.
/// </summary>
[TestClass]
public sealed class LogConsoleTests
{
    // -- Stripping -------------------------------------------------------------

    [TestMethod]
    public void Strip_RemovesColourCodes()
        => Assert.AreEqual(
            "Build succeeded",
            LogConsole.StripAnsi("\x1B[32mBuild succeeded\x1B[0m"));

    [TestMethod]
    public void Strip_RemovesCursorAndClearSequences()
        => Assert.AreEqual(
            "Restoring",
            LogConsole.StripAnsi("\x1B[?25l\x1B[2K\x1B[1GRestoring\x1B[?25h"),
            "the terminal logger hides the cursor and rewrites the line - none of it means " +
            "anything in a file");

    [TestMethod]
    public void Strip_RemovesWindowTitleSequences()
        => Assert.AreEqual(
            "done",
            // \u0007 not \x07: a C# \x escape takes up to four hex digits, so "\x07done" is
            // the single character U+007D followed by "one".
            LogConsole.StripAnsi("\x1B]0;dotnet build\u0007done"),
            "an OSC payload must go with its introducer, not be left behind as text");

    [TestMethod]
    public void Strip_RemovesTheBell()
        => Assert.AreEqual("finished", LogConsole.StripAnsi("finished\x07"));

    [TestMethod]
    public void Strip_LeavesBoxDrawingAlone()
    {
        // The console layout is Unicode, not ANSI. Stripping it would be the cure killing
        // the patient.
        const string line = "  ┌- Warmup https://localhost/api ✓ ═══";

        Assert.AreEqual(line, LogConsole.StripAnsi(line));
    }

    [TestMethod]
    public void Strip_LeavesOrdinaryTextUntouched()
        => Assert.AreEqual("Exit 0 (2.6s)", LogConsole.StripAnsi("Exit 0 (2.6s)"));

    // -- Line writing ----------------------------------------------------------

    [TestMethod]
    public void EveryLineIsStamped()
    {
        var lines = Capture(console =>
        {
            console.WriteLine("Stop app pool");
            console.WriteLine("Start app pool");
        });

        Assert.AreEqual(2, lines.Count);
        foreach (var line in lines)
            StringAssert.Matches(line, Stamped, $"'{line}' has no timestamp");

        StringAssert.EndsWith(lines[0], "Stop app pool");
        StringAssert.EndsWith(lines[1], "Start app pool");
    }

    [TestMethod]
    public void BlankLinesStayBlank()
    {
        var lines = Capture(console =>
        {
            console.WriteLine("before");
            console.WriteLine();
            console.WriteLine("after");
        });

        Assert.AreEqual(string.Empty, lines[1],
            "blank lines separate one run from the next - a bare timestamp reads as a line " +
            "that lost its content");
    }

    [TestMethod]
    public void EscapesAreStrippedOnTheWayThrough()
    {
        var lines = Capture(console => console.WriteLine("\x1B[31mError CS1001\x1B[0m"));

        StringAssert.EndsWith(lines.Single(), "Error CS1001");
        Assert.IsFalse(lines.Single().Contains('\x1B'));
    }

    [TestMethod]
    public void AnEscapeSplitAcrossWritesIsStillStripped()
    {
        // Relayed child output arrives in whatever chunks the pipe delivers, so a sequence can
        // straddle two calls. Stripping per write rather than per line would miss this one.
        var lines = Capture(console =>
        {
            console.Write("\x1B[3");
            console.Write("2mgreen\x1B[0m");
            console.WriteLine();
        });

        Assert.AreEqual("green", lines.Single()[^"green".Length..]);
        Assert.IsFalse(lines.Single().Contains('\x1B'));
    }

    [TestMethod]
    public void CarriageReturnsDoNotSplitALine()
    {
        var lines = Capture(console => console.WriteLine("Exit 0\r"));

        Assert.AreEqual(1, lines.Count, "CRLF must produce one line, not one line and a blank");
        StringAssert.EndsWith(lines[0], "Exit 0");
    }

    [TestMethod]
    public void AProgressBarCollapsesToItsFinalState()
    {
        // A spinner redraws one line with \r. Only the last state is worth keeping.
        var lines = Capture(console => console.WriteLine("10%\r55%\r100%"));

        Assert.AreEqual(1, lines.Count);
        StringAssert.EndsWith(lines[0], "10%55%100%",
            "the fragments are joined rather than each becoming its own timestamped line");
    }

    [TestMethod]
    public void FlushDoesNotBreakAPartialLine()
    {
        // Console auto-flushes after every write. If Flush emitted the buffer, one WriteLine
        // built from several writes would come out as several stamped lines.
        var lines = Capture(console =>
        {
            console.Write("Exit ");
            console.Flush();
            console.Write("0");
            console.Flush();
            console.WriteLine();
        });

        Assert.AreEqual(1, lines.Count);
        StringAssert.EndsWith(lines[0], "Exit 0");
    }

    [TestMethod]
    public void APartialLineIsNotLostWhenTheWriterIsDisposed()
    {
        var buffer = new StringWriter();
        var writer = LogConsole.CreateWriter(buffer);

        writer.Write("Apply aborted");
        writer.Dispose();

        StringAssert.EndsWith(buffer.ToString().TrimEnd(), "Apply aborted",
            "a crash writes a line without a newline - dropping it loses the reason");
    }

    // -- Fixtures --------------------------------------------------------------

    /// <summary>hh:mm:ss.fff followed by the content.</summary>
    private static readonly Regex Stamped = new(@"^\d{2}:\d{2}:\d{2}\.\d{3}  \S");

    /// <summary>Runs an action against a log writer and returns the lines it produced.</summary>
    private static List<string> Capture(Action<TextWriter> write)
    {
        var buffer = new StringWriter();
        using (var writer = LogConsole.CreateWriter(buffer))
            write(writer);

        return buffer.ToString()
            .Split(Environment.NewLine)
            .SkipLast(1)
            .ToList();
    }
}
