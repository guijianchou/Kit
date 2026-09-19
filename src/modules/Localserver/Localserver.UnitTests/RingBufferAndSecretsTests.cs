// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Localserver.UnitTests;

using System;
using System.IO;
using System.Linq;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Coverage for the high-throughput log buffer and the credential store. Both are used on
/// hot or security-sensitive paths and neither needs a child process to exercise.
/// </summary>
[TestClass]
public sealed class RingBufferAndSecretsTests
{
    private string _dataDirectory = string.Empty;

    [TestInitialize]
    public void CreateIsolatedDataDirectory()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "KitLocalserverStorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    [TestCleanup]
    public void RemoveIsolatedDataDirectory()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    // ---- LogRingBuffer: bounded memory is what keeps a chatty service from killing us ----
    [TestMethod]
    public void EmptyBufferSnapshotsAsEmpty()
    {
        var buffer = new LogRingBuffer();

        Assert.AreEqual(0, buffer.Snapshot().Count);
        Assert.AreEqual(0, buffer.DroppedCount);
    }

    [TestMethod]
    public void AppendedLinesKeepSequenceOrder()
    {
        var buffer = new LogRingBuffer();

        buffer.Append(LogStream.StdOut, "first");
        buffer.Append(LogStream.StdErr, "second");
        buffer.Append(LogStream.StdOut, "third");

        var lines = buffer.Snapshot();
        Assert.AreEqual(3, lines.Count);
        Assert.AreEqual("first", lines[0].Text);
        Assert.AreEqual("third", lines[2].Text);
        Assert.AreEqual(LogStream.StdErr, lines[1].Stream);
        Assert.IsTrue(lines[1].Sequence > lines[0].Sequence);
    }

    [TestMethod]
    public void CapacityIsEnforcedAndDropsAreCounted()
    {
        var buffer = new LogRingBuffer(capacity: 10);

        // Push well past the capacity: memory must stay bounded and the loss must be
        // visible rather than silent.
        for (int i = 0; i < 50; i++)
        {
            buffer.Append(LogStream.StdOut, $"line {i}");
        }

        var lines = buffer.Snapshot();
        Assert.AreEqual(10, lines.Count, "The buffer must never exceed its capacity.");
        Assert.IsTrue(buffer.DroppedCount >= 40, "Dropped lines must be counted.");
        Assert.AreEqual("line 49", lines[^1].Text, "The newest line survives.");
    }

    [TestMethod]
    public void SnapshotAfterSequenceReturnsOnlyNewerLines()
    {
        var buffer = new LogRingBuffer();
        buffer.Append(LogStream.StdOut, "a");
        var first = buffer.Snapshot()[0].Sequence;
        buffer.Append(LogStream.StdOut, "b");
        buffer.Append(LogStream.StdOut, "c");

        var newer = buffer.Snapshot(afterSequence: first);

        Assert.AreEqual(2, newer.Count, "Only lines after the cursor are returned.");
        Assert.AreEqual("b", newer[0].Text);
    }

    [TestMethod]
    public void ResizeKeepsTheMostRecentLines()
    {
        var buffer = new LogRingBuffer(capacity: 20);
        for (int i = 0; i < 20; i++)
        {
            buffer.Append(LogStream.StdOut, $"line {i}");
        }

        buffer.Resize(5);

        var lines = buffer.Snapshot();
        Assert.AreEqual(5, lines.Count);
        Assert.AreEqual(5, buffer.Capacity);
        Assert.AreEqual("line 19", lines[^1].Text, "Resizing keeps the newest lines.");
    }

    [TestMethod]
    public void InvalidCapacityIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LogRingBuffer(capacity: 0));
    }

    // ---- SecretStore: credentials must never be stored in plaintext ----
    [TestMethod]
    public void MissingSecretReturnsNull()
    {
        var store = new SecretStore(_dataDirectory);
        store.Load();

        Assert.IsNull(store.Get("absent"));
    }

    [TestMethod]
    public void SecretsRoundTripThroughTheStore()
    {
        var store = new SecretStore(_dataDirectory);
        store.Load();
        store.Set("db.password", "s3cret-value");
        store.Save();

        // A fresh instance reads what the first one wrote.
        var reloaded = new SecretStore(_dataDirectory);
        reloaded.Load();

        Assert.AreEqual("s3cret-value", reloaded.Get("db.password"));
    }

    [TestMethod]
    public void SecretsAreNotWrittenInPlaintext()
    {
        var store = new SecretStore(_dataDirectory);
        store.Load();
        store.Set("api.key", "PLAINTEXT-CANARY-VALUE");
        store.Save();

        foreach (string file in Directory.GetFiles(_dataDirectory, "*", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            Assert.IsFalse(
                content.Contains("PLAINTEXT-CANARY-VALUE", StringComparison.Ordinal),
                $"Credential material must be encrypted at rest, but {Path.GetFileName(file)} contained it verbatim.");
        }
    }

    [TestMethod]
    public void RemovingASecretDropsItFromTheStore()
    {
        var store = new SecretStore(_dataDirectory);
        store.Load();
        store.Set("token", "value");
        store.Remove("token");

        Assert.IsNull(store.Get("token"));
        Assert.IsFalse(store.GetAllNames().Contains("token"));
    }

    [TestMethod]
    public void NamesAreListedWithoutTheirValues()
    {
        var store = new SecretStore(_dataDirectory);
        store.Load();
        store.Set("alpha", "1");
        store.Set("beta", "2");

        var names = store.GetAllNames();

        Assert.IsTrue(names.Contains("alpha"));
        Assert.IsTrue(names.Contains("beta"));
        Assert.IsFalse(names.Any(n => n.Contains('1')), "Names must not leak values.");
    }
}
