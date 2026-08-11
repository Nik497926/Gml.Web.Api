using System.Net;
using Gml.Dto.Messages;
using Gml.Web.Api.Core.Options;
using Microsoft.Extensions.Options;

namespace Gml.Web.Api.Core.Handlers;

public static class MarketplaceHandler
{
    public static Task<IResult> GetBridge(IOptions<ServerSettings> options)
    {
        var settings = options.Value;

        var payload = new
        {
            available = true,
            projectName = settings.ProjectName,
            version = settings.ProjectVersion
        };

        return Task.FromResult(Results.Ok(ResponseMessage.Create(payload, "Marketplace bridge OK", HttpStatusCode.OK)));
    }
}
