// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Calculates file hashes for Monitor scans.
/// </summary>
public static class MonitorHasher
{
    /// <summary>
    /// Marker used when a file is larger than the configured hash limit.
    /// </summary>
    public const string Sha1SkippedTooLarge = "SKIPPED_TOO_LARGE";

    private const string LowerHexAlphabet = "0123456789abcdef";

    /// <summary>
    /// Calculates the SHA1 hash for a file.
    /// </summary>
    /// <param name="filePath">The file to hash.</param>
    /// <param name="chunkSizeBytes">The stream read chunk size.</param>
    /// <param name="maxFileSizeMb">The maximum file size to hash, in megabytes.</param>
    /// <returns>A lowercase SHA1 value, <see cref="Sha1SkippedTooLarge"/>, or null when the file cannot be read.</returns>
    public static string? CalculateSha1(string filePath, int chunkSizeBytes, int? maxFileSizeMb)
    {
        return CalculateHash(filePath, "SHA1", chunkSizeBytes, maxFileSizeMb);
    }

    /// <summary>
    /// Calculates the configured hash for a file.
    /// </summary>
    /// <param name="filePath">The file to hash.</param>
    /// <param name="algorithm">The hash algorithm. Supported values are SHA1, MD5, SHA256, and SHA512.</param>
    /// <param name="chunkSizeBytes">The stream read chunk size.</param>
    /// <param name="maxFileSizeMb">The maximum file size to hash, in megabytes.</param>
    /// <returns>A lowercase hash value, <see cref="Sha1SkippedTooLarge"/>, or null when the file cannot be read.</returns>
    public static string? CalculateHash(string filePath, string algorithm, int chunkSizeBytes, int? maxFileSizeMb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);

        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists)
        {
            return null;
        }

        if (maxFileSizeMb.HasValue && fileInfo.Length > maxFileSizeMb.Value * 1024L * 1024L)
        {
            return Sha1SkippedTooLarge;
        }

        try
        {
            using FileStream stream = File.OpenRead(filePath);
            using HashAlgorithm hashAlgorithm = CreateHashAlgorithm(algorithm);
            byte[] hash = hashAlgorithm.ComputeHash(stream);
            return ToLowerHex(hash);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HashAlgorithm CreateHashAlgorithm(string algorithm)
    {
        switch (algorithm.ToUpperInvariant())
        {
            case "SHA1":
#pragma warning disable CA5350 // SHA1 remains the default for compatibility with the original Monitor CSV data.
                return SHA1.Create();
#pragma warning restore CA5350
            case "MD5":
#pragma warning disable CA5351 // MD5 is an explicit user-selectable inventory hash, not a security boundary.
                return MD5.Create();
#pragma warning restore CA5351
            case "SHA256":
                return SHA256.Create();
            case "SHA512":
                return SHA512.Create();
            default:
                throw new ArgumentException("Unsupported hash algorithm: " + algorithm + ".", nameof(algorithm));
        }
    }

    private static string ToLowerHex(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length * 2, bytes, static (chars, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                byte value = source[index];
                chars[index * 2] = LowerHexAlphabet[value >> 4];
                chars[(index * 2) + 1] = LowerHexAlphabet[value & 0xF];
            }
        });
    }
}
