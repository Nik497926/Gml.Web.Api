using Gml.Web.Api.Core.Models.Java;

namespace Gml.Web.Api.Core.Services;

public interface IAzulJavaService
{
    /// <summary>
    /// List packages for a single Azul OS/arch target.
    /// When <paramref name="os"/> / <paramref name="arch"/> are null, they are not defaulted to the host —
    /// pass explicit values or use <see cref="ListLatestForAllTargetsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<JavaVersionDto>> ListPackagesAsync(
        int majorVersion,
        string? os = null,
        string? arch = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Latest package per (os, arch) target for a Java major (windows/linux/macos × x86/arm).
    /// </summary>
    Task<IReadOnlyList<JavaVersionDto>> ListLatestForAllTargetsAsync(
        int majorVersion,
        CancellationToken cancellationToken = default);
}
