using System.Net;
using AutoMapper;
using FluentValidation;
using Gml.Dto.Files;
using Gml.Dto.Messages;
using Gml.Models.System;
using GmlCore.Interfaces;
using GmlCore.Interfaces.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gml.Web.Api.Core.Handlers;

public class FileHandler : IFileHandler
{
    public static async Task<IResult> GetFile(HttpContext context, IGmlManager gmlManager, string fileHash)
    {
        // var response = context.Response;

        var (file, fileName, length) = await gmlManager.Files.GetFileStream(fileHash);

        try
        {
            // response.Headers.Append("Content-Disposition", $"attachment; filename={fileName}");
            // response.Headers.Append("Content-Length", length.ToString());
            // response.ContentType = "application/octet-stream";
            //
            // await file.CopyToAsync(response.Body);

            return Results.File(file, "application/octet-stream", fileName);
        }
        catch (Exception exception)
        {
            Console.WriteLine(fileName + exception);
            gmlManager.BugTracker.CaptureException(exception);
        }

        return Results.NotFound();
    }

    [Authorize]
    public static async Task<IResult> AddFileWhiteList(
        IGmlManager manager,
        IValidator<List<FileWhiteListDto>> validator,
        [FromBody] List<FileWhiteListDto> fileDto)
    {
        var result = await validator.ValidateAsync(fileDto);

        if (!result.IsValid)
            return Results.BadRequest(ResponseMessage.Create(result.Errors, "Ошибка валидации",
                HttpStatusCode.BadRequest));

        foreach (var item in fileDto)
            item.Directory = NormalizeWhitelistPath(item.Directory);

        fileDto = fileDto
            .Where(c => !string.IsNullOrWhiteSpace(c.Directory))
            .DistinctBy(c => c.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profileNames = fileDto.GroupBy(c => c.ProfileName);

        foreach (var profileFiles in profileNames)
        {
            var profile = await manager.Profiles.GetProfile(profileFiles.Key);

            if (profile == null)
                return Results.NotFound(ResponseMessage.Create($"Профиль с именем \"{profileFiles.Key}\" не найден",
                    HttpStatusCode.NotFound));

            foreach (var fileInfo in profileFiles)
            {
                var alreadyListed = profile.FileWhiteList.Any(existing =>
                    PathsEqual(existing.Directory, fileInfo.Directory));

                if (alreadyListed)
                    continue;

                await manager.Profiles.AddFileToWhiteList(profile, new LocalFileInfo(fileInfo.Directory));
            }
        }

        return Results.Ok(ResponseMessage.Create($"\"{fileDto.Count}\" файлов было успешно добавлено в White-Лист",
            HttpStatusCode.OK));
    }

    [Authorize]
    public static async Task<IResult> RemoveFileWhiteList(
        IGmlManager manager,
        IValidator<List<FileWhiteListDto>> validator,
        [FromBody] List<FileWhiteListDto> fileDto)
    {
        var result = await validator.ValidateAsync(fileDto);

        if (!result.IsValid)
            return Results.BadRequest(ResponseMessage.Create(result.Errors, "Ошибка валидации",
                HttpStatusCode.BadRequest));

        foreach (var item in fileDto)
            item.Directory = NormalizeWhitelistPath(item.Directory);

        fileDto = fileDto
            .Where(c => !string.IsNullOrWhiteSpace(c.Directory))
            .DistinctBy(c => c.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profileNames = fileDto.GroupBy(c => c.ProfileName);

        foreach (var profileFiles in profileNames)
        {
            var profile = await manager.Profiles.GetProfile(profileFiles.Key);

            if (profile == null)
                return Results.NotFound(ResponseMessage.Create($"Профиль с именем \"{profileFiles.Key}\" не найден",
                    HttpStatusCode.NotFound));

            var targets = profileFiles
                .Select(c => c.Directory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toRemove = profile.FileWhiteList
                .Where(existing => targets.Contains(NormalizeWhitelistPath(existing.Directory)))
                .ToList();

            foreach (var fileInfo in toRemove)
            {
                await manager.Profiles.RemoveFileFromWhiteList(profile, fileInfo);
            }
        }

        return Results.Ok(ResponseMessage.Create($"\"{fileDto.Count}\" файлов было успешно удалено из White-Листа",
            HttpStatusCode.OK));
    }

    public static async Task<IResult> AddFolderWhiteList(
        IGmlManager manager,
        IMapper mapper,
        IValidator<List<FolderWhiteListDto>> validator,
        [FromBody] List<FolderWhiteListDto> folderDto)
    {
        var result = await validator.ValidateAsync(folderDto);

        if (!result.IsValid)
            return Results.BadRequest(ResponseMessage.Create(result.Errors, "Ошибка валидации",
                HttpStatusCode.BadRequest));

        foreach (var item in folderDto)
            item.Path = NormalizeWhitelistPath(item.Path);

        folderDto = folderDto
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profileNames = folderDto.GroupBy(c => c.ProfileName);

        foreach (var profileFolders in profileNames)
        {
            var profile = await manager.Profiles.GetProfile(profileFolders.Key);

            if (profile == null)
                return Results.NotFound(ResponseMessage.Create($"Профиль с именем \"{profileFolders.Key}\" не найден",
                    HttpStatusCode.NotFound));

            var foldersForProfile = profileFolders
                .Where(folder => !profile.FolderWhiteList.Any(existing =>
                    PathsEqual(existing.Path, folder.Path)))
                .Select(folder => new LocalFolderInfo(folder.Path))
                .ToList();

            if (foldersForProfile.Count == 0)
                continue;

            await manager.Profiles.AddFolderToWhiteList(profile, foldersForProfile);
        }

        return Results.Ok(ResponseMessage.Create($"\"{folderDto.Count}\" папок было успешно добавлено в White-Лист",
            HttpStatusCode.OK));
    }

    public static async Task<IResult> RemoveFolderWhiteList(
        IGmlManager manager,
        IMapper mapper,
        IValidator<List<FolderWhiteListDto>> validator,
        [FromBody] List<FolderWhiteListDto> folderDto)
    {
        var result = await validator.ValidateAsync(folderDto);

        if (!result.IsValid)
            return Results.BadRequest(ResponseMessage.Create(result.Errors, "Ошибка валидации",
                HttpStatusCode.BadRequest));

        foreach (var item in folderDto)
            item.Path = NormalizeWhitelistPath(item.Path);

        folderDto = folderDto
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profileNames = folderDto.GroupBy(c => c.ProfileName);

        foreach (var profileFolders in profileNames)
        {
            var profile = await manager.Profiles.GetProfile(profileFolders.Key);

            if (profile == null)
                return Results.NotFound(ResponseMessage.Create($"Профиль с именем \"{profileFolders.Key}\" не найден",
                    HttpStatusCode.NotFound));

            var targets = profileFolders
                .Select(c => c.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toRemove = profile.FolderWhiteList
                .Where(existing => targets.Contains(NormalizeWhitelistPath(existing.Path)))
                .ToList();

            if (toRemove.Count == 0)
                continue;

            await manager.Profiles.RemoveFolderFromWhiteList(profile, toRemove);
        }

        return Results.Ok(ResponseMessage.Create($"\"{folderDto.Count}\" папок было успешно удалено из White-Лист",
            HttpStatusCode.OK));
    }

    /// <summary>
    /// Единый вид пути белого списка: `/` как разделитель, без ведущих/хвостовых слэшей.
    /// </summary>
    internal static string NormalizeWhitelistPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Trim()
            .Replace('\\', '/')
            .Trim('/');
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(NormalizeWhitelistPath(left), NormalizeWhitelistPath(right), StringComparison.OrdinalIgnoreCase);
}
