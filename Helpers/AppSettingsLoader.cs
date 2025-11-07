using System;
using System.IO;
using System.Text.Json;
using S3VideoManager.Models;

namespace S3VideoManager.Helpers;

internal static class AppSettingsLoader
{
    private static readonly object Sync = new();
    private static AppSettings? _cached;

    public static AppSettings GetAppSettings(string? basePath = null, bool forceReload = false)
    {
        if (!forceReload && _cached is not null)
        {
            return _cached;
        }

        lock (Sync)
        {
            if (!forceReload && _cached is not null)
            {
                return _cached;
            }

            var path = ResolvePath(basePath);
            using var stream = File.OpenRead(path);

            var settings = JsonSerializer.Deserialize<AppSettings>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException($"Unable to deserialize {path}.");

            settings.Aws ??= new AwsSettings();
            settings.Transcode ??= new TranscodeSettings();

            settings.Aws.EnsureIsValid();
            settings.Transcode.EnsureIsValid();

            _cached = settings;
            return settings;
        }
    }

    private static string ResolvePath(string? basePath)
    {
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            var candidate = Path.GetFullPath(Path.Combine(basePath, "appsettings.json"));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var defaultPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        throw new FileNotFoundException("Unable to locate appsettings.json.");
    }
}
