namespace Gml.Web.Api.Core.Services;

public interface IGitHubService
{
    Task<IEnumerable<string>> GetRepositoryBranches(string user, string repository);
    Task<IEnumerable<string>> GetRepositoryTags(string user, string repository);
    Task<IEnumerable<string>> GetLauncherVersions(string user, string repository);
    Task<string> DownloadProject(string projectPath, string branchName, string repoUrl);
    Task EditLauncherFiles(string projectPath, string host, string folder);

    /// <summary>
    /// Доустанавливает .NET SDK 10 в каталог BuildDotnet (Gml.Core InstallDotnet кладёт только 8.0.302,
    /// а лаунчер в global.json требует 10.0.0).
    /// </summary>
    Task EnsureLauncherDotnetSdk10(string installationDirectory, Action<string>? log = null);

    /// <summary>
    /// Копирует брендовый шаблон из {installationDirectory}/LauncherBrand в Themes проекта лаунчера.
    /// Используется при сборке из панели: владелец кладёт шаблон один раз, игрок тему не выбирает.
    /// </summary>
    Task ApplyBrandTheme(string projectPath, string installationDirectory);
}
