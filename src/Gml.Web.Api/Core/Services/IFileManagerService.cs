using Gml.Web.Api.Core.Models.FileManager;
using GmlCore.Interfaces;

namespace Gml.Web.Api.Core.Services;

public interface IFileManagerService
{
    Task<string> ResolveRootAsync(string scope, string? profileName);
    string ResolveSafePath(string root, string? relativePath);
    string ToRelativePath(string root, string fullPath);
    Task<IReadOnlyList<FileManagerEntryDto>> ListAsync(string root, string? relativePath);
    Task<FileManagerContentDto> ReadAsync(string root, string relativePath);
    Task WriteAsync(string root, string relativePath, string content);
    Task DeleteAsync(string root, IEnumerable<string> relativePaths);
    Task<(Stream Stream, string FileName)> OpenDownloadAsync(string root, string relativePath);
    Task<string> UploadAsync(string root, string? destinationRelativePath, string fileName, Stream content, bool overwrite);
    Task<string> ArchiveAsync(string root, IEnumerable<string> relativePaths, string archiveName);
    Task ExtractAsync(string root, string archiveRelativePath, string? destinationRelativePath);
}
