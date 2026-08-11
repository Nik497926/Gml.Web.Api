using System.Collections.Concurrent;

namespace Gml.Web.Api.Core.Services;

/// <summary>
/// In-memory store for external (Unicore) access/refresh tokens keyed by player UUID.
/// Нужен, когда AccessToken игрока — Gml JWT, а кабинет всё равно ходит в Unicore.
/// </summary>
public class ExternalPlayerTokenStore
{
    private readonly ConcurrentDictionary<string, TokenPair> _byUuid =
        new(StringComparer.OrdinalIgnoreCase);

    public void SetTokens(string uuid, string? accessToken, string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return;

        if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
        {
            _byUuid.TryRemove(uuid, out _);
            return;
        }

        _byUuid.AddOrUpdate(
            uuid,
            _ => new TokenPair(accessToken, refreshToken),
            (_, prev) => new TokenPair(
                string.IsNullOrWhiteSpace(accessToken) ? prev.AccessToken : accessToken,
                string.IsNullOrWhiteSpace(refreshToken) ? prev.RefreshToken : refreshToken));
    }

    public void SetRefreshToken(string uuid, string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            if (_byUuid.TryGetValue(uuid, out var prev) && !string.IsNullOrWhiteSpace(prev.AccessToken))
            {
                _byUuid[uuid] = prev with { RefreshToken = null };
                return;
            }

            _byUuid.TryRemove(uuid, out _);
            return;
        }

        _byUuid.AddOrUpdate(
            uuid,
            _ => new TokenPair(null, refreshToken),
            (_, prev) => prev with { RefreshToken = refreshToken });
    }

    public string? GetRefreshToken(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return null;

        return _byUuid.TryGetValue(uuid, out var pair) ? pair.RefreshToken : null;
    }

    public string? GetAccessToken(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return null;

        return _byUuid.TryGetValue(uuid, out var pair) ? pair.AccessToken : null;
    }

    private sealed record TokenPair(string? AccessToken, string? RefreshToken);
}
