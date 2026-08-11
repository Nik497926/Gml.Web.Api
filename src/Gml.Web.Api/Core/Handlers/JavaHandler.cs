using System.Net;
using Gml.Dto.Messages;
using Gml.Web.Api.Core.Models.Java;
using Gml.Web.Api.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Gml.Web.Api.Core.Handlers;

public static class JavaHandler
{
    public static IResult Recommend(
        IJavaRecommendationService recommendation,
        [FromQuery] string? minecraftVersion)
    {
        var dto = recommendation.Recommend(minecraftVersion);
        return Results.Ok(ResponseMessage.Create(dto, "Рекомендация Java получена", HttpStatusCode.OK));
    }

    public static async Task<IResult> ListVersions(
        IJavaRecommendationService recommendation,
        IAzulJavaService azul,
        [FromQuery] string? minecraftVersion,
        [FromQuery] string? os,
        [FromQuery] string? arch)
    {
        try
        {
            var recommendedMajor = recommendation.RecommendMajor(minecraftVersion);
            var majors = new[] { recommendedMajor, 8, 17, 21 }
                .Distinct()
                .ToArray();

            var result = new List<JavaVersionDto>
            {
                new()
                {
                    Name = "По умолчанию",
                    Version = "default",
                    MajorVersion = recommendedMajor,
                    Source = JavaRuntimeSource.Default,
                    Recommended = true
                }
            };

            // One UI row per major: selecting Azul Zulu binds JDK for all OS × arch.
            foreach (var major in majors)
            {
                try
                {
                    var packages = string.IsNullOrWhiteSpace(os) || string.IsNullOrWhiteSpace(arch)
                        ? await azul.ListLatestForAllTargetsAsync(major)
                        : await azul.ListPackagesAsync(major, os, arch);

                    if (packages.Count == 0)
                        continue;

                    var sample = packages.First();
                    var platforms = packages
                        .Select(p => $"{p.Os}/{p.Arch}")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s)
                        .ToArray();

                    result.Add(new JavaVersionDto
                    {
                        Name = $"Azul Zulu {major}",
                        Version = sample.Version,
                        MajorVersion = major,
                        Source = JavaRuntimeSource.Azul,
                        // No single-OS downloadUrl — AssignAzul downloads the full matrix by major.
                        DownloadUrl = null,
                        PackageUuid = null,
                        Os = null,
                        Arch = null,
                        Recommended = major == recommendedMajor
                    });

                    _ = platforms; // reserved for future UI detail
                }
                catch
                {
                    // Azul may be unreachable for some majors — skip
                }
            }

            var ordered = result
                .OrderBy(v => v.Source == JavaRuntimeSource.Default ? 0 : 1)
                .ThenByDescending(v => v.Recommended)
                .ThenByDescending(v => v.MajorVersion)
                .ThenBy(v => v.Name)
                .ToList();

            return Results.Ok(ResponseMessage.Create(ordered, "Список версий Java получен", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(ResponseMessage.Create(ex.Message, HttpStatusCode.BadRequest));
        }
    }

    public static async Task<IResult> AssignAzul(
        string profileName,
        IJavaRuntimeService runtimeService,
        [FromBody] JavaAzulAssignRequest request)
    {
        try
        {
            var meta = await runtimeService.AssignAzulAsync(profileName, request);
            return Results.Ok(ResponseMessage.Create(meta, "Java Azul привязана к профилю", HttpStatusCode.OK));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.NotFound(ResponseMessage.Create(ex.Message, HttpStatusCode.NotFound));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(ResponseMessage.Create(ex.Message, HttpStatusCode.BadRequest));
        }
    }

    public static async Task<IResult> Upload(
        string profileName,
        HttpContext context,
        IJavaRuntimeService runtimeService)
    {
        try
        {
            var file = context.Request.Form.Files.GetFile("file")
                       ?? context.Request.Form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(ResponseMessage.Create("Файл не передан", HttpStatusCode.BadRequest));

            await using var stream = file.OpenReadStream();
            var meta = await runtimeService.AssignUploadAsync(profileName, stream, file.FileName);
            return Results.Ok(ResponseMessage.Create(meta, "Своя Java загружена и привязана", HttpStatusCode.OK));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.NotFound(ResponseMessage.Create(ex.Message, HttpStatusCode.NotFound));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(ResponseMessage.Create(ex.Message, HttpStatusCode.BadRequest));
        }
    }

    public static async Task<IResult> SetDefault(
        string profileName,
        IJavaRuntimeService runtimeService)
    {
        try
        {
            var meta = await runtimeService.SetDefaultAsync(profileName);
            return Results.Ok(ResponseMessage.Create(meta, "Выбрана Java по умолчанию", HttpStatusCode.OK));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.NotFound(ResponseMessage.Create(ex.Message, HttpStatusCode.NotFound));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(ResponseMessage.Create(ex.Message, HttpStatusCode.BadRequest));
        }
    }

    public static async Task<IResult> GetMeta(
        string profileName,
        IJavaRuntimeService runtimeService)
    {
        try
        {
            var meta = await runtimeService.GetMetaAsync(profileName);
            return Results.Ok(ResponseMessage.Create(meta, "Метаданные Java профиля", HttpStatusCode.OK));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(ResponseMessage.Create(ex.Message, HttpStatusCode.BadRequest));
        }
    }
}
