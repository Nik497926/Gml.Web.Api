using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using Gml.Web.Api.Core.Options;
using GmlCore.Interfaces;
using Newtonsoft.Json.Linq;

namespace Gml.Web.Api.Core.Services;

public class GitHubService : IGitHubService
{
    private readonly IGmlManager _gmlManager;
    private readonly HttpClient _httpClient;
    private ICollection<string> _versions;
    private ICollection<string> _branches;

    public GitHubService(IHttpClientFactory httpClientFactory, IGmlManager gmlManager)
    {
        _gmlManager = gmlManager;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<IEnumerable<string>> GetRepositoryBranches(string user, string repository)
    {
        var url = $"https://api.github.com/repos/{user}/{repository}/branches";

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("request");

        var response = await _httpClient.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            var responseString = await response.Content.ReadAsStringAsync();

            var branches = JArray.Parse(responseString);

            _branches = branches.Select(c => c["name"]!.ToString()).ToArray();
        }

        return _branches ?? Array.Empty<string>();
    }

    public async Task<IEnumerable<string>> GetRepositoryTags(string user, string repository)
    {
        var url = $"https://api.github.com/repos/{user}/{repository}/tags?per_page=100";

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("request");

        var response = await _httpClient.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            var responseString = await response.Content.ReadAsStringAsync();

            var branches = JArray.Parse(responseString);

            _versions = branches.Select(c => c["name"]!.ToString()).ToArray();
            return _versions;
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Published launcher versions only (GitHub Releases / tags). Never includes branch names like master.
    /// Uses git ls-remote as fallback when the GitHub API is rate-limited.
    /// </summary>
    public async Task<IEnumerable<string>> GetLauncherVersions(string user, string repository)
    {
        var versions = (await TryGetPublishedReleaseTags(user, repository)).ToList();

        if (versions.Count == 0)
            versions = (await ListRemoteGitTags($"https://github.com/{user}/{repository}.git")).ToList();

        if (versions.Count == 0)
            versions = (await GetRepositoryTags(user, repository)).ToList();

        return versions
            .Where(v => !string.IsNullOrWhiteSpace(v) && !IsExcludedBranchName(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IEnumerable<string>> TryGetPublishedReleaseTags(string user, string repository)
    {
        try
        {
            var url = $"https://api.github.com/repos/{user}/{repository}/releases?per_page=100";
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("request");

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var releases = JArray.Parse(await response.Content.ReadAsStringAsync());
            return releases
                .Where(r => r["draft"]?.Value<bool>() != true)
                .Select(r => r["tag_name"]?.ToString())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Cast<string>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<IEnumerable<string>> ListRemoteGitTags(string repoUrl)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"ls-remote --tags --refs \"{repoUrl}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return Array.Empty<string>();

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                        return null;

                    const string prefix = "refs/tags/";
                    var refName = parts[^1];
                    return refName.StartsWith(prefix, StringComparison.Ordinal)
                        ? refName[prefix.Length..]
                        : null;
                })
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Cast<string>()
                .OrderByDescending(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsExcludedBranchName(string name) =>
        name.Equals("master", StringComparison.OrdinalIgnoreCase)
        || name.Equals("main", StringComparison.OrdinalIgnoreCase)
        || name.Equals("develop", StringComparison.OrdinalIgnoreCase)
        || name.Equals("dev", StringComparison.OrdinalIgnoreCase)
        || name.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    // public async Task<string> DownloadProject(string projectPath, string branchName, string repoUrl)
    // {
    //
    //
    //     var directory = new DirectoryInfo(projectPath);
    //
    //     if (!directory.Exists) directory.Create();
    //
    //     var zipPath = $"{projectPath}/{branchName}.zip";
    //     var extractPath = NormalizePath(projectPath, branchName);
    //
    //     var url = $"https://github.com/Gml-Launcher/Gml.Launcher/archive/refs/tags/{branchName}.zip";
    //
    //     using (var client = new HttpClient())
    //     {
    //         var stream = await client.GetStreamAsync(url);
    //
    //         await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
    //         {
    //             await stream.CopyToAsync(fileStream);
    //         }
    //     }
    //
    //     // Проверяем, существует ли уже папка для распаковки
    //     if (!Directory.Exists(extractPath))
    //         // Если папка не существует - создаем ее
    //         Directory.CreateDirectory(extractPath);
    //
    //     // Распаковываем архив
    //     ZipFile.ExtractToDirectory(zipPath, extractPath, true);
    //
    //     File.Delete(zipPath);
    //
    //     return new DirectoryInfo(extractPath).GetDirectories().First().FullName;
    // }

    public async Task<string> DownloadProject(string projectPath, string branchName, string repoUrl)
    {
        var directory = new DirectoryInfo(Path.Combine(projectPath, branchName));

        if (!directory.Exists)
        {
            directory.Create();
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"clone --recursive --branch {branchName} {repoUrl} {directory.FullName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = processInfo;
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"An error occurred while cloning the repository: {error}");
        }

        Console.WriteLine(output);

        return directory.FullName;
    }

    private string NormalizePath(string directory, string fileDirectory)
    {
        directory = directory
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        // .TrimStart(Path.DirectorySeparatorChar);

        fileDirectory = fileDirectory
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        return Path.Combine(directory, fileDirectory);
    }

    /// <summary>
    /// Канал SDK, который требует global.json лаунчера (10.0.0 + rollForward latestMajor).
    /// Gml.Core.InstallDotnet ставит только 8.0.302 — этого недостаточно.
    /// </summary>
    private const string LauncherDotnetSdkChannel = "10.0";

    public async Task EditLauncherFiles(string projectPath, string host, string folder)
    {
        var keys = new Dictionary<string, string>
        {
            { "{{HOST}}", host },
            { "{{FOLDER_NAME}}", folder }
        };

        var settingsTemplateFile =
            Path.Combine(projectPath, "src", "Gml.Launcher", "Assets", "Resources",
                "ResourceKeysDictionary.Template.cs");

        var settingsFile =
            Path.Combine(projectPath, "src", "Gml.Launcher", "Assets", "Resources", "ResourceKeysDictionary.cs");

        if (File.Exists(settingsFile))
            File.Delete(settingsFile);

        var content = await File.ReadAllTextAsync(settingsTemplateFile);

        foreach (var key in keys) content = content.Replace(key.Key, key.Value);

        await File.WriteAllTextAsync(settingsFile, content);
    }

    public async Task EnsureLauncherDotnetSdk10(string installationDirectory, Action<string>? log = null)
    {
        var dotnetRoot = ResolveBuildDotnetRoot(installationDirectory);
        Directory.CreateDirectory(dotnetRoot);

        var dotnetExe = Path.Combine(
            dotnetRoot,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        if (File.Exists(dotnetExe) && await HasSdkMajorAsync(dotnetExe, 10))
        {
            log?.Invoke($"NET SDK 10 already present in {dotnetRoot}");
            return;
        }

        log?.Invoke($"Installing .NET SDK {LauncherDotnetSdkChannel} into {dotnetRoot}...");

        if (OperatingSystem.IsWindows())
        {
            await InstallSdkWindowsAsync(dotnetRoot, log);
        }
        else
        {
            await InstallSdkUnixAsync(dotnetRoot, log);
        }

        if (!File.Exists(dotnetExe) || !await HasSdkMajorAsync(dotnetExe, 10))
            throw new InvalidOperationException(
                $"Не удалось установить .NET SDK {LauncherDotnetSdkChannel} в {dotnetRoot}");

        log?.Invoke($"NET SDK {LauncherDotnetSdkChannel} installed.");
    }

    private string ResolveBuildDotnetRoot(string installationDirectory)
    {
        // После InstallDotnet Gml.Core выставляет BuildDotnetPath (часто путь к бинарнику dotnet).
        var buildPath = _gmlManager.LauncherInfo.Settings.SystemProcedures.BuildDotnetPath;
        if (!string.IsNullOrWhiteSpace(buildPath))
        {
            if (buildPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                || buildPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(buildPath);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }

            return buildPath;
        }

        return Path.Combine(installationDirectory, "dotnet");
    }

    private static async Task<bool> HasSdkMajorAsync(string dotnetExe, int major)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dotnetExe,
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.TrimStart().StartsWith($"{major}.", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private async Task InstallSdkUnixAsync(string installDir, Action<string>? log)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"dotnet-install-{Guid.NewGuid():N}.sh");
        try
        {
            await using (var stream = await _httpClient.GetStreamAsync("https://dot.net/v1/dotnet-install.sh"))
            await using (var file = File.Create(scriptPath))
                await stream.CopyToAsync(file);

            var chmod = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (chmod is not null)
                await chmod.WaitForExitAsync();

            await RunProcessAsync(
                "/bin/bash",
                $"\"{scriptPath}\" --channel {LauncherDotnetSdkChannel} --install-dir \"{installDir}\"",
                log);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private async Task InstallSdkWindowsAsync(string installDir, Action<string>? log)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"dotnet-install-{Guid.NewGuid():N}.ps1");
        try
        {
            await using (var stream = await _httpClient.GetStreamAsync("https://dot.net/v1/dotnet-install.ps1"))
            await using (var file = File.Create(scriptPath))
                await stream.CopyToAsync(file);

            await RunProcessAsync(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Channel {LauncherDotnetSdkChannel} -InstallDir \"{installDir}\"",
                log);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private static async Task RunProcessAsync(string fileName, string arguments, Action<string>? log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Не удалось запустить {fileName}");

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log?.Invoke(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Установка .NET SDK завершилась с кодом {process.ExitCode}");
    }

    public Task ApplyBrandTheme(string projectPath, string installationDirectory)
    {
        var brandRoot = Path.Combine(installationDirectory, "LauncherBrand");
        var targetThemes = Path.Combine(projectPath, "src", "Gml.Launcher", "Themes");

        if (!Directory.Exists(brandRoot))
            return Task.CompletedTask;

        Directory.CreateDirectory(targetThemes);

        // LauncherBrand/active.json → Themes/active.json
        var brandActive = Path.Combine(brandRoot, "active.json");
        if (File.Exists(brandActive))
            File.Copy(brandActive, Path.Combine(targetThemes, "active.json"), overwrite: true);

        // LauncherBrand/Themes/* или сразу папки тем в корне LauncherBrand
        var brandThemes = Path.Combine(brandRoot, "Themes");
        var sourceRoot = Directory.Exists(brandThemes) ? brandThemes : brandRoot;

        foreach (var dir in Directory.GetDirectories(sourceRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "samples", StringComparison.OrdinalIgnoreCase))
                continue;

            var dest = Path.Combine(targetThemes, name);
            CopyDirectory(dir, dest);
        }

        // Отдельные файлы theme.json / logo в корне бренда не копируем как темы —
        // только папки и active.json.
        return Task.CompletedTask;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        }
    }
}
