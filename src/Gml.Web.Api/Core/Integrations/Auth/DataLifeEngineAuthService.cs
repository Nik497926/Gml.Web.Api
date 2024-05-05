using System.Text;
using GmlCore.Interfaces;
using Newtonsoft.Json;

namespace Gml.Web.Api.Core.Integrations.Auth;

public class DataLifeEngineAuthService(IHttpClientFactory httpClientFactory, IGmlManager gmlManager)
    : IPlatformAuthService
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient();

    public async Task<bool> Auth(string login, string password)
    {
        var dto = JsonConvert.SerializeObject(new
        {
            Login = login,
            Password = password
        });

        var content = new StringContent(dto, Encoding.UTF8, "application/json");

        var result =
            await _httpClient.PostAsync((await gmlManager.Integrations.GetActiveAuthService())!.Endpoint, content);

        return result.IsSuccessStatusCode;
    }
}
