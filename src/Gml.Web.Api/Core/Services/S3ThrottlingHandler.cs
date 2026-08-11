namespace Gml.Web.Api.Core.Services;

/// <summary>
/// Ограничивает параллельные HTTP-запросы к S3 и повторяет при rate-limit / lock timeout.
/// </summary>
public sealed class S3ThrottlingHandler : DelegatingHandler
{
    private readonly SemaphoreSlim _gate;
    private readonly int _maxRetries;
    private readonly int _spacingMs;

    public S3ThrottlingHandler(
        HttpMessageHandler innerHandler,
        int maxConcurrent = 2,
        int maxRetries = 6,
        int spacingMs = 75)
        : base(innerHandler)
    {
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrent));
        _maxRetries = Math.Max(0, maxRetries);
        _spacingMs = Math.Max(0, spacingMs);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            byte[]? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            for (var attempt = 0; ; attempt++)
            {
                using var message = await CloneRequestAsync(request, body, cancellationToken);
                var response = await base.SendAsync(message, cancellationToken);

                if (!IsRateLimited(response) || attempt >= _maxRetries)
                    return response;

                var delay = TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt));
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
        }
        finally
        {
            if (_spacingMs > 0)
                await Task.Delay(_spacingMs, cancellationToken);

            _gate.Release();
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
            return true;

        // Unicore/MinIO иногда отдаёт 500/XX с текстом про request rate / lock
        if (!response.IsSuccessStatusCode && response.Content is not null)
        {
            // не читаем тело здесь повторно — достаточно статуса; детальный разбор делает Minio
            // но для 409/500 тоже пробуем ретрай при типичных rate-limit ответах
            if ((int)response.StatusCode is >= 500 and < 600 or 409)
                return true;
        }

        return false;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        await Task.CompletedTask;
        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _gate.Dispose();

        base.Dispose(disposing);
    }
}
