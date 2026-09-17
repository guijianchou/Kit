using Microsoft.VisualStudio.TestTools.UnitTesting;
using UDPtestLib.Core;
using UDPtestLib.Storage;

namespace UDPtest.UnitTests
{
    [TestClass]
    public sealed class StorageTests
    {
        [TestMethod]
        public async Task TargetSettingsStore_SaveAndLoad_RoundTripsCorrectly()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"udptest_test_{Guid.NewGuid():N}.json");
            try
            {
                TargetSettingsStore store = new(tempFile);
                TargetSettings initial = await store.LoadAsync();
                Assert.AreEqual(2, initial.TcpSettings.Count);
                Assert.AreEqual(4, initial.UdpSettings.Count);

                TargetSettings custom = new()
                {
                    TcpSettings =
                    [
                        new TcpEndpointSetting("Custom TCP", "https://example.com/generate_204"),
                    ],
                    UdpSettings =
                    [
                        new UdpEndpointSetting("Custom UDP", "stun.custom.com:3478", ProbeKind.StunBinding),
                    ],
                };

                await store.SaveAsync(custom);
                TargetSettings loaded = await store.LoadAsync();

                Assert.AreEqual(1, loaded.TcpSettings.Count);
                Assert.AreEqual("Custom TCP", loaded.TcpSettings[0].Name);
                Assert.AreEqual(1, loaded.UdpSettings.Count);
                Assert.AreEqual("Custom UDP", loaded.UdpSettings[0].Name);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
    }
}
