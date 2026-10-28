using System.Net;
using System.Security.Claims;
using Gml.Dto.Messages;
using Gml.Web.Api.Core.Models.FileManager;
using Gml.Web.Api.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Gml.Web.Api.Core.Handlers;

public static class FileManagerHandler
{
    public static async Task<IResult> ListEntries(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromQuery] string scope,
        [FromQuery] string? path,
        [FromQuery] string? profileName)
    {
        var authError = EnsureScopeAccess(httpContext.User, scope);
        if (authError is not null)
            return authError;

        try
        {
            var root = await fileManager.ResolveRootAsync(scope, profileName);
            var entries = await fileManager.ListAsync(root, path);
            return Results.Ok(ResponseMessage.Create(entries, "Список файлов получен", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public static async Task<IResult> ReadContent(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromQuery] string scope,
        [FromQuery] string path,
        [FromQuery] string? profileName)
    {
        var authError = EnsureScopeAccess(httpContext.User, scope);
        if (authError is not null)
            return authError;

        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(ResponseMessage.Create("Не указан path", HttpStatusCode.BadRequest));

        try
        {
            var root = await fileManager.ResolveRootAsync(scope, profileName);
            var content = await fileManager.ReadAsync(root, path);
            if (content.IsBinary)
            {
                return Results.Json(
                    ResponseMessage.Create(content, "Файл бинарный или слишком большой для редактирования",
                        HttpStatusCode.UnsupportedMediaType),
                    statusCode: (int)HttpStatusCode.UnsupportedMediaType);
            }

            return Results.Ok(ResponseMessage.Create(content, "Содержимое файла получено", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public static async Task<IResult> WriteContent(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromBody] FileManagerWriteRequest request)
    {
        var authError = EnsureScopeAccess(httpContext.User, request.Scope);
        if (authError is not null)
            return authError;

        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(ResponseMessage.Create("Не указан path", HttpStatusCode.BadRequest));

        try
        {
            var root = await fileManager.ResolveRootAsync(request.Scope, request.ProfileName);
            await fileManager.WriteAsync(root, request.Path, request.Content ?? string.Empty);
            return Results.Ok(ResponseMessage.Create("Файл сохранён", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public static async Task<IResult> DeleteEntries(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromBody] FileManagerDeleteRequest request)
    {
        var authError = EnsureScopeAccess(httpContext.User, request.Scope);
        if (authError is not null)
            return authError;

        if (request.Paths is null || request.Paths.Count == 0)
            return Results.BadRequest(ResponseMessage.Create("Не указаны paths", HttpStatusCode.BadRequest));

        try
        {
            var root = await fileManager.ResolveRootAsync(request.Scope, request.ProfileName);
            await fileManager.DeleteAsync(root, request.Paths);
            return Results.Ok(ResponseMessage.Create("Удаление выполнено", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public static async Task<IResult> Download(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromQuery] string scope,
        [FromQuery] string path,
        [FromQuery] string? profileName)
    {
        var authError = EnsureScopeAccess(httpContext.User, scope);
        if (authError is not null)
            return authError;

        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(ResponseMessage.Create("Не указан path", HttpStatusCode.BadRequest));

        try
        {
            var root = await fileManager.ResolveRootAsync(scope, profileName);
            var (stream, fileName) = await fileManager.OpenDownloadAsync(root, path);
            return Results.File(stream, "application/octet-stream", fileName, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public static async Task<IResult> Upload(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromQuery] string scope,
        [FromQuery] string? path,
        [FromQuery] string? profileName,
        [FromQuery] bool overwrite = true)
    {
        var authError = EnsureScopeAccess(httpContext.User, scope);
        if (authError is not null)
            return authError;

        if (!httpContext.Request.HasFormContentType)
            return Results.BadRequest(ResponseMessage.Create("Ожидается multipart/form-data", HttpStatusCode.BadRequest));

        try
        {
            var form = await httpContext.Request.ReadFormAsync();
            var files = form.Files;
            if (files.Count == 0)
                return Results.BadRequest(ResponseMessage.Create("Не переданы файлы", HttpStatusCode.BadRequest));

            var root = await fileManager.ResolveRootAsync(scope, profileName);
            var uploaded = new List<object>(files.Count);

            foreach (var file in files)
            {
                if (file.Length <= 0)
                    continue;

                await using var stream = file.OpenReadStream();
                var relativePath = await fileManager.UploadAsync(
                    root,
                    path,
                    file.FileName,
                    stream,
                    overwrite);
                uploaded.Add(new { relativePath, name = Path.GetFileName(relativePath), size = file.Length });
            }

            if (uploaded.Count == 0)
                return Results.BadRequest(ResponseMessage.Create("Пустые файлы не загружены", HttpStatusCode.BadRequest));

            return Results.Ok(ResponseMessage.Create(uploaded, "Файлы загружены", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public static async Task<IResult> Archive(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromBody] FileManagerArchiveRequest request)
    {
        var authError = EnsureScopeAccess(httpContext.User, request.Scope);
        if (authError is not null)
            return authError;

        try
        {
            var root = await fileManager.ResolveRootAsync(request.Scope, request.ProfileName);
            var archivePath = await fileManager.ArchiveAsync(root, request.Paths, request.ArchiveName);
            return Results.Ok(ResponseMessage.Create(new { relativePath = archivePath }, "Архив создан",
                HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public static async Task<IResult> Extract(
        HttpContext httpContext,
        IFileManagerService fileManager,
        [FromBody] FileManagerExtractRequest request)
    {
        var authError = EnsureScopeAccess(httpContext.User, request.Scope);
        if (authError is not null)
            return authError;

        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(ResponseMessage.Create("Не указан path", HttpStatusCode.BadRequest));

        try
        {
            var root = await fileManager.ResolveRootAsync(request.Scope, request.ProfileName);
            await fileManager.ExtractAsync(root, request.Path, request.Destination);
            return Results.Ok(ResponseMessage.Create("Архив распакован", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    private static IResult? EnsureScopeAccess(ClaimsPrincipal user, string? scope)
    {
        if (!FileManagerScope.IsValid(scope))
        {
            return Results.BadRequest(ResponseMessage.Create(
                "Некорректный scope. Ожидается global или profile", HttpStatusCode.BadRequest));
        }

        if (user.IsInRole("Admin")
            || user.Claims.Any(c => c.Type == ClaimTypes.Role && c.Value == "Admin"))
        {
            return null;
        }

        var required = FileManagerScope.RequiredPermission(scope!);
        var hasPerm = user.Claims.Any(c =>
            c.Type == "perm" && string.Equals(c.Value, required, StringComparison.OrdinalIgnoreCase));

        if (!hasPerm)
        {
            return Results.Json(
                ResponseMessage.Create("Недостаточно прав", HttpStatusCode.Forbidden),
                statusCode: (int)HttpStatusCode.Forbidden);
        }

        return null;
    }

    private static IResult MapException(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Results.Json(
            ResponseMessage.Create(ex.Message, HttpStatusCode.Forbidden),
            statusCode: (int)HttpStatusCode.Forbidden),
        DirectoryNotFoundException or FileNotFoundException => Results.NotFound(
            ResponseMessage.Create(ex.Message, HttpStatusCode.NotFound)),
        ArgumentException or InvalidOperationException => Results.BadRequest(
            ResponseMessage.Create(ex.Message, HttpStatusCode.BadRequest)),
        _ => Results.BadRequest(ResponseMessage.Create(ex.Message, HttpStatusCode.BadRequest))
    };
}
