// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetOutputBufferTests
{
    [TestMethod]
    public void SplitAnsiColorsHyperlinksAndControlStringsDoNotReachOutput()
    {
        var buffer = new WingetOutputBuffer();
        foreach (char character in "\u001b[31mRed\u001b[0m \u001b]8;;https://example.test/secret\u001b\\link\u001b]8;;\u001b\\\u001b]0;window title\a\u001bPprivate data\u001b\\\u001b(B\0\a\r\nDone")
        {
            buffer.Append(character.ToString());
        }

        Assert.AreEqual("Red link\nDone", buffer.Snapshot());
    }

    [TestMethod]
    public void CarriageReturnReplacesSpinnerLineButPreservesCrLfAndCompletedLines()
    {
        var buffer = new WingetOutputBuffer();
        buffer.Append("Started\r\nDownload 10%\r");
        Assert.AreEqual("Started\nDownload 10%", buffer.Snapshot());
        buffer.Append("\u001b[2KDownload 90%\rDone\r");
        buffer.Append("\nNext");
        Assert.AreEqual("Started\nDone\nNext", buffer.Snapshot());
        buffer.Append("\b\bxt");
        Assert.AreEqual("Started\nDone\nNext", buffer.Snapshot());
    }

    [TestMethod]
    public void StandardErrorCannotEraseOrJoinPartialStandardOutput()
    {
        var buffer = new WingetOutputBuffer();
        buffer.Append("Downloading 20%");
        buffer.Append("Warning", standardError: true);
        buffer.Append("\rDownloading 80%");
        buffer.Append(": dependency\r\n", standardError: true);
        Assert.AreEqual("Downloading 80%\nWarning: dependency\n", buffer.Snapshot());
        buffer.Append("\rDone\r\n");
        buffer.Append("Finished\n");
        Assert.AreEqual("Done\nWarning: dependency\nFinished\n", buffer.Snapshot());
    }

    [TestMethod]
    public void SnapshotsAreThrottledDeduplicatedAndFinalTailIsFlushed()
    {
        var buffer = new WingetOutputBuffer();
        buffer.Append("First");
        Assert.IsNull(buffer.TakeChangedSnapshot(TimeSpan.FromMilliseconds(249)));
        Assert.AreEqual("First", buffer.TakeChangedSnapshot(TimeSpan.FromMilliseconds(250)));
        buffer.Append("\rSecond");
        Assert.IsNull(buffer.TakeChangedSnapshot(TimeSpan.FromMilliseconds(499)));
        Assert.AreEqual("Second", buffer.TakeChangedSnapshot(TimeSpan.FromMilliseconds(500)));
        Assert.IsNull(buffer.TakeChangedSnapshot(TimeSpan.FromMilliseconds(750)));
        buffer.Append("\rDone");
        Assert.AreEqual("Done", buffer.TakeChangedSnapshot(TimeSpan.FromMilliseconds(751), force: true));
        Assert.IsNull(buffer.TakeChangedSnapshot(TimeSpan.FromMilliseconds(752), force: true));
    }

    [TestMethod]
    public void TailStaysBoundedAcrossLargeLinesAndManyCompletedLines()
    {
        var buffer = new WingetOutputBuffer();
        buffer.Append(new string('x', WingetOutputBuffer.MaximumCharacters * 20));
        Assert.IsTrue(buffer.Snapshot().Length <= WingetOutputBuffer.MaximumCharacters);
        buffer.Append("\rDone\n");
        Assert.AreEqual("Done\n", buffer.Snapshot(), "A truncated active line still supports carriage-return replacement.");
        for (int index = 0; index < 10000; index++)
        {
            buffer.Append($"Message {index}\n");
        }

        string snapshot = buffer.Snapshot();
        Assert.IsTrue(snapshot.Length <= WingetOutputBuffer.MaximumCharacters);
        StringAssert.EndsWith(snapshot, "Message 9999\n");
        Assert.IsFalse(snapshot.Contains("Message 0\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SplitUnicodeAndTruncatedTailNeverPublishHalfSurrogates()
    {
        var buffer = new WingetOutputBuffer();
        buffer.Append("Ready \ud83d");
        Assert.AreEqual("Ready ", buffer.Snapshot());
        buffer.Append("\ude00\n");
        Assert.AreEqual("Ready 😀\n", buffer.Snapshot());
        buffer.Append(string.Concat(Enumerable.Repeat("😀", WingetOutputBuffer.MaximumCharacters)));
        string snapshot = buffer.Snapshot();
        Assert.IsTrue(snapshot.Length <= WingetOutputBuffer.MaximumCharacters);
        Assert.IsTrue(snapshot.Length % 2 == 0);
        Assert.IsFalse(char.IsLowSurrogate(snapshot[0]));
        Assert.IsFalse(char.IsHighSurrogate(snapshot[^1]));
    }

    [TestMethod]
    public void TerminalCursorRedrawReplacesOnlyTheChangedDownloadDigitsAcrossSplitControls()
    {
        var buffer = new WingetOutputBuffer(terminal: true);
        var parser = new WingetCliProgress();
        buffer.Append("Downloading synthetic.exe\r\n  1.00 MB / 4.00 MB");
        Assert.AreEqual(25d, parser.Read(buffer.Snapshot()).Percentage);
        foreach (char character in "\u001b[2;3H3")
        {
            buffer.Append(character.ToString());
        }

        Assert.AreEqual("Downloading synthetic.exe\n  3.00 MB / 4.00 MB", buffer.Snapshot());
        Assert.AreEqual(75d, parser.Read(buffer.Snapshot()).Percentage);
        buffer.Append("\u001b[3G2\u001b[1D1\u001b[3C9");
        Assert.AreEqual("Downloading synthetic.exe\n  1.009MB / 4.00 MB", buffer.Snapshot());
        buffer.Append("\r  2 MB / 4 MB\u001b[K");
        Assert.AreEqual(50d, parser.Read(buffer.Snapshot()).Percentage);
        buffer.Append("\r  3.00 MB / 4.00 MB\r\nStarting package install...");
        var final = parser.Read(buffer.Snapshot());
        Assert.AreEqual("Installing", final.Stage);
        Assert.IsNull(final.Percentage);
    }

    [TestMethod]
    public void TerminalCarriageReturnPreservesUntouchedCellsAndEraseLineUsesCursorPosition()
    {
        var buffer = new WingetOutputBuffer(terminal: true);
        buffer.Append("abcdef\rXY");
        Assert.AreEqual("XYcdef", buffer.Snapshot());
        buffer.Append("\u001b[1K");
        Assert.AreEqual("   def", buffer.Snapshot());
        buffer.Append("\u001b[2KZ");
        Assert.AreEqual("  Z", buffer.Snapshot());
        buffer.Append("\u001b[1GQ\u001b[K");
        Assert.AreEqual("Q", buffer.Snapshot());
        buffer.Append("\r\nLast\r\n");
        Assert.AreEqual("Q\nLast\n", buffer.Snapshot(), "The terminal's empty cursor row must not add an extra final newline.");
    }

    [TestMethod]
    public void TerminalHistoryAndCursorParametersStayBoundedAfterScrolling()
    {
        var buffer = new WingetOutputBuffer(terminal: true);
        for (int index = 0; index < 1000; index++)
        {
            buffer.Append($"Row {index} {new string('x', 70)}\r\n");
        }

        buffer.Append("\u001b[99999;99999HZ");
        buffer.Append("\u001b[" + new string('9', 10000) + "G");
        buffer.Append("\u001b[40;1HTail\u001b[K");
        string snapshot = buffer.Snapshot();
        Assert.IsTrue(snapshot.Length <= WingetOutputBuffer.MaximumCharacters);
        StringAssert.EndsWith(snapshot, "Tail");
        Assert.IsFalse(snapshot.Contains("Row 0 ", StringComparison.Ordinal));
        Assert.IsFalse(snapshot.Contains('\u001b'));
    }

    [TestMethod]
    public void TerminalWideAndSplitUnicodeRemainWholeDuringCursorOverwrites()
    {
        var buffer = new WingetOutputBuffer(terminal: true);
        buffer.Append("中\ud83d");
        Assert.AreEqual("中", buffer.Snapshot());
        buffer.Append("\ude00X");
        Assert.AreEqual("中😀X", buffer.Snapshot());
        buffer.Append("\u001b[5GZ");
        Assert.AreEqual("中😀Z", buffer.Snapshot());
        buffer.Append("\u001b[4GQ");
        Assert.AreEqual("中 QZ", buffer.Snapshot(), "Overwriting the trailing cell of a wide glyph must remove its whole surrogate pair.");
    }

    [TestMethod]
    public void TerminalCombiningMarksAndCursorPaddingCannotEvictVisibleScreenRows()
    {
        var buffer = new WingetOutputBuffer(terminal: true);
        for (int index = 0; index < 80; index++)
        {
            buffer.Append("a" + new string('\u0301', 319) + "\u001b[160GX\r\n");
        }

        buffer.Append("\u001b[40;1HFinal\u001b[K");
        string snapshot = buffer.Snapshot();
        Assert.IsTrue(snapshot.Length <= WingetOutputBuffer.MaximumCharacters);
        StringAssert.EndsWith(snapshot, "Final", "The current screen row must remain attached after history trimming.");
    }
}
