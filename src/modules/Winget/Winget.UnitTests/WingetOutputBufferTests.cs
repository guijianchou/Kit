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
}
