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

        // Kernel — служебный пользователь Unicore (API-ключи / внутренности).
        // Публичный профиль и кабинет для него всегда 404 — не считаем это ошибкой интеграции.
        if (IsUnicoreKernelUser(player.Name))
        {
            return new UnicorePlayerCabinetDto
            {
                Available = false,
                Message = "Kernel — системный пользователь Unicore, кабинет недоступен"
            };
        }

        var baseUri = new Uri(authService.Endpoint);
        var host = $"{baseUri.Scheme}://{baseUri.Host}";
        var cabinetUrl = $"{host}/cabinet";

        // Публичный профиль: playtime + UUID Unicore (часто ≠ UUID в Gml)
        var (publicCabinet, unicoreUuid) = await TryLoadPublicProfileAsync(host, player.Name, ct);
        var unicoreUserUuid = !string.IsNullOrWhiteSpace(unicoreUuid) ? unicoreUuid! : player.Uuid;

        string? apiKeyError = null;

        // 1) Админский путь: Unicore API key (не зависит от сессии игрока и от тоггла токенов)
        var apiKey = unicoreAuthOptions.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var fromApiKey = await TryLoadWithApiKeyAsync(host, apiKey, unicoreUserUuid, publicCabinet, ct);
            if (fromApiKey.Cabinet is not null)
            {
                fromApiKey.Cabinet.Available = true;
                fromApiKey.Cabinet.CabinetUrl = cabinetUrl;
                fromApiKey.Cabinet.TotalPlaytime = fromApiKey.Cabinet.Servers.Sum(s => s.Playtime);
                return fromApiKey.Cabinet;
            }

            apiKeyError = fromApiKey.Error;
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
        publicCabinet.Available = true;
        publicCabinet.CabinetUrl = cabinetUrl;
        publicCabinet.TotalPlaytime = publicCabinet.Servers.Sum(s => s.Playtime);
        publicCabinet.Message = string.IsNullOrWhiteSpace(apiKey)
            ? "Привилегии недоступны: задайте API-ключ Unicore в настройках интеграции или нужен вход игрока через лаунчер."
            : $"Привилегии недоступны: API-ключ не прочитал кабинет ({apiKeyError ?? "unknown"}). UUID Unicore={unicoreUserUuid}. Проверьте IP allowlist (IP бэкенда Gml) и права admin.dashboard + admin.users.read.";
        return publicCabinet;
    }

    private static bool IsUnicoreKernelUser(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Equals("Kernel", StringComparison.OrdinalIgnoreCase);

    private async Task<(UnicorePlayerCabinetDto? Cabinet, string? Error)> TryLoadWithApiKeyAsync(
        string host,
        string apiKey,
        string unicoreUserUuid,
        UnicorePlayerCabinetDto playtime,
        CancellationToken ct)
    {
        try
        {
            // Не дергаем GET /users/{uuid}: для Kernel и части скрытых профилей Unicore отвечает 404,
            // хотя groups/money с API-ключом при этом могут быть валидны.
            using var groupsRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{host}/donates/groups/admin/{Uri.EscapeDataString(unicoreUserUuid)}");
            groupsRequest.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {apiKey}");

            using var moneyRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{host}/cabinet/money/admin/{Uri.EscapeDataString(unicoreUserUuid)}");
            moneyRequest.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {apiKey}");

            var groupsTask = _httpClient.SendAsync(groupsRequest, ct);
            var moneyTask = _httpClient.SendAsync(moneyRequest, ct);
            await Task.WhenAll(groupsTask, moneyTask);

            var groupsResponse = await groupsTask;
            var moneyResponse = await moneyTask;

            if (!groupsResponse.IsSuccessStatusCode && !moneyResponse.IsSuccessStatusCode)
            {
                return (null,
                    $"groups={(int)groupsResponse.StatusCode}, money={(int)moneyResponse.StatusCode}");
            }

            var groupsJson = groupsResponse.IsSuccessStatusCode
                ? ParseGroupsAdmin(await groupsResponse.Content.ReadAsStringAsync(ct))
                : [];

            var result = MergeApiKeyCabinet(playtime, groupsJson);

            // /cabinet/money/admin/:uuid → Money[] (валюта на серверах), не объект с "real"
            if (moneyResponse.IsSuccessStatusCode)
                TryApplyMoneyPayload(result, await moneyResponse.Content.ReadAsStringAsync(ct));

            // Баланс "real" лежит на пользователе — отдельно, 404 (Kernel/скрытый) не валит кабинет
            if (result.Balance is null)
            {
                using var userRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{host}/users/{Uri.EscapeDataString(unicoreUserUuid)}");
                userRequest.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {apiKey}");
                using var userResponse = await _httpClient.SendAsync(userRequest, ct);
                if (userResponse.IsSuccessStatusCode)
                    TryApplyUserRealBalance(result, await userResponse.Content.ReadAsStringAsync(ct));
            }

            result.Message = null;
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void TryApplyMoneyPayload(UnicorePlayerCabinetDto result, string body)
    {
        try
        {
            var token = JToken.Parse(body);
            // Объект с real (на случай другого формата)
            if (token is JObject)
            {
                TryApplyUserRealBalance(result, body);
                return;
            }

            // Массив Money[] — игровые балансы по серверам, shop "real" здесь нет
            _ = token as JArray;
        }
        catch
        {
            // не ломаем кабинет из‑за формата money
        }
    }

    private static void TryApplyUserRealBalance(UnicorePlayerCabinetDto result, string body)
    {
        try
        {
            var token = JToken.Parse(body);
            if (token is not JObject obj)
                return;

            var realToken = obj["real"] ?? obj["user"]?["real"] ?? obj["money"]?["real"];
            if (realToken is null || realToken.Type == JTokenType.Null)
                return;

            if (decimal.TryParse(realToken.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var balance))
            {
                result.Balance = balance;
            }
        }
        catch
        {
            // ignore
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

    private async Task<(UnicorePlayerCabinetDto Cabinet, string? UnicoreUuid)> TryLoadPublicProfileAsync(
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
                return (new UnicorePlayerCabinetDto
                {
                    Servers = [],
                    Message = "Не удалось получить публичный профиль Unicore"
                }, null);
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            var playtimes = json["playtimes"] as JArray ?? [];
            var uuid = json["uuid"]?.ToString();
            return (MergeServers(playtimes, []), string.IsNullOrWhiteSpace(uuid) ? null : uuid);
        }
        catch
        {
            return (new UnicorePlayerCabinetDto
            {
                Servers = [],
                Message = "Ошибка запроса к UnicoreCMS"
            }, null);
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
