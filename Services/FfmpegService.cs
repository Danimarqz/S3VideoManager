using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using S3VideoManager.Helpers;
using S3VideoManager.Models;

namespace S3VideoManager.Services;

internal class FfmpegService
{
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
        CancellationToken cancellationToken = default)
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
        var arguments = BuildArguments(inputFilePath, finalOutputDirectory);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                logProgress?.Report(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                logProgress?.Report(e.Data);
            }
        };

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
                    // Process might have already exited.
                }
            }
        });

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start ffmpeg process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exitCode = await tcs.Task.ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {exitCode}. See log for details.");
        }

        var masterPlaylist = Path.Combine(finalOutputDirectory, "master.m3u8");
        if (!File.Exists(masterPlaylist))
        {
            throw new InvalidOperationException("ffmpeg finished without generating master.m3u8.");
        }

        percentProgress?.Report(1);
        return finalOutputDirectory;
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

    private string BuildArguments(string inputFilePath, string outputDirectory)
    {
        var playlistPath = Path.Combine(outputDirectory, "master.m3u8");
        var segmentPattern = Path.Combine(outputDirectory, "segment_%05d.ts");

        var builder = new StringBuilder();
        builder.Append("-y ");
        builder.Append("-hwaccel cuda ");
        builder.AppendFormat("-i \"{0}\" ", inputFilePath);
        builder.Append("-vf \"scale=-2:720,fps=30\" ");
        builder.Append("-c:v h264_nvenc ");
        builder.Append("-preset slow ");
        builder.Append("-rc:v vbr ");
        builder.Append("-cq 28 ");
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
}
