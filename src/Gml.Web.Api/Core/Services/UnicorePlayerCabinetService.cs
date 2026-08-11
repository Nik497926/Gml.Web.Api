using System.Net.Http.Headers;
using Gml.Web.Api.Core.Integrations.Auth;
using Gml.Web.Api.Core.Models.Unicore;
using GmlCore.Interfaces;
using GmlCore.Interfaces.Auth;
using GmlCore.Interfaces.Enums;
using Newtonsoft.Json.Linq;

namespace Gml.Web.Api.Core.Services;

public class UnicorePlayerCabinetService(
    IHttpClientFactory httpClientFactory,
    IGmlManager gmlManager,
    ExternalPlayerTokenStore tokenStore,
    UnicoreCMSAuthService unicoreAuthService,
    IAccessTokenService accessTokenService,
    UnicoreAuthOptionsService unicoreAuthOptions)
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient();

    public async Task<UnicorePlayerCabinetDto> GetCabinetAsync(string userUuid, CancellationToken ct = default)
    {
        var authType = await gmlManager.Integrations.GetAuthType();
        if (authType is not AuthType.UnicoreCMS)
        {
            return new UnicorePlayerCabinetDto
            {
                Available = false,
                Message = "Активна не UnicoreCMS авторизация"
            };
        }

        var authService = await gmlManager.Integrations.GetActiveAuthService();
        if (authService is null || string.IsNullOrWhiteSpace(authService.Endpoint))
        {
            return new UnicorePlayerCabinetDto
            {
                Available = false,
                Message = "Не настроен endpoint UnicoreCMS"
            };
        }

        var player = await gmlManager.Users.GetUserByUuid(userUuid);
        if (player is null)
        {
            return new UnicorePlayerCabinetDto
            {
                Available = false,
                Message = "Игрок не найден"
            };
        }

        var baseUri = new Uri(authService.Endpoint);
        var host = $"{baseUri.Scheme}://{baseUri.Host}";

        var accessToken = await ResolveAccessTokenAsync(player.Uuid, player.AccessToken, player.ExpiredDate);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            var fromMe = await TryLoadWithBearerAsync(host, accessToken, ct);
            if (fromMe is not null)
            {
                fromMe.Available = true;
                fromMe.CabinetUrl = $"{host}/cabinet";
                fromMe.TotalPlaytime = fromMe.Servers.Sum(s => s.Playtime);
                return fromMe;
            }
        }

        var fromPublic = await TryLoadPublicPlaytimeAsync(host, player.Name, ct);
        fromPublic.Available = true;
        fromPublic.CabinetUrl = $"{host}/cabinet";
        fromPublic.TotalPlaytime = fromPublic.Servers.Sum(s => s.Playtime);
        fromPublic.Message =
            "Привилегии недоступны: нет актуального Unicore-токена игрока. Нужен вход через лаунчер.";
        return fromPublic;
    }

    private async Task<string?> ResolveAccessTokenAsync(
        string uuid,
        string? storedAccess,
        DateTime expiredDate)
    {
        // 1) Unicore access из store (режим «наш JWT» держит Unicore токены отдельно)
        var storedExternal = tokenStore.GetAccessToken(uuid);
        if (!string.IsNullOrWhiteSpace(storedExternal)
            && !accessTokenService.ValidateToken(storedExternal))
        {
            var expiry = UnicoreCMSAuthService.TryGetJwtExpiry(storedExternal);
            if (!expiry.HasValue || expiry.Value > DateTime.UtcNow)
                return storedExternal;
        }

        // 2) Unicore JWT на самом игроке (режим useExternalTokens)
        if (!string.IsNullOrWhiteSpace(storedAccess)
            && expiredDate > DateTime.UtcNow
            && !accessTokenService.ValidateToken(storedAccess))
        {
            return storedAccess;
        }

        // 3) Refresh Unicore
        var refresh = tokenStore.GetRefreshToken(uuid);
        if (string.IsNullOrWhiteSpace(refresh))
            return null;

        var refreshed = await unicoreAuthService.RefreshAsync(refresh);
        if (refreshed is not { IsSuccess: true, AccessToken: { Length: > 0 } newAccess })
            return null;

        tokenStore.SetTokens(uuid, newAccess, refreshed.RefreshToken);

        // Обновляем AccessToken игрока только если включён режим Unicore-токенов
        if (unicoreAuthOptions.UseExternalTokens)
        {
            var user = await gmlManager.Users.GetUserByUuid(uuid);
            if (user is not null)
            {
                user.AccessToken = newAccess;
                var expiry = UnicoreCMSAuthService.TryGetJwtExpiry(newAccess);
                if (expiry.HasValue)
                    user.ExpiredDate = expiry.Value;
                await gmlManager.Users.UpdateUser(user);
            }
        }

        return newAccess;
    }

    private async Task<UnicorePlayerCabinetDto?> TryLoadWithBearerAsync(
        string host,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            using var playtimeRequest = new HttpRequestMessage(HttpMethod.Get, $"{host}/cabinet/playtime/me");
            playtimeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var groupsRequest = new HttpRequestMessage(HttpMethod.Get, $"{host}/donates/groups/me");
            groupsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var meRequest = new HttpRequestMessage(HttpMethod.Get, $"{host}/auth/me");
            meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var playtimeTask = _httpClient.SendAsync(playtimeRequest, ct);
            var groupsTask = _httpClient.SendAsync(groupsRequest, ct);
            var meTask = _httpClient.SendAsync(meRequest, ct);
            await Task.WhenAll(playtimeTask, groupsTask, meTask);

            var playtimeResponse = await playtimeTask;
            var groupsResponse = await groupsTask;
            var meResponse = await meTask;

            if (!playtimeResponse.IsSuccessStatusCode)
                return null;

            var playtimeJson = JArray.Parse(await playtimeResponse.Content.ReadAsStringAsync(ct));
            var groupsJson = groupsResponse.IsSuccessStatusCode
                ? JArray.Parse(await groupsResponse.Content.ReadAsStringAsync(ct))
                : [];

            var result = MergeServers(playtimeJson, groupsJson);
            if (meResponse.IsSuccessStatusCode)
            {
                var meJson = JObject.Parse(await meResponse.Content.ReadAsStringAsync(ct));
                var realToken = meJson["user"]?["real"] ?? meJson["real"];
                if (realToken is not null && realToken.Type != JTokenType.Null
                    && decimal.TryParse(realToken.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var balance))
                {
                    result.Balance = balance;
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private async Task<UnicorePlayerCabinetDto> TryLoadPublicPlaytimeAsync(
        string host,
        string username,
        CancellationToken ct)
    {
        try
        {
            var url = $"{host}/users/public/user/{Uri.EscapeDataString(username)}";
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new UnicorePlayerCabinetDto
                {
                    Servers = [],
                    Message = "Не удалось получить публичный профиль Unicore"
                };
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            var playtimes = json["playtimes"] as JArray ?? [];
            return MergeServers(playtimes, []);
        }
        catch
        {
            return new UnicorePlayerCabinetDto
            {
                Servers = [],
                Message = "Ошибка запроса к UnicoreCMS"
            };
        }
    }

    private static UnicorePlayerCabinetDto MergeServers(JArray playtimes, JArray groups)
    {
        var map = new Dictionary<string, UnicorePlayerServerDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in playtimes)
        {
            var server = row["server"];
            var serverId = server?["id"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(serverId))
                continue;

            map[serverId] = new UnicorePlayerServerDto
            {
                ServerId = serverId,
                ServerName = server?["name"]?.ToString() ?? serverId,
                Playtime = row["time"]?.Value<long>() ?? 0
            };
        }

        foreach (var row in groups)
        {
            var server = row["server"];
            var serverId = server?["id"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(serverId))
                continue;

            if (!map.TryGetValue(serverId, out var dto))
            {
                dto = new UnicorePlayerServerDto
                {
                    ServerId = serverId,
                    ServerName = server?["name"]?.ToString() ?? serverId,
                    Playtime = 0
                };
                map[serverId] = dto;
            }

            var group = row["group"];
            var name = group?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            DateTime? expired = null;
            var expiredToken = row["expired"];
            if (expiredToken is not null && expiredToken.Type != JTokenType.Null)
            {
                if (DateTime.TryParse(expiredToken.ToString(), out var parsed))
                    expired = DateTime.SpecifyKind(parsed.ToUniversalTime(), DateTimeKind.Utc);
            }

            dto.DonateGroups.Add(new UnicoreDonateGroupDto
            {
                Name = name,
                IngameId = group?["ingame_id"]?.ToString(),
                Expired = expired
            });
        }

        return new UnicorePlayerCabinetDto
        {
            Servers = map.Values
                .Where(s => s.Playtime > 0 || s.DonateGroups.Count > 0)
                .OrderByDescending(s => s.Playtime)
                .ThenBy(s => s.ServerName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }
}
