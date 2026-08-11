using GmlCore.Interfaces.Enums;
using DomainSettings = Gml.Domains.Settings.Settings;
using StorageTypeEnum = GmlCore.Interfaces.Enums.StorageType;
using TextureProtocolEnum = GmlCore.Interfaces.Enums.TextureProtocol;

namespace Gml.Web.Api.Core.Models.Settings;

/// <summary>
/// Flexible body for PUT /settings/platform.
/// Uses int/string instead of enum/TimeSpan so client JSON never fails STJ binding.
/// </summary>
public class SettingsUpdateRequest
{
    public bool RegistrationIsEnabled { get; set; }
    public int StorageType { get; set; }
    public string? StorageHost { get; set; }
    public string? StorageLogin { get; set; }
    public string? CurseForgeKey { get; set; }
    public string? VkKey { get; set; }
    public string? StoragePassword { get; set; }
    public int TextureProtocol { get; set; }
    public bool SentryNeedAutoClear { get; set; }
    public string? SentryAutoClearPeriod { get; set; }

    /// <summary>
    /// true — AccessToken игрока из Unicore; false — JWT Gml.
    /// </summary>
    public bool UnicoreUseExternalTokens { get; set; } = true;

    public DomainSettings ToDomain(DomainSettings? previous)
    {
        TimeSpan period;
        if (string.IsNullOrWhiteSpace(SentryAutoClearPeriod))
        {
            period = previous?.SentryAutoClearPeriod ?? TimeSpan.FromMinutes(5);
        }
        else if (!TimeSpan.TryParse(SentryAutoClearPeriod, out period))
        {
            period = previous?.SentryAutoClearPeriod ?? TimeSpan.FromMinutes(5);
        }

        var password = StoragePassword;
        if (string.IsNullOrWhiteSpace(password) && previous is not null)
            password = previous.StoragePassword;

        var textureProtocol = Enum.IsDefined(typeof(TextureProtocolEnum), TextureProtocol)
            ? (TextureProtocolEnum)TextureProtocol
            : previous?.TextureProtocol ?? TextureProtocolEnum.Https;

        var storageType = Enum.IsDefined(typeof(StorageTypeEnum), StorageType)
            ? (StorageTypeEnum)StorageType
            : previous?.StorageType ?? StorageTypeEnum.LocalStorage;

        return new DomainSettings
        {
            RegistrationIsEnabled = RegistrationIsEnabled,
            StorageType = storageType,
            StorageHost = StorageHost ?? previous?.StorageHost ?? string.Empty,
            StorageLogin = StorageLogin ?? previous?.StorageLogin ?? string.Empty,
            StoragePassword = password ?? string.Empty,
            CurseForgeKey = CurseForgeKey ?? previous?.CurseForgeKey ?? string.Empty,
            VkKey = VkKey ?? previous?.VkKey ?? string.Empty,
            TextureProtocol = textureProtocol,
            SentryNeedAutoClear = SentryNeedAutoClear,
            SentryAutoClearPeriod = period
        };
    }
}
