namespace S3VideoManager.Models;

public class AppSettings
{
    public AwsSettings Aws { get; set; } = new();
    public TranscodeSettings Transcode { get; set; } = new();
}
