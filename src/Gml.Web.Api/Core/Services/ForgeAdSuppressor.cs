using System.Reflection;

namespace Gml.Web.Api.Core.Services;

/// <summary>
/// CmlLib.Core.Installer.Forge после установки Forge открывает браузер с adfoc.us.
/// Обнуляем ForgeAdUrl, чтобы Process.Start не открывал страницу на сервере панели.
/// </summary>
public class ForgeAdSuppressor(ILogger<ForgeAdSuppressor> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                               .FirstOrDefault(a => a.GetName().Name == "CmlLib.Core.Installer.Forge")
                           ?? Assembly.Load("CmlLib.Core.Installer.Forge");

            var type = assembly.GetType("CmlLib.Core.Installer.Forge.ForgeInstaller")
                       ?? assembly.GetTypes().FirstOrDefault(t => t.Name == "ForgeInstaller");

            if (type is null)
            {
                logger.LogWarning("ForgeInstaller type not found — cannot suppress ad browser");
                return Task.CompletedTask;
            }

            var field = type.GetField("ForgeAdUrl", BindingFlags.Public | BindingFlags.Static)
                        ?? type.GetField("ForgeAdUrl", BindingFlags.NonPublic | BindingFlags.Static);

            if (field is null)
            {
                logger.LogWarning("ForgeAdUrl field not found — cannot suppress ad browser");
                return Task.CompletedTask;
            }

            field.SetValue(null, string.Empty);
            logger.LogInformation("Forge installer browser ad suppressed");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to suppress Forge installer browser ad");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
