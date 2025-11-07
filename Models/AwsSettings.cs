using System;

namespace S3VideoManager.Models;

public class AwsSettings
{
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";

    public void EnsureIsValid()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
        {
            throw new InvalidOperationException("AwsSettings.Bucket cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Region))
        {
            throw new InvalidOperationException("AwsSettings.Region cannot be empty.");
        }
    }
}
