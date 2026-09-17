using System.Text.Json;
using UDPtestLib.Common;
using UDPtestLib.Core;

namespace UDPtestLib.Storage
{
    public sealed record TargetSettings
    {
        public const int CurrentVersion = 1;

        public int Version { get; init; } = CurrentVersion;

        public List<TcpEndpointSetting> TcpSettings { get; init; } =
            ProbeCatalog.CreateDefaultTcpSettings().ToList();

        public List<UdpEndpointSetting> UdpSettings { get; init; } =
            ProbeCatalog.CreateDefaultUdpSettings().ToList();

        public TargetSettings Normalize() => this with
        {
            Version = CurrentVersion,
            TcpSettings = NormalizeTcpSettings(TcpSettings),
            UdpSettings = NormalizeUdpSettings(UdpSettings),
        };

        private static List<TcpEndpointSetting> NormalizeTcpSettings(IReadOnlyList<TcpEndpointSetting>? settings)
        {
            TcpEndpointSetting[] defaults = ProbeCatalog.CreateDefaultTcpSettings().ToArray();
            if (settings is null || settings.Count == 0)
            {
                return defaults.ToList();
            }

            List<TcpEndpointSetting> normalized = new(settings.Count);
            for (int index = 0; index < settings.Count; index++)
            {
                TcpEndpointSetting candidate = settings[index];
                string name = string.IsNullOrWhiteSpace(candidate.Name)
                    ? (index < defaults.Length ? defaults[index].Name : $"TCP {index + 1}")
                    : candidate.Name.Trim();

                if (candidate.Kind != ProbeKind.Https204
                    || !TcpTargetParser.TryParse(candidate.Endpoint, out string canonicalEndpoint, out _, out _))
                {
                    canonicalEndpoint = index < defaults.Length ? defaults[index].Endpoint : "https://www.google.com/generate_204";
                }

                normalized.Add(new TcpEndpointSetting(name, canonicalEndpoint, ProbeKind.Https204));
            }

            return normalized;
        }

        private static List<UdpEndpointSetting> NormalizeUdpSettings(IReadOnlyList<UdpEndpointSetting>? settings)
        {
            UdpEndpointSetting[] defaults = ProbeCatalog.CreateDefaultUdpSettings().ToArray();
            if (settings is null || settings.Count == 0)
            {
                return defaults.ToList();
            }

            List<UdpEndpointSetting> normalized = new(settings.Count);
            for (int index = 0; index < settings.Count; index++)
            {
                UdpEndpointSetting candidate = settings[index];
                string name = string.IsNullOrWhiteSpace(candidate.Name)
                    ? (index < defaults.Length ? defaults[index].Name : $"UDP {index + 1}")
                    : candidate.Name.Trim();

                ProbeKind kind = candidate.Kind is ProbeKind.StunBinding or ProbeKind.UdpEcho
                    ? candidate.Kind
                    : (index < defaults.Length ? defaults[index].Kind : ProbeKind.StunBinding);

                if (!UdpTargetParser.TryParse(candidate.Endpoint, kind, out _, out _, out string canonicalEndpoint, out _))
                {
                    canonicalEndpoint = index < defaults.Length ? defaults[index].Endpoint : "stun.cloudflare.com:3478";
                    kind = index < defaults.Length ? defaults[index].Kind : ProbeKind.StunBinding;
                }

                normalized.Add(new UdpEndpointSetting(name, canonicalEndpoint, kind));
            }

            return normalized;
        }
    }

    public sealed class TargetSettingsStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public TargetSettingsStore(string? filePath = null)
        {
            _filePath = filePath ?? UDPtestPathHelper.TargetsFilePath;
        }

        public string FilePath => _filePath;

        public async Task<TargetSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new TargetSettings();
                }

                await using FileStream stream = new(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4_096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                TargetSettings? settings = await JsonSerializer.DeserializeAsync<TargetSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                return (settings ?? new TargetSettings()).Normalize();
            }
            catch (JsonException)
            {
                return new TargetSettings();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveAsync(TargetSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            TargetSettings normalized = settings.Normalize();
            string directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            string temporaryPath = $"{_filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4_096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        normalized,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
                _gate.Release();
            }
        }

        private static void TryDeleteTemporaryFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
