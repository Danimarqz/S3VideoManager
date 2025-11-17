using System;
using System.IO;
using System.Text.Json;
using S3VideoManager.Models;

namespace S3VideoManager.Helpers;

internal static class AppSettingsLoader
{
    private static readonly object Sync = new();
    private static AppSettings? _cached;
    private const string EmbeddedAppSettingsResource = "S3VideoManager.appsettings.json";

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

            using var stream = OpenSettingsStream(basePath);

            var settings = JsonSerializer.Deserialize<AppSettings>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Unable to deserialize appsettings.json.");

            settings.Aws ??= new AwsSettings();
            settings.Transcode ??= new TranscodeSettings();

            settings.Aws.EnsureIsValid();
            settings.Transcode.EnsureIsValid();

            _cached = settings;
            return settings;
        }
    }

    private static Stream OpenSettingsStream(string? basePath)
    {
        var path = ResolvePath(basePath);
        if (!string.IsNullOrEmpty(path))
        {
            return File.OpenRead(path);
        }

        var assembly = typeof(AppSettingsLoader).Assembly;
        var resourceStream = assembly.GetManifestResourceStream(EmbeddedAppSettingsResource);
        if (resourceStream is not null)
        {
            return resourceStream;
        }

        throw new FileNotFoundException("Unable to locate appsettings.json.");
    }

    private static string? ResolvePath(string? basePath)
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

        return null;
    }
}
