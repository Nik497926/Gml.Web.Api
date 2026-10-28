namespace Gml.Web.Api.Core.Models.Settings;

public class UnicoreTokensUpdateRequest
{
    public bool UseExternalTokens { get; set; } = true;

    /// <summary>
    /// UnicoreCMS API key (Authorization: Api-Key …).
    /// null — leave unchanged; empty string — clear; otherwise replace.
    /// </summary>
    public string? ApiKey { get; set; }
}
