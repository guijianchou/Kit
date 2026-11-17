// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class RecycleBinHelperTests
{
    [TestMethod]
    public void MissingPathIsRejectedWithoutCreatingIt()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("missing.txt");
        var result = new RecycleBinHelper().MoveToRecycleBin(path);
        Assert.IsFalse(result.Success);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    [TestCategory("WindowsShell")]
    public async Task BackgroundCallerProducesRecoverableFixtureContent()
    {
        using var fixture = new FixtureDirectory();
        using var identity = WindowsIdentity.GetCurrent();
        string path = fixture.PathFor("recycle-fixture.txt");
        string content = "Kit disposable recycle fixture " + Guid.NewGuid().ToString("N");
        File.WriteAllText(path, content);
        string recycleRoot = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", identity.User!.Value);
        var before = new HashSet<string>(GetMetadataPaths(recycleRoot), StringComparer.OrdinalIgnoreCase);
        string? ownMetadata = null;
        string? ownPayload = null;
        try
        {
            var result = await Task.Run(() => new RecycleBinHelper().MoveToRecycleBin(path));

            // Inspect only entries created during this operation, and identify the fixture
            // by its exact original path before reading or removing any recycle payload.
            ownMetadata = GetMetadataPaths(recycleRoot)
                .Except(before, StringComparer.OrdinalIgnoreCase)
                .SingleOrDefault(candidate => string.Equals(ReadOriginalPath(candidate), path, StringComparison.OrdinalIgnoreCase));
            if (ownMetadata is not null)
            {
                ownPayload = Path.Combine(recycleRoot, "$R" + Path.GetFileName(ownMetadata)[2..]);
            }

            Assert.IsTrue(result.Success, result.Error);
            Assert.IsFalse(File.Exists(path));
            Assert.IsNotNull(ownMetadata, "The deleted fixture must have recoverable recycle metadata.");
            Assert.IsNotNull(ownPayload);
            Assert.AreEqual(content, File.ReadAllText(ownPayload), "The recycled payload must retain the fixture contents.");
        }
        finally
        {
            if (ownMetadata is not null && string.Equals(ReadOriginalPath(ownMetadata), path, StringComparison.OrdinalIgnoreCase))
            {
                // These two files belong only to the disposable fixture. Never empty or
                // recursively remove the user's recycle bin.
                if (ownPayload is not null)
                {
                    File.Delete(ownPayload);
                }

                File.Delete(ownMetadata);
            }
        }
    }

    private static IEnumerable<string> GetMetadataPaths(string directory)
    {
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "$I*") : [];
    }

    private static string? ReadOriginalPath(string metadata)
    {
        byte[] bytes;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
                break;
            }
            catch (IOException exception) when (attempt < 10 && unchecked((uint)exception.HResult) is 0x80070020u or 0x80070021u)
            {
                // Explorer/antivirus may briefly open fresh metadata after the shell
                // operation completes. Retry only sharing locks, not other failures.
                Thread.Sleep(50);
            }
        }

        if (bytes.Length < 28)
        {
            return null;
        }

        long version = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        int offset = version switch
        {
            1 => 24,
            2 => 28,
            _ => -1,
        };
        return offset < 0 ? null : Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset).TrimEnd('\0');
    }
}
