using System.IO.Compression;
using System.Text;
using Gml.Web.Api.Core.Models.FileManager;
using GmlCore.Interfaces;

namespace Gml.Web.Api.Core.Services;

public class FileManagerService(IGmlManager gmlManager) : IFileManagerService
{
    public const long MaxEditableFileBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".json5", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf",
        ".properties", ".md", ".log", ".csv", ".tsv", ".html", ".htm", ".css", ".js", ".ts",
        ".tsx", ".jsx", ".cs", ".java", ".kt", ".gradle", ".sh", ".bat", ".cmd", ".ps1",
        ".env", ".gitignore", ".gitattributes", ".editorconfig", ".mcmeta", ".lang", ".snbt"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jar", ".zip", ".7z", ".rar", ".gz", ".tar", ".bz2",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg",
        ".mp3", ".ogg", ".wav", ".mp4", ".webm",
        ".dll", ".exe", ".so", ".dylib", ".class", ".pdb",
        ".db", ".sqlite", ".sqlite3", ".bin", ".pak", ".dat", ".nbt", ".mca", ".mcr"
    };

    /// <summary>SQLite БД панели и её journal-файлы — полностью недоступны через ФМ.</summary>
    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "data.db",
        "data.db-wal",
        "data.db-shm"
    };

    public async Task<string> ResolveRootAsync(string scope, string? profileName)
    {
        if (string.Equals(scope, FileManagerScope.Profile, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentException("Для scope=profile требуется profileName");

            var profile = await gmlManager.Profiles.GetProfile(profileName);
            if (profile is null)
                throw new DirectoryNotFoundException($"Профиль \"{profileName}\" не найден");

            if (string.IsNullOrWhiteSpace(profile.ClientPath))
                throw new DirectoryNotFoundException($"У профиля \"{profileName}\" нет ClientPath");

            var root = Path.GetFullPath(profile.ClientPath);
            if (!Directory.Exists(root))
                Directory.CreateDirectory(root);

            return root;
        }

        var installation = gmlManager.LauncherInfo.InstallationDirectory;
        if (string.IsNullOrWhiteSpace(installation))
            throw new InvalidOperationException("InstallationDirectory не задан");

        var globalRoot = Path.GetFullPath(installation);
        if (!Directory.Exists(globalRoot))
            Directory.CreateDirectory(globalRoot);

        return globalRoot;
    }

    public string ResolveSafePath(string root, string? relativePath)
    {
        var normalizedRoot = NormalizeRoot(root);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath is "." or "/" or "\\")
            return normalizedRoot;

        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderRoot(normalizedRoot, combined);
        EnsureNotProtected(combined);
        return combined;
    }

    public string ToRelativePath(string root, string fullPath)
    {
        var normalizedRoot = NormalizeRoot(root);
        var normalizedFull = Path.GetFullPath(fullPath);
        EnsureUnderRoot(normalizedRoot, normalizedFull);

        var relative = Path.GetRelativePath(normalizedRoot, normalizedFull);
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    public Task<IReadOnlyList<FileManagerEntryDto>> ListAsync(string root, string? relativePath)
    {
        var directory = ResolveSafePath(root, relativePath);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Каталог не найден");

        var entries = new List<FileManagerEntryDto>();

        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            var info = new DirectoryInfo(dir);
            if (IsProtectedName(info.Name))
                continue;

            entries.Add(new FileManagerEntryDto
            {
                Name = info.Name,
                RelativePath = ToRelativePath(root, info.FullName),
                IsDirectory = true,
                Size = 0,
                ModifiedAt = info.LastWriteTimeUtc,
                Extension = null
            });
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var info = new FileInfo(file);
            if (IsProtectedName(info.Name))
                continue;

            entries.Add(new FileManagerEntryDto
            {
                Name = info.Name,
                RelativePath = ToRelativePath(root, info.FullName),
                IsDirectory = false,
                Size = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
                Extension = info.Extension
            });
        }

        return Task.FromResult<IReadOnlyList<FileManagerEntryDto>>(
            entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    public async Task<FileManagerContentDto> ReadAsync(string root, string relativePath)
    {
        var fullPath = ResolveSafePath(root, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Файл не найден", relativePath);

        var info = new FileInfo(fullPath);
        var isBinary = IsLikelyBinary(info);

        if (isBinary || info.Length > MaxEditableFileBytes)
        {
            return new FileManagerContentDto
            {
                RelativePath = ToRelativePath(root, fullPath),
                Content = null,
                IsBinary = true,
                Encoding = "utf-8"
            };
        }

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        return new FileManagerContentDto
        {
            RelativePath = ToRelativePath(root, fullPath),
            Content = content,
            IsBinary = false,
            Encoding = "utf-8"
        };
    }

    public async Task WriteAsync(string root, string relativePath, string content)
    {
        var fullPath = ResolveSafePath(root, relativePath);
        if (Directory.Exists(fullPath))
            throw new InvalidOperationException("Нельзя записать содержимое в каталог");

        if (File.Exists(fullPath) && IsLikelyBinary(new FileInfo(fullPath)))
            throw new InvalidOperationException("Бинарный файл нельзя редактировать. Используйте скачивание.");

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxEditableFileBytes)
            throw new InvalidOperationException($"Размер файла превышает лимит {MaxEditableFileBytes} байт");

        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
    }

    public Task DeleteAsync(string root, IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var fullPath = ResolveSafePath(root, relativePath);
            if (Directory.Exists(fullPath))
            {
                EnsureDirectoryHasNoProtectedFiles(fullPath);
                Directory.Delete(fullPath, true);
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }

        return Task.CompletedTask;
    }

    public Task<(Stream Stream, string FileName)> OpenDownloadAsync(string root, string relativePath)
    {
        var fullPath = ResolveSafePath(root, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Файл не найден", relativePath);

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult((stream, Path.GetFileName(fullPath)));
    }

    public async Task<string> UploadAsync(
        string root,
        string? destinationRelativePath,
        string fileName,
        Stream content,
        bool overwrite)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new ArgumentException("Некорректное имя файла");

        if (IsProtectedName(safeName))
            throw new UnauthorizedAccessException(ProtectedFileMessage);

        var relative = string.IsNullOrWhiteSpace(destinationRelativePath)
            ? safeName
            : Path.Combine(destinationRelativePath.Replace('/', Path.DirectorySeparatorChar), safeName)
                .Replace('\\', '/');

        var fullPath = ResolveSafePath(root, relative);
        if (Directory.Exists(fullPath))
            throw new InvalidOperationException("Нельзя записать файл поверх каталога");

        if (File.Exists(fullPath) && !overwrite)
            throw new InvalidOperationException($"Файл уже существует: {relative}");

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(
            fullPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 64,
            useAsync: true);
        await content.CopyToAsync(fileStream);

        return ToRelativePath(root, fullPath);
    }

    public Task<string> ArchiveAsync(string root, IEnumerable<string> relativePaths, string archiveName)
    {
        var paths = relativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
            throw new ArgumentException("Не выбраны файлы или папки для архивации");

        var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(archiveName) ? "archive.zip" : archiveName);
        if (!safeName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            safeName += ".zip";

        var parentRelative = Path.GetDirectoryName(paths[0].Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var archiveRelative = string.IsNullOrEmpty(parentRelative)
            ? safeName
            : Path.Combine(parentRelative, safeName).Replace('\\', '/');

        var archiveFullPath = ResolveSafePath(root, archiveRelative);
        if (File.Exists(archiveFullPath))
            File.Delete(archiveFullPath);

        var archiveDirectory = Path.GetDirectoryName(archiveFullPath);
        if (!string.IsNullOrEmpty(archiveDirectory) && !Directory.Exists(archiveDirectory))
            Directory.CreateDirectory(archiveDirectory);

        using (var archive = ZipFile.Open(archiveFullPath, ZipArchiveMode.Create))
        {
            foreach (var relativePath in paths)
            {
                var fullPath = ResolveSafePath(root, relativePath);
                if (Directory.Exists(fullPath))
                {
                    AddDirectoryToArchive(archive, root, fullPath, relativePath);
                }
                else if (File.Exists(fullPath))
                {
                    if (IsProtectedName(Path.GetFileName(fullPath)))
                        throw new UnauthorizedAccessException(ProtectedFileMessage);
                    archive.CreateEntryFromFile(fullPath, NormalizeZipEntryName(relativePath), CompressionLevel.Optimal);
                }
            }
        }

        return Task.FromResult(ToRelativePath(root, archiveFullPath));
    }

    public Task ExtractAsync(string root, string archiveRelativePath, string? destinationRelativePath)
    {
        var archiveFullPath = ResolveSafePath(root, archiveRelativePath);
        if (!File.Exists(archiveFullPath))
            throw new FileNotFoundException("Архив не найден", archiveRelativePath);

        if (!archiveFullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Поддерживаются только .zip архивы");

        string destinationFullPath;
        if (string.IsNullOrWhiteSpace(destinationRelativePath))
        {
            var parent = Path.GetDirectoryName(archiveFullPath) ?? root;
            var folderName = Path.GetFileNameWithoutExtension(archiveFullPath);
            destinationFullPath = Path.Combine(parent, folderName);
        }
        else
        {
            destinationFullPath = ResolveSafePath(root, destinationRelativePath);
        }

        EnsureUnderRoot(NormalizeRoot(root), Path.GetFullPath(destinationFullPath));

        if (!Directory.Exists(destinationFullPath))
            Directory.CreateDirectory(destinationFullPath);

        using var archive = ZipFile.OpenRead(archiveFullPath);
        foreach (var entry in archive.Entries)
        {
            var entryName = entry.FullName.Replace('\\', '/');
            var fileName = Path.GetFileName(entryName.TrimEnd('/'));
            if (IsProtectedName(fileName))
                throw new UnauthorizedAccessException(
                    "Архив содержит защищённый файл базы данных и не может быть распакован");

            var targetPath = Path.GetFullPath(Path.Combine(destinationFullPath, entryName));
            EnsureUnderRoot(NormalizeRoot(destinationFullPath), targetPath);

            if (string.IsNullOrEmpty(entry.Name) || entryName.EndsWith('/'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            entry.ExtractToFile(targetPath, overwrite: true);
        }

        return Task.CompletedTask;
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string root, string directoryFullPath, string relativePrefix)
    {
        var files = Directory.GetFiles(directoryFullPath, "*", SearchOption.AllDirectories)
            .Where(f => !IsProtectedName(Path.GetFileName(f)))
            .ToArray();

        if (files.Length == 0)
        {
            var entryName = NormalizeZipEntryName(relativePrefix.TrimEnd('/', '\\') + "/");
            archive.CreateEntry(entryName);
            return;
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, NormalizeZipEntryName(relative), CompressionLevel.Optimal);
        }
    }

    private static string NormalizeZipEntryName(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    private static string NormalizeRoot(string root)
    {
        var normalized = Path.GetFullPath(root);
        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               + Path.DirectorySeparatorChar;
    }

    private static void EnsureUnderRoot(string normalizedRoot, string fullPath)
    {
        var candidate = Path.GetFullPath(fullPath);
        var root = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Путь выходит за пределы разрешённого корня");
        }
    }

    private const string ProtectedFileMessage =
        "Файл базы данных (data.db) защищён и недоступен через файловый менеджер";

    private static bool IsProtectedName(string? name) =>
        !string.IsNullOrEmpty(name) && ProtectedFileNames.Contains(name);

    private static void EnsureNotProtected(string fullPath)
    {
        if (IsProtectedName(Path.GetFileName(fullPath)))
            throw new UnauthorizedAccessException(ProtectedFileMessage);
    }

    private static void EnsureDirectoryHasNoProtectedFiles(string directoryFullPath)
    {
        foreach (var file in Directory.EnumerateFiles(directoryFullPath, "*", SearchOption.AllDirectories))
        {
            if (IsProtectedName(Path.GetFileName(file)))
            {
                throw new UnauthorizedAccessException(
                    "Нельзя удалить папку: внутри есть защищённый файл базы данных (data.db)");
            }
        }
    }

    private static bool IsLikelyBinary(FileInfo info)
    {
        if (!string.IsNullOrEmpty(info.Extension) && BinaryExtensions.Contains(info.Extension))
            return true;

        if (!string.IsNullOrEmpty(info.Extension) && TextExtensions.Contains(info.Extension))
            return false;

        if (info.Length == 0)
            return false;

        var sampleSize = (int)Math.Min(512, info.Length);
        Span<byte> buffer = stackalloc byte[sampleSize];
        using var stream = info.OpenRead();
        var read = stream.Read(buffer);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
                return true;
        }

        // Нет null-байтов — считаем текстовым (можно открыть в редакторе)
        return false;
    }
}
