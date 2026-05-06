// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorHashAlgorithmTests
{
    [DataTestMethod]
    [DataRow("SHA1", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [DataRow("MD5", "900150983cd24fb0d6963f7d28e17f72")]
    [DataRow("SHA256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [DataRow("SHA512", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f")]
    public void CalculateHashSupportsConfiguredAlgorithms(string algorithm, string expectedHash)
    {
        string root = Path.Combine(Path.GetTempPath(), "kit-monitor-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "sample.txt");

        try
        {
            File.WriteAllText(filePath, "abc");

            string? hash = MonitorHasher.CalculateHash(filePath, algorithm, 32768, 500);

            Assert.AreEqual(expectedHash, hash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
