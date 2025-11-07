using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace S3VideoManager.Helpers;

internal static class FfmpegExtractor
{
    private const string ResourceName = "S3VideoManager.Resources.ffmpeg.exe";
    private static readonly SemaphoreSlim Gate = new(1, 1);
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
            var targetPath = Path.Combine(targetDirectory, "ffmpeg.exe");

            await using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

            var resourceLength = resourceStream.Length;
            if (NeedsWrite(targetPath, resourceLength))
            {
                resourceStream.Position = 0;
                await using var fileStream = File.Open(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                await resourceStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            _cachedPath = targetPath;
            return targetPath;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool NeedsWrite(string targetPath, long resourceLength)
    {
        if (!File.Exists(targetPath))
        {
            return true;
        }

        try
        {
            var fileInfo = new FileInfo(targetPath);
            return fileInfo.Length != resourceLength;
        }
        catch (IOException)
        {
            // If we can't read the file, attempt to overwrite it during extraction.
            return true;
        }
    }
}
