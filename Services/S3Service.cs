using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3VideoManager.Models;

namespace S3VideoManager.Services;

public class S3Service : IDisposable, IAsyncDisposable
{
    private readonly AwsSettings _settings;
    private readonly IAmazonS3 _s3Client;
    private readonly bool _ownsClient;

    public S3Service(AwsSettings settings, IAmazonS3? s3Client = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.EnsureIsValid();

        if (s3Client is not null)
        {
            _s3Client = s3Client;
            _ownsClient = false;
        }
        else
        {
            _s3Client = CreateClient(_settings);
            _ownsClient = true;
        }
    }

    public async Task<IReadOnlyList<string>> GetSubjectsAsync(CancellationToken cancellationToken = default)
    {
        var subjects = new List<string>();
        var request = new ListObjectsV2Request
        {
            BucketName = _settings.Bucket,
            Delimiter = "/",
            MaxKeys = 1000
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3Client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var prefix in response.CommonPrefixes)
            {
                var subject = TrimTrailingSlash(prefix);
                if (!string.IsNullOrWhiteSpace(subject))
                {
                    subjects.Add(subject);
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated.GetValueOrDefault());

        subjects.Sort(StringComparer.OrdinalIgnoreCase);
        return subjects;
    }

    public async Task<IReadOnlyList<string>> GetClassesAsync(string subjectName, CancellationToken cancellationToken = default)
    {
        var normalizedSubject = NormalizeKeySegment(subjectName, nameof(subjectName));
        var subjectPrefix = $"{normalizedSubject}/";

        var classes = new List<string>();
        var request = new ListObjectsV2Request
        {
            BucketName = _settings.Bucket,
            Prefix = subjectPrefix,
            Delimiter = "/",
            MaxKeys = 1000
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3Client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var prefix in response.CommonPrefixes)
            {
                if (!prefix.StartsWith(subjectPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var remainder = prefix.Substring(subjectPrefix.Length);
                var className = TrimTrailingSlash(remainder);
                if (!string.IsNullOrWhiteSpace(className))
                {
                    classes.Add(className);
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated.GetValueOrDefault());

        classes.Sort(StringComparer.OrdinalIgnoreCase);
        return classes;
    }

    public async Task CreateSubjectAsync(string subjectName, CancellationToken cancellationToken = default)
    {
        var normalizedSubject = NormalizeKeySegment(subjectName, nameof(subjectName));
        var folderKey = $"{normalizedSubject}/";

        var request = new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = folderKey,
            ContentBody = string.Empty
        };

        await _s3Client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteClassAsync(string subjectName, string className, CancellationToken cancellationToken = default)
    {
        var prefix = BuildClassPrefix(subjectName, className);

        var request = new ListObjectsV2Request
        {
            BucketName = _settings.Bucket,
            Prefix = prefix
        };

        var keysBuffer = new List<KeyVersion>(1000);
        ListObjectsV2Response response;
        do
        {
            response = await _s3Client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var s3Object in response.S3Objects)
            {
                keysBuffer.Add(new KeyVersion { Key = s3Object.Key });
                if (keysBuffer.Count == 1000)
                {
                    await DeleteBatchAsync(keysBuffer, cancellationToken).ConfigureAwait(false);
                    keysBuffer.Clear();
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated.GetValueOrDefault());

        if (keysBuffer.Count > 0)
        {
            await DeleteBatchAsync(keysBuffer, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UploadClassAsync(
        string subjectName,
        string className,
        string sourceDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory is required.", nameof(sourceDirectory));
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(IsManagedHlsFile)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("No HLS files (.m3u8/.ts) found to upload.");
        }

        var totalBytes = files.Sum(static path => new FileInfo(path).Length);
        if (totalBytes == 0)
        {
            throw new InvalidOperationException("Cannot upload empty files.");
        }

        var classRoot = BuildClassRoot(subjectName, className);
        long uploadedBytes = 0;
        progress?.Report(0);

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath)
                .Replace('\\', '/');
            var key = $"{classRoot}/{relativePath}".Replace("//", "/");

            var contentType = ResolveContentType(filePath);
            var fileInfo = new FileInfo(filePath);

            using var fileStream = File.OpenRead(filePath);
            var putRequest = new PutObjectRequest
            {
                BucketName = _settings.Bucket,
                Key = key,
                InputStream = fileStream,
                AutoResetStreamPosition = false,
                ContentType = contentType
            };

            await _s3Client.PutObjectAsync(putRequest, cancellationToken).ConfigureAwait(false);

            uploadedBytes += fileInfo.Length;
            progress?.Report((double)uploadedBytes / totalBytes);
        }

        progress?.Report(1);
    }

    private async Task DeleteBatchAsync(List<KeyVersion> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var deleteRequest = new DeleteObjectsRequest
        {
            BucketName = _settings.Bucket
        };
        deleteRequest.Objects.AddRange(keys);

        await _s3Client.DeleteObjectsAsync(deleteRequest, cancellationToken).ConfigureAwait(false);
    }

    private static IAmazonS3 CreateClient(AwsSettings settings)
    {
        var region = RegionEndpoint.GetBySystemName(settings.Region);
        var config = new AmazonS3Config
        {
            RegionEndpoint = region
        };

        if (!string.IsNullOrWhiteSpace(settings.AccessKey) &&
            !string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            var credentials = new BasicAWSCredentials(settings.AccessKey, settings.SecretKey);
            return new AmazonS3Client(credentials, config);
        }

        return new AmazonS3Client(config);
    }

    private static string BuildClassPrefix(string subjectName, string className)
    {
        var classRoot = BuildClassRoot(subjectName, className);
        return $"{classRoot}/";
    }

    private static string BuildClassRoot(string subjectName, string className)
    {
        var normalizedSubject = NormalizeKeySegment(subjectName, nameof(subjectName));
        var normalizedClass = NormalizeKeySegment(className, nameof(className));
        return $"{normalizedSubject}/{normalizedClass}";
    }

    private static string TrimTrailingSlash(string input)
    {
        return input.TrimEnd('/');
    }

    private static string NormalizeKeySegment(string? input, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Value cannot be empty.", argumentName);
        }

        var normalized = input.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value cannot resolve to an empty segment.", argumentName);
        }

        return normalized;
    }

    private static string ResolveContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return "application/vnd.apple.mpegurl";
        }

        if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return "video/mp2t";
        }

        throw new InvalidOperationException($"Unsupported file type '{extension}'. Only HLS playlists and segments are allowed.");
    }

    private static bool IsManagedHlsFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ts", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _s3Client.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
