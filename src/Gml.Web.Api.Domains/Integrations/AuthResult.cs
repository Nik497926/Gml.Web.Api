#nullable enable
namespace Gml.Web.Api.Domains.Integrations;
 
public class AuthResult
{
    public bool IsSuccess { get; set; }
    public string? Login { get; set; }
    public string? Email { get; set; }
    public decimal RealMoney { get; set; }
    public string? Uuid { get; set; }
    public string? Message { get; set; }
}
