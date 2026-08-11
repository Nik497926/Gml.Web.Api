namespace Gml.Web.Api.Core.Models.Java;

public class ProfileJavaMeta
{
    public string Source { get; set; } = JavaRuntimeSource.Default;
    public int JavaMajor { get; set; }
    public string? RuntimeId { get; set; }
    /// <summary>Legacy / display path (usually host or first available runtime).</summary>
    public string? JavaPath { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? PackageUuid { get; set; }

    /// <summary>
    /// Per-target runtimes. Key: "{os}-{arch}" e.g. "windows-x86", "linux-arm".
    /// </summary>
    public Dictionary<string, ProfileJavaRuntimeEntry> Runtimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ProfileJavaRuntimeEntry
{
    public string? RuntimeId { get; set; }
    public string? JavaPath { get; set; }
    public string? DownloadUrl { get; set; }
    public string? PackageUuid { get; set; }
    public string? Os { get; set; }
    public string? Arch { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
}
