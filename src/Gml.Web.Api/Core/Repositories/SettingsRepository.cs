using System.Reactive.Subjects;
using Gml.Domains.Settings;
using Gml.Web.Api.Core.Options;
using Gml.Web.Api.Core.Services;
using Gml.Web.Api.Data;
using GmlCore.Interfaces;
using GmlCore.Interfaces.Enums;
using Microsoft.EntityFrameworkCore;

namespace Gml.Web.Api.Core.Repositories;

public class SettingsRepository(
    DatabaseContext databaseContext,
    ServerSettings options,
    IGmlManager gmlManager,
    ISubject<Settings> settingsObservable,
    S3MinioClientRepairService s3MinioClientRepairService,
    ILogger<SettingsRepository> logger)
    : ISettingsRepository
{
    private readonly ServerSettings _options = options;
    public IObservable<Settings> SettingsUpdated => settingsObservable;

    public async Task<Settings?> UpdateSettings(Settings settings)
    {
        var oldSettings = await databaseContext.Settings.OrderByDescending(c => c.Id).FirstOrDefaultAsync();

        gmlManager.LauncherInfo.UpdateSettings(
            settings.StorageType,
            settings.StorageHost,
            settings.StorageLogin,
            settings.StoragePassword,
            settings.TextureProtocol,
            settings.CurseForgeKey,
            settings.VkKey,
            settings.SentryAutoClearPeriod,
            settings.SentryNeedAutoClear
        );

        if (settings.StorageType == StorageType.S3)
        {
            try
            {
                await s3MinioClientRepairService.ApplyAsync(gmlManager);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "S3 client repair failed after settings update");
            }
        }

        settings.Id = 0;
        settings.IsInstalled = true;
        settings.ProjectName = oldSettings?.ProjectName;
        await databaseContext.AddAsync(settings);
        await databaseContext.SaveChangesAsync();

        settingsObservable.OnNext(settings);

        return settings;
    }

    public Task<Settings?> GetSettings()
    {
        return databaseContext.Settings.OrderByDescending(c => c.Id).FirstOrDefaultAsync();
    }
}
