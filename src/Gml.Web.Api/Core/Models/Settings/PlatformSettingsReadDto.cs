using Gml.Dto.Settings;

namespace Gml.Web.Api.Core.Models.Settings;

/// <summary>
/// SettingsReadDto + локальный флаг Unicore (не в Gml.Dto).
/// </summary>
public class PlatformSettingsReadDto : SettingsReadDto
{
    /// <summary>
    /// true — токен игрока из Unicore; false — JWT Gml (кабинет через сохранённый Unicore refresh).
    /// </summary>
    public bool UnicoreUseExternalTokens { get; set; } = true;
}
