using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using S3VideoManager.Helpers;
using S3VideoManager.Models;

namespace S3VideoManager.Services;

public class FfmpegService
{
    private static readonly SemaphoreSlim NvencProbeLock = new(1, 1);
    private static bool? _nvencAvailable;

    private readonly TranscodeSettings _settings;

    public FfmpegService(TranscodeSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.EnsureIsValid();
    }

    public async Task<string> TranscodeToHlsAsync(
        string inputFilePath,
        string? outputDirectory = null,
        IProgress<string>? logProgress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken cancellationToken = default,
        string? outputName = null)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new ArgumentException("Input file path cannot be empty.", nameof(inputFilePath));
        }

        if (!File.Exists(inputFilePath))
        {
            throw new FileNotFoundException("Input video file not found.", inputFilePath);
        }

        var finalOutputDirectory = PrepareOutputDirectory(inputFilePath, outputDirectory);
        percentProgress?.Report(0);

        var ffmpegPath = await FfmpegExtractor.GetExecutablePathAsync(cancellationToken).ConfigureAwait(false);
        var supportsNvenc = await IsNvencAvailableAsync(ffmpegPath, logProgress, cancellationToken).ConfigureAwait(false);
        var useHardware = supportsNvenc;

        while (true)
        {
            var arguments = BuildArguments(inputFilePath, finalOutputDirectory, useHardware, outputName);
            var exitCode = await RunFfmpegProcessAsync(ffmpegPath, arguments, logProgress, percentProgress, cancellationToken).ConfigureAwait(false);

            if (exitCode == 0)
            {
                break;
            }

            if (useHardware)
            {
                logProgress?.Report("No se pudo usar NVENC, reintentando con codificación por CPU (libx264)...");
                useHardware = false;
                continue;
            }

            throw new InvalidOperationException($"ffmpeg exited with code {exitCode}. See log for details.");
        }

        var masterPlaylist = Path.Combine(finalOutputDirectory, $"{outputName}.m3u8");
        if (!File.Exists(masterPlaylist))
        {
            throw new InvalidOperationException($"ffmpeg finished without generating {outputName}.m3u8.");
        }

        percentProgress?.Report(1);
        return finalOutputDirectory;
    }

    private static async Task<int> RunFfmpegProcessAsync(
        string ffmpegPath,
        string arguments,
        IProgress<string>? logProgress,
        IProgress<double>? percentProgress,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        TimeSpan totalDuration = TimeSpan.Zero;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true
        };

        void HandleOutput(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            
            logProgress?.Report(data);

            // Parse Duration if not set
            if (totalDuration == TimeSpan.Zero)
            {
                if (TryExtractDuration(data, out var duration))
                {
                    totalDuration = duration;
                }
            }

            // Parse Time if duration is known
            if (totalDuration > TimeSpan.Zero)
            {
                if (TryExtractTime(data, out var currentTime))
                {
                    var percentage = currentTime.TotalSeconds / totalDuration.TotalSeconds;
                    percentProgress?.Report(Math.Clamp(percentage, 0, 1.0));
                }
            }
        }

        process.OutputDataReceived += (_, e) => HandleOutput(e.Data);
        process.ErrorDataReceived += (_, e) => HandleOutput(e.Data);

        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        using var ctr = cancellationToken.Register(() =>
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // ignored
                }
            }
        });

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start ffmpeg process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return await tcs.Task.ConfigureAwait(false);
    }

    private static bool TryExtractDuration(string line, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        // Example: Duration: 00:00:30.50, ...
        const string marker = "Duration: ";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;

        var start = index + marker.Length;
        var end = line.IndexOf(',', start);
        if (end < 0) return false;

        var durationStr = line.Substring(start, end - start);
        return TimeSpan.TryParse(durationStr, out duration);
    }

    private static bool TryExtractTime(string line, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        // Example: frame=  150 fps=0.0 q=28.0 size=    1536kB time=00:00:05.00 bitrate=2516.6kbits/s speed=10.0x
        const string marker = "time=";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;

        var start = index + marker.Length;
        var end = line.IndexOf(' ', start);
        if (end < 0) 
        {
             // Sometimes it might be at the end of the line or followed by something else not a space? 
             // Ideally scan until next key=value pair or space
             
             // Simple fallback: read 11 chars (HH:mm:ss.ff)
             // But time can be variable length if > 24h.
             // Let's search for the first char that is NOT a digit, colon or dot.
             end = start;
             while (end < line.Length && (char.IsDigit(line[end]) || line[end] == ':' || line[end] == '.'))
             {
                 end++;
             }
        }
        
        if (end <= start) return false;

        var timeStr = line.Substring(start, end - start);
        return TimeSpan.TryParse(timeStr, out time);
    }

    private static async Task<bool> IsNvencAvailableAsync(string ffmpegPath, IProgress<string>? logProgress, CancellationToken cancellationToken)
    {
        if (_nvencAvailable.HasValue)
        {
            return _nvencAvailable.Value;
        }

        await NvencProbeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_nvencAvailable.HasValue)
            {
                return _nvencAvailable.Value;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch
            {
                _nvencAvailable = false;
                return _nvencAvailable.Value;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var combined = (await outputTask.ConfigureAwait(false)) + (await errorTask.ConfigureAwait(false));
            var hasNvenc = process.ExitCode == 0 &&
                           combined.IndexOf("h264_nvenc", StringComparison.OrdinalIgnoreCase) >= 0;

            _nvencAvailable = hasNvenc;
            logProgress?.Report(hasNvenc
                ? "NVENC disponible: usando aceleración por GPU."
                : "NVENC no disponible: se usará codificación por CPU.");
            return hasNvenc;
        }
        catch
        {
            _nvencAvailable = false;
            logProgress?.Report("No se pudo detectar NVENC, se usará codificación por CPU.");
            return false;
        }
        finally
        {
            NvencProbeLock.Release();
        }
    }

    private static string PrepareOutputDirectory(string inputFilePath, string? requestedDirectory)
    {
        if (!string.IsNullOrWhiteSpace(requestedDirectory))
        {
            Directory.CreateDirectory(requestedDirectory);
            return requestedDirectory;
        }

        var safeName = Path.GetFileNameWithoutExtension(inputFilePath);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var directory = Path.Combine(Path.GetTempPath(), "S3VideoManager", "Transcode", $"{safeName}_{timestamp}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string BuildArguments(string inputFilePath, string outputDirectory, bool useHardware, string? outputName)
    {
        var safeBaseName = SanitizeOutputBaseName(outputName, Path.GetFileNameWithoutExtension(inputFilePath));
        var playlistPath = Path.Combine(outputDirectory, $"{safeBaseName}.m3u8");
        var segmentPattern = Path.Combine(outputDirectory, $"{safeBaseName}_%05d.ts");

        var builder = new StringBuilder();
        builder.Append("-y ");

        if (useHardware)
        {
            builder.Append("-hwaccel cuda ");
            builder.AppendFormat("-i \"{0}\" ", inputFilePath);
            builder.Append("-vf \"scale=-2:720,fps=30\" ");
            builder.Append("-c:v h264_nvenc ");
            builder.Append("-preset slow ");
            builder.Append("-rc:v vbr ");
            builder.Append("-cq 28 ");
        }
        else
        {
            builder.AppendFormat("-i \"{0}\" ", inputFilePath);
            builder.Append("-vf \"scale=-2:720,fps=30\" ");
            builder.Append("-c:v libx264 ");
            builder.Append("-preset slow ");
        }

        builder.AppendFormat("-b:v {0} ", _settings.VideoBitrate);
        builder.AppendFormat("-maxrate {0} ", _settings.VideoBitrate);
        builder.Append("-c:a aac ");
        builder.AppendFormat("-b:a {0} ", _settings.AudioBitrate);
        builder.Append("-ac 1 ");
        builder.AppendFormat("-hls_time {0} ", _settings.HlsTimeSeconds);
        builder.Append("-hls_list_size 0 ");
        builder.Append("-hls_playlist_type vod ");
        builder.AppendFormat("-hls_segment_filename \"{0}\" ", segmentPattern);
        builder.AppendFormat("\"{0}\"", playlistPath);

        return builder.ToString();
    }

    private static string SanitizeOutputBaseName(string? desiredName, string fallback)
    {
        var baseName = string.IsNullOrWhiteSpace(desiredName) ? fallback : desiredName.Trim();
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "output";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(invalidChar, '-');
        }

        return baseName;
    }
}
