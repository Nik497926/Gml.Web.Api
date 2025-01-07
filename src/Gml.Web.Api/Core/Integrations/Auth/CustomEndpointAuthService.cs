using System.Net;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Gml.Web.Api.Domains.Integrations;
using GmlCore.Interfaces;
using Newtonsoft.Json;

namespace Gml.Web.Api.Core.Integrations.Auth;

public class CustomEndpointAuthService(IHttpClientFactory httpClientFactory, IGmlManager gmlManager)
    : IPlatformAuthService
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient();

    public async Task<AuthResult> Auth(string login, string password)
    {
        var dto = JsonConvert.SerializeObject(new
        {
            Login = login,
            Password = password
        });

        var content = new StringContent(dto, Encoding.UTF8, "application/json");

        var result =
            await _httpClient.PostAsync((await gmlManager.Integrations.GetActiveAuthService())!.Endpoint, content);

        var resultContent = await result.Content.ReadAsStringAsync();

        var authResult = new AuthResult
        {
            Login = login,
            IsSuccess = result.IsSuccessStatusCode
        };

        switch (result.StatusCode)
        {
            case HttpStatusCode.NotFound:
            case HttpStatusCode.BadRequest:
                var message = JsonConvert.DeserializeObject<AuthErrorResponse>(resultContent)?.Message;

                if (string.IsNullOrEmpty(message))
                    return authResult;

                authResult.Message = message;

                return authResult;
            case HttpStatusCode.Unauthorized:
                return authResult;
            case HttpStatusCode.OK:
                var model = JsonConvert.DeserializeObject<AuthCustomResponse>(resultContent);

                authResult.Login = model?.Login ?? login;
                authResult.Uuid = model?.UserUuid;

                return authResult;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}

public class AuthErrorResponse
{
    public string Message { get; set; }
}
