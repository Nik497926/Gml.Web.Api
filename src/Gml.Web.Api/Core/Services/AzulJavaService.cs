using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Gml.Web.Api.Core.Models.Java;
using Gml.Web.Api.Core.Options;

namespace Gml.Web.Api.Core.Services;

public class AzulJavaService(IHttpClientFactory httpClientFactory) : IAzulJavaService
{
    public static readonly (string Os, string Arch)[] AllTargets =
    [
        ("windows", "x86"),
        ("windows", "arm"),
        ("linux", "x86"),
        ("linux", "arm"),
        ("macos", "x86"),
        ("macos", "arm")
    ];

    public async Task<IReadOnlyList<JavaVersionDto>> ListPackagesAsync(
        int majorVersion,
        string? os = null,
        string? arch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(os) || string.IsNullOrWhiteSpace(arch))
            return await ListLatestForAllTargetsAsync(majorVersion, cancellationToken);

        return await ListPackagesForTargetAsync(majorVersion, os, arch, cancellationToken);
    }

    public async Task<IReadOnlyList<JavaVersionDto>> ListLatestForAllTargetsAsync(
        int majorVersion,
        CancellationToken cancellationToken = default)
    {
        var tasks = AllTargets
            .Select(t => ListPackagesForTargetAsync(majorVersion, t.Os, t.Arch, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(r => r.Take(1))
            .ToList();
    }

    private async Task<IReadOnlyList<JavaVersionDto>> ListPackagesForTargetAsync(
        int majorVersion,
        string os,
        string arch,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.AzulMetadata);
            var url =
                $"zulu/packages/?java_version={majorVersion}" +
                $"&os={Uri.EscapeDataString(os)}" +
                $"&arch={Uri.EscapeDataString(arch)}" +
                "&java_package_type=jdk" +
                "&archive_type=zip" +
                "&javafx_bundled=false" +
                "&release_status=ga" +
                "&availability_types=CA" +
                "&latest=true" +
                "&page=1&page_size=5";

            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            var packages = await response.Content.ReadFromJsonAsync<List<AzulPackage>>(cancellationToken: cancellationToken)
                           ?? [];

            return packages
                .Where(p => !string.IsNullOrWhiteSpace(p.DownloadUrl))
                .Select(p => new JavaVersionDto
                {
                    Name = string.IsNullOrWhiteSpace(p.Name)
                        ? $"Zulu {majorVersion}"
                        : Path.GetFileNameWithoutExtension(p.Name),
                    Version = FormatJavaVersion(p.JavaVersion) ?? p.Name ?? majorVersion.ToString(),
                    MajorVersion = p.JavaVersion is { Count: > 0 } ? p.JavaVersion[0] : majorVersion,
                    Source = JavaRuntimeSource.Azul,
                    DownloadUrl = p.DownloadUrl,
                    PackageUuid = p.PackageUuid,
                    Os = os,
                    Arch = arch,
                    Recommended = false
                })
                .GroupBy(p => p.PackageUuid ?? p.DownloadUrl)
                .Select(g => g.First())
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static string DetectOs()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        return "linux";
    }

    public static string DetectArch()
    {
        // Azul Metadata API: x64 Windows/Linux often filtered as arch=x86 (returns win_x64 / linux_x64).
        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm",
            _ => "x86"
        };
    }

    public static string RuntimeKey(string os, string arch) => $"{os}-{arch}".ToLowerInvariant();

    /// <summary>
    /// Map launcher OsType string / OsArch ("64", "arm64", …) to Azul runtime key.
    /// </summary>
    public static string? ResolveRuntimeKey(string? osName, string? osArch)
    {
        var os = NormalizeOs(osName);
        var arch = NormalizeArch(osArch);
        if (os is null || arch is null)
            return null;
        return RuntimeKey(os, arch);
    }

    public static string? NormalizeOs(string? osName)
    {
        if (string.IsNullOrWhiteSpace(osName))
            return null;

        var v = osName.Trim().ToLowerInvariant();
        return v switch
        {
            "windows" or "win" or "win32" or "0" => "windows",
            "linux" or "1" => "linux",
            "macos" or "osx" or "mac" or "darwin" or "2" => "macos",
            _ => v is "windows" or "linux" or "macos" ? v : null
        };
    }

    public static string? NormalizeArch(string? osArch)
    {
        if (string.IsNullOrWhiteSpace(osArch))
            return null;

        var v = osArch.Trim().ToLowerInvariant();
        return v switch
        {
            "64" or "x64" or "amd64" or "x86_64" or "86" or "x86" => "x86",
            "arm64" or "aarch64" or "arm" => "arm",
            "32" => "x86",
            _ => null
        };
    }

    private static string? FormatJavaVersion(List<int>? parts)
    {
        if (parts is null || parts.Count == 0) return null;
        return string.Join('.', parts);
    }

    private sealed class AzulPackage
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("package_uuid")]
        public string? PackageUuid { get; set; }

        [JsonPropertyName("java_version")]
        public List<int>? JavaVersion { get; set; }
    }
}
