using System.Reflection;
using GmlCore.Interfaces;
using GmlCore.Interfaces.Enums;
using Minio;
using Minio.DataModel.Args;

namespace Gml.Web.Api.Core.Services;

/// <summary>
/// Gml.Core создаёт MinioClient через WithEndpoint(полный URL) без WithSSL —
/// из‑за этого загрузки в S3 падают, хотя «Проверить соединение» проходит.
/// Подменяем клиент корректным после применения настроек и ограничиваем RPS.
/// </summary>
public class S3MinioClientRepairService(ILogger<S3MinioClientRepairService> logger)
{
    private static readonly string[] KnownBuckets =
    [
        "profiles",
        "profile-backgrounds",
        "other"
    ];

    public async Task ApplyAsync(IGmlManager gmlManager, CancellationToken cancellationToken = default)
    {
        var storage = gmlManager.LauncherInfo.StorageSettings;
        if (storage.StorageType != StorageType.S3)
            return;

        try
        {
            var parsed = S3StorageHostParser.Parse(storage.StorageHost);
            var maxConcurrent = GetEnvInt("S3_MAX_CONCURRENT", 2);
            var client = BuildThrottledClient(parsed, storage.StorageLogin, storage.StoragePassword, maxConcurrent);

            InjectMinioClient(gmlManager.Files, client);

            var buckets = KnownBuckets.ToList();
            if (!string.IsNullOrWhiteSpace(parsed.PathBucket) &&
                !buckets.Contains(parsed.PathBucket, StringComparer.OrdinalIgnoreCase))
            {
                buckets.Add(parsed.PathBucket);
            }

            foreach (var bucket in buckets)
            {
                await EnsureBucketAsync(client, bucket, cancellationToken);
                // небольшая пауза между служебными вызовами
                await Task.Delay(100, cancellationToken);
            }

            logger.LogInformation(
                "S3 Minio client repaired: endpoint={Endpoint}, ssl={UseSsl}, maxConcurrent={MaxConcurrent}, buckets=[{Buckets}]",
                parsed.Endpoint,
                parsed.UseSsl,
                maxConcurrent,
                string.Join(", ", buckets));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to repair S3 Minio client");
            throw;
        }
    }

    internal static IMinioClient BuildThrottledClient(
        S3EndpointInfo parsed,
        string accessKey,
        string secretKey,
        int maxConcurrent = 2)
    {
        var sockets = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(1, maxConcurrent),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        var throttled = new S3ThrottlingHandler(
            sockets,
            maxConcurrent: maxConcurrent,
            maxRetries: GetEnvInt("S3_MAX_RETRIES", 6),
            spacingMs: GetEnvInt("S3_REQUEST_SPACING_MS", 100));

        var httpClient = new HttpClient(throttled)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        return new MinioClient()
            .WithEndpoint(parsed.Endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(parsed.UseSsl)
            .WithHttpClient(httpClient)
            .WithTimeout(1000 * 60 * 5)
            .Build();
    }

    private static void InjectMinioClient(object fileStorageProcedures, IMinioClient client)
    {
        var field = fileStorageProcedures.GetType()
            .GetField("_minioClient", BindingFlags.Instance | BindingFlags.NonPublic);

        if (field is null)
            throw new InvalidOperationException("Поле _minioClient не найдено в FileStorageProcedures — обновите Gml.Core");

        if (field.GetValue(fileStorageProcedures) is IDisposable old)
        {
            try { old.Dispose(); } catch { /* ignore */ }
        }

        field.SetValue(fileStorageProcedures, client);
    }

    private static async Task EnsureBucketAsync(IMinioClient client, string bucket, CancellationToken ct)
    {
        var exists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
        if (!exists)
        {
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);
        }
    }

    private static int GetEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
