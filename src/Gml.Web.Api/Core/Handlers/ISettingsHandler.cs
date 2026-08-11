using AutoMapper;
using Gml.Web.Api.Core.Models.Settings;
using Gml.Web.Api.Core.Repositories;
using Gml.Web.Api.Core.Services;

namespace Gml.Web.Api.Core.Handlers;

public interface ISettingsHandler
{
    static abstract Task<IResult> UpdateSettings(
        ISettingsRepository settingsService,
        IMapper mapper,
        UnicoreAuthOptionsService unicoreAuthOptions,
        SettingsUpdateRequest settingsDto);

    static abstract Task<IResult> GetSettings(
        ISettingsRepository settingsService,
        IMapper mapper,
        UnicoreAuthOptionsService unicoreAuthOptions);

    static abstract Task<IResult> TestS3Connection(
        ISettingsRepository settingsService,
        S3ConnectionTestService s3ConnectionTestService,
        SettingsS3TestRequest request);
}
