namespace Kit.AiHub.Storage;

/// <summary>A public-preference snapshot and its optional legacy/current DPAPI payload.</summary>
public sealed class AiHubSettingsBackup
{
    public byte[] SettingsJson { get; }
    public byte[]? EncryptedSecrets { get; }

    internal AiHubSettingsBackup(byte[] settingsJson, byte[]? encryptedSecrets)
    {
        SettingsJson = (byte[])settingsJson.Clone();
        EncryptedSecrets = encryptedSecrets is null ? null : (byte[])encryptedSecrets.Clone();
    }
}
