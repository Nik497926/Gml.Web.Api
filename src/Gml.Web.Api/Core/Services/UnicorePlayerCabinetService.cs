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
        var cabinetUrl = $"{host}/cabinet";

        // 1) Админский путь: Unicore API key (не зависит от сессии игрока)
        var apiKey = unicoreAuthOptions.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var fromApiKey = await TryLoadWithApiKeyAsync(host, apiKey, player.Uuid, player.Name, ct);
            if (fromApiKey is not null)
            {
                fromApiKey.Available = true;
                fromApiKey.CabinetUrl = cabinetUrl;
                fromApiKey.TotalPlaytime = fromApiKey.Servers.Sum(s => s.Playtime);
                return fromApiKey;
            }
        }

        // 2) Путь игрока: Bearer JWT Unicore (/me)
        var accessToken = await ResolveAccessTokenAsync(player.Uuid, player.AccessToken, player.ExpiredDate);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            var fromMe = await TryLoadWithBearerAsync(host, accessToken, ct);
            if (fromMe is not null)
            {
                fromMe.Available = true;
                fromMe.CabinetUrl = cabinetUrl;
                fromMe.TotalPlaytime = fromMe.Servers.Sum(s => s.Playtime);
                return fromMe;
            }
        }

        // 3) Публичный playtime без привилегий
        var fromPublic = await TryLoadPublicPlaytimeAsync(host, player.Name, ct);
        fromPublic.Available = true;
        fromPublic.CabinetUrl = cabinetUrl;
        fromPublic.TotalPlaytime = fromPublic.Servers.Sum(s => s.Playtime);
        fromPublic.Message = string.IsNullOrWhiteSpace(apiKey)
            ? "Привилегии недоступны: задайте API-ключ Unicore в настройках интеграции или нужен вход игрока через лаунчер."
            : "Привилегии недоступны: API-ключ Unicore не смог прочитать кабинет (проверьте права admin.dashboard + admin.users.read и IP allowlist).";
        return fromPublic;
    }

    private async Task<UnicorePlayerCabinetDto?> TryLoadWithApiKeyAsync(
        string host,
        string apiKey,
        string userUuid,
        string username,
        CancellationToken ct)
    {
        try
        {
            using var groupsRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{host}/donates/groups/admin/{Uri.EscapeDataString(userUuid)}");
            groupsRequest.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {apiKey}");

            using var moneyRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{host}/cabinet/money/admin/{Uri.EscapeDataString(userUuid)}");
            moneyRequest.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {apiKey}");

            var groupsTask = _httpClient.SendAsync(groupsRequest, ct);
            var moneyTask = _httpClient.SendAsync(moneyRequest, ct);
            var publicTask = TryLoadPublicPlaytimeAsync(host, username, ct);
            await Task.WhenAll(groupsTask, moneyTask, publicTask);

            var groupsResponse = await groupsTask;
            var moneyResponse = await moneyTask;
            var playtime = await publicTask;

            if (!groupsResponse.IsSuccessStatusCode && !moneyResponse.IsSuccessStatusCode)
                return null;

            var groupsJson = groupsResponse.IsSuccessStatusCode
                ? ParseGroupsAdmin(await groupsResponse.Content.ReadAsStringAsync(ct))
                : [];

            if (playtime.Servers.Count == 0 && groupsJson.Count == 0 && !moneyResponse.IsSuccessStatusCode)
                return null;

            var result = MergeApiKeyCabinet(playtime, groupsJson);

            if (moneyResponse.IsSuccessStatusCode)
            {
                var moneyJson = JToken.Parse(await moneyResponse.Content.ReadAsStringAsync(ct));
                var realToken = moneyJson["real"] ?? moneyJson["user"]?["real"] ?? moneyJson["money"]?["real"];
                if (realToken is not null && realToken.Type != JTokenType.Null
                    && decimal.TryParse(realToken.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var balance))
                {
                    result.Balance = balance;
                }
            }

            result.Message = null;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static JArray ParseGroupsAdmin(string body)
    {
        var token = JToken.Parse(body);
        return token switch
        {
            JArray arr => arr,
            JObject obj when obj["data"] is JArray data => data,
            JObject obj when obj["items"] is JArray items => items,
            _ => []
        };
    }

    private static UnicorePlayerCabinetDto MergeApiKeyCabinet(
        UnicorePlayerCabinetDto playtime,
        JArray groupsJson)
    {
        var map = playtime.Servers.ToDictionary(
            s => s.ServerId,
            s => new UnicorePlayerServerDto
            {
                ServerId = s.ServerId,
                ServerName = s.ServerName,
                Playtime = s.Playtime,
                DonateGroups = []
            },
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in groupsJson)
        {
            var server = row["server"];
            var serverId = server?["id"]?.ToString()
                           ?? row["serverId"]?.ToString()
                           ?? string.Empty;
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

            var group = row["group"] ?? row;
            var name = group?["name"]?.ToString() ?? row["groupName"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            DateTime? expired = null;
            var expiredToken = row["expired"];
            if (expiredToken is not null && expiredToken.Type != JTokenType.Null
                && DateTime.TryParse(expiredToken.ToString(), out var parsed))
            {
                expired = DateTime.SpecifyKind(parsed.ToUniversalTime(), DateTimeKind.Utc);
            }

            dto.DonateGroups.Add(new UnicoreDonateGroupDto
            {
                Name = name!,
                IngameId = group?["ingame_id"]?.ToString() ?? group?["ingameId"]?.ToString(),
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
