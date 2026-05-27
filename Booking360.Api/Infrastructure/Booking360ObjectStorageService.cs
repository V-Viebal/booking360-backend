using System.Text.RegularExpressions;
using Minio;
using Minio.DataModel.Args;

namespace Booking360.Api.Infrastructure;

public sealed class Booking360ObjectStorageService
{
    private static readonly Regex UnsafeCharacters = new("[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private readonly IMinioClient _minioClient;
    private readonly Booking360Options _options;

    public Booking360ObjectStorageService(IMinioClient minioClient, Booking360Options options)
    {
        _minioClient = minioClient;
        _options = options;
    }

    public string ActiveBucket => _options.ActiveBucket;

    public async Task EnsureBucketExistsAsync(CancellationToken cancellationToken = default)
    {
        var bucketExists = await _minioClient.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_options.ActiveBucket),
            cancellationToken);

        if (!bucketExists)
        {
            await _minioClient.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(_options.ActiveBucket),
                cancellationToken);
        }
    }

    public async Task<string> UploadAsync(
        Stream stream,
        long sizeBytes,
        string contentType,
        string originalFileName,
        string ownerSubject,
        CancellationToken cancellationToken = default)
    {
        var safeFileName = UnsafeCharacters.Replace(Path.GetFileName(originalFileName), "-");
        var objectKey = $"uploads/{ownerSubject}/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}-{safeFileName}";

        var args = new PutObjectArgs()
            .WithBucket(_options.ActiveBucket)
            .WithObject(objectKey)
            .WithStreamData(stream)
            .WithObjectSize(sizeBytes)
            .WithContentType(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

        await _minioClient.PutObjectAsync(args, cancellationToken);
        return objectKey;
    }

    public Task<string> GetDownloadUrlAsync(string objectKey) =>
        _minioClient.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(_options.ActiveBucket)
                .WithObject(objectKey)
                .WithExpiry(60 * 15));
}