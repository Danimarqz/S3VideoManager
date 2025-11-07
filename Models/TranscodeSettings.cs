using System;

namespace S3VideoManager.Models;

public class TranscodeSettings
{
    public int HlsTimeSeconds { get; set; } = 6;
    public string VideoBitrate { get; set; } = "1000k";
    public string AudioBitrate { get; set; } = "64k";

    public void EnsureIsValid()
    {
        if (HlsTimeSeconds <= 0)
        {
            throw new InvalidOperationException("TranscodeSettings.HlsTimeSeconds must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(VideoBitrate))
        {
            throw new InvalidOperationException("TranscodeSettings.VideoBitrate cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(AudioBitrate))
        {
            throw new InvalidOperationException("TranscodeSettings.AudioBitrate cannot be empty.");
        }
    }
}
