using Minio;
using Minio.DataModel.Args;

namespace Gml.Web.Api.Core.Services;

public class S3ConnectionTestService
{
    public async Task TestAsync(string storageHost, string accessKey, string secretKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Не указаны Access Key или Secret Key");

        var parsed = S3StorageHostParser.Parse(storageHost);
        var client = S3MinioClientRepairService.BuildThrottledClient(parsed, accessKey, secretKey, maxConcurrent: 1);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        await client.ListBucketsAsync(timeoutCts.Token);

        foreach (var bucket in S3StorageHostParser.RequiredBuckets)
        {
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(bucket),
                timeoutCts.Token);

            if (!exists)
            {
                await client.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(bucket),
                    timeoutCts.Token);
            }

            await Task.Delay(100, timeoutCts.Token);
        }

        if (!string.IsNullOrWhiteSpace(parsed.PathBucket))
        {
            var pathExists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(parsed.PathBucket),
                timeoutCts.Token);

            if (!pathExists)
            {
                await client.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(parsed.PathBucket),
                    timeoutCts.Token);
            }
        }
    }
}
