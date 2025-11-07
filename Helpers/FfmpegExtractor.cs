using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace S3VideoManager.Helpers;

internal static class FfmpegExtractor
{
    private const string ResourceName = "S3VideoManager.Resources.ffmpeg.exe";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string ResourceSignature = ComputeResourceSignature();
    private static string? _cachedPath;

    public static async Task<string> GetExecutablePathAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_cachedPath) && File.Exists(_cachedPath))
        {
            return _cachedPath;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_cachedPath) && File.Exists(_cachedPath))
            {
                return _cachedPath;
            }

            var targetDirectory = Path.Combine(Path.GetTempPath(), "S3VideoManager");
            Directory.CreateDirectory(targetDirectory);
            var fileName = $"ffmpeg_{ResourceSignature}.exe";
            var targetPath = Path.Combine(targetDirectory, fileName);

            if (!File.Exists(targetPath))
            {
                await using var resourceStream = OpenResourceStream();
                try
                {
                    await using var fileStream = File.Open(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    await resourceStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // If another process created the file concurrently, fall through and use it.
                    if (!File.Exists(targetPath))
                    {
                        throw;
                    }
                }
            }

            _cachedPath = targetPath;
            return targetPath;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Stream OpenResourceStream()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
               ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
    }

    private static string ComputeResourceSignature()
    {
        using var stream = OpenResourceStream();
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}
