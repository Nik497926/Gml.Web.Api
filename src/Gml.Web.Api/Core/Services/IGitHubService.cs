namespace Gml.Web.Api.Core.Services;

public interface IGitHubService
{
    Task<IEnumerable<string>> GetRepositoryBranches(string user, string repository);
    Task<IEnumerable<string>> GetRepositoryTags(string user, string repository);
    Task<IEnumerable<string>> GetLauncherVersions(string user, string repository);
    Task<string> DownloadProject(string projectPath, string branchName, string repoUrl);
    Task EditLauncherFiles(string projectPath, string host, string folder);

    /// <summary>
    /// Копирует брендовый шаблон из {installationDirectory}/LauncherBrand в Themes проекта лаунчера.
    /// Используется при сборке из панели: владелец кладёт шаблон один раз, игрок тему не выбирает.
    /// </summary>
    Task ApplyBrandTheme(string projectPath, string installationDirectory);
}
