namespace Gml.Web.Api.Core.Services;

public readonly record struct S3EndpointInfo(string Endpoint, bool UseSsl, string? PathBucket);

public static class S3StorageHostParser
{
    /// <summary>
    /// Gml.Core передаёт StorageHost целиком в Minio WithEndpoint и не включает SSL.
    /// Парсим URL так же, как тестовое соединение: host[:port] + https + опциональный bucket из path.
    /// </summary>
    public static S3EndpointInfo Parse(string storageHost)
    {
        if (string.IsNullOrWhiteSpace(storageHost))
            throw new ArgumentException("Не указан хост хранилища S3");

        var raw = storageHost.Trim();

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            var endpoint = absolute.IsDefaultPort
                ? absolute.Host
                : $"{absolute.Host}:{absolute.Port}";
            var bucketFromPath = absolute.AbsolutePath
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            return new S3EndpointInfo(endpoint, absolute.Scheme == Uri.UriSchemeHttps, bucketFromPath);
        }

        // host или host:port без схемы
        var useSsl = true;
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            useSsl = false;
            raw = raw["http://".Length..];
        }
        else if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw["https://".Length..];
        }

        string? bucketFromHost = null;
        var slash = raw.IndexOf('/');
        if (slash >= 0)
        {
            bucketFromHost = raw[(slash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            raw = raw[..slash];
        }

        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Некорректный хост хранилища S3");

        return new S3EndpointInfo(raw, useSsl, bucketFromHost);
    }

    public static readonly string[] RequiredBuckets = ["profiles", "profile-backgrounds"];
}
