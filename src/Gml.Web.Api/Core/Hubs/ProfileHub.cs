using System.Text.Json.Serialization;
using Gml.Web.Api.Core.Models.Java;
using Gml.Web.Api.Core.Services;
using GmlCore.Interfaces;
using GmlCore.Interfaces.Enums;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Gml.Web.Api.Core.Hubs;

public class ProfileHub : BaseHub
{
    private readonly IGmlManager _gmlManager;
    private double lastPackProgress = -1;

    private double lastProgress = -1;

    public ProfileHub(IGmlManager gmlManager)
    {
        _gmlManager = gmlManager;
    }

    public async Task Build(string profileName)
    {
        try
        {
            if (string.IsNullOrEmpty(profileName))
                return;

            var profile = await _gmlManager.Profiles.GetProfile(profileName);

            if (profile is null)
                return;

            if (profile.State is ProfileState.Loading or ProfileState.Packing)
            {
                SendCallerMessage(
                    "В данный момент уже происходит загрузка выбранного профиля!");
                return;
            }

            Log("Preparation for packaging...", profileName);

            var eventInfo = _gmlManager.Profiles.PackChanged.Subscribe(percentage =>
            {
                ChangePackProgress(profileName, percentage);
            });
            await _gmlManager.Profiles.PackProfile(profile);
            await Clients.All.SendAsync("SuccessPacked", profileName);
            eventInfo.Dispose();
            lastPackProgress = -1;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            throw;
        }
    }

    private async void ChangePackProgress(string profileName, double percentage)
    {
        try
        {
            if (Math.Abs(lastPackProgress - percentage) < 0.001) return;

            lastPackProgress = percentage;

            await Clients.All.SendAsync("ChangeProgress", profileName, percentage);
        }
        catch (Exception exception)
        {
            SendCallerMessage(exception.Message);
            Console.WriteLine(exception);
        }
    }

    public async Task Restore(string profileName)
    {
        await RestoreInternal(profileName);
    }

    public async Task RestoreAndChangeBootstrapVersion(string profileName, JavaVersionDto? javaVersion)
    {
        try
        {
            if (javaVersion is not null
                && !string.Equals(javaVersion.Source, JavaRuntimeSource.Default, StringComparison.OrdinalIgnoreCase)
                && javaVersion.Source != "default"
                && !string.IsNullOrWhiteSpace(javaVersion.Version)
                && !string.Equals(javaVersion.Version, "default", StringComparison.OrdinalIgnoreCase))
            {
                var runtime = Context.GetHttpContext()?.RequestServices.GetRequiredService<IJavaRuntimeService>();
                if (runtime is not null
                    && string.Equals(javaVersion.Source, JavaRuntimeSource.Azul, StringComparison.OrdinalIgnoreCase)
                    && javaVersion.MajorVersion > 0)
                {
                    await runtime.AssignAzulAsync(profileName, new JavaAzulAssignRequest
                    {
                        DownloadUrl = javaVersion.DownloadUrl,
                        PackageUuid = javaVersion.PackageUuid,
                        Name = javaVersion.Name,
                        Version = javaVersion.Version,
                        MajorVersion = javaVersion.MajorVersion,
                        Os = javaVersion.Os,
                        Arch = javaVersion.Arch
                    });
                }
                else if (runtime is not null)
                {
                    // Already assigned custom/azul runtime — ensure JavaPath applied from meta
                    var profile = await _gmlManager.Profiles.GetProfile(profileName);
                    if (profile is not null)
                    {
                        var meta = await runtime.GetMetaAsync(profileName);
                        await runtime.ApplyToProfileAsync(profile, meta);
                    }
                }
            }
            else
            {
                var runtime = Context.GetHttpContext()?.RequestServices.GetRequiredService<IJavaRuntimeService>();
                if (runtime is not null)
                    await runtime.SetDefaultAsync(profileName);
            }

            await RestoreInternal(profileName);
        }
        catch (Exception exception)
        {
            _gmlManager.BugTracker.CaptureException(exception);
            SendCallerMessage($"Не удалось применить Java и восстановить профиль. {exception.Message}");
            await Clients.All.SendAsync("RestoreFailed", profileName);
        }
    }

    private async Task RestoreInternal(string profileName)
    {
        try
        {
            var profile = await _gmlManager.Profiles.GetProfile(profileName);

            if (profile == null)
            {
                SendCallerMessage($"Профиль \"{profileName}\" не найден");
                return;
            }

            if (profile.State == ProfileState.Loading)
            {
                SendCallerMessage(
                    "В данный момент уже происходит загрузка выбранного профиля!");
                return;
            }

            // Ensure selected runtime meta exists before restore (JavaPath stored in sidecar).
            try
            {
                var runtime = Context.GetHttpContext()?.RequestServices.GetService<IJavaRuntimeService>();
                if (runtime is not null)
                    _ = await runtime.GetMetaAsync(profileName);
            }
            catch
            {
                // non-fatal
            }

            var fullPercentage = profile.GameLoader.FullPercentages.Subscribe(percentage =>
            {
                SendProgress("FullProgress", profile.Name, percentage);
            });

            var loadPercentage = profile.GameLoader.LoadPercentages.Subscribe(percentage =>
            {
                SendProgress("ChangeProgress", profile.Name, percentage);
            });

            var logInfo = profile.GameLoader.LoadLog.Subscribe(logs => { Log(logs, profile.Name); });

            var loadErrors = new List<string>();

            var exception = profile.GameLoader.LoadException.Subscribe(async logs =>
            {
                var message = logs.ToString();
                if (!string.IsNullOrWhiteSpace(message))
                    loadErrors.Add(message);

                try
                {
                    await Clients.All.SendAsync("OnException", profile.Name, message);
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception);
                    _gmlManager.BugTracker.CaptureException(exception);
                }
            });

            await _gmlManager.Profiles.RestoreProfileInfo(profile.Name);

            fullPercentage.Dispose();
            loadPercentage.Dispose();
            logInfo.Dispose();
            exception.Dispose();

            if (loadErrors.Count > 0)
            {
                SendCallerMessage(
                    $"Загрузка профиля завершилась с ошибками ({loadErrors.Count}). Проверьте лог.");
                await Clients.All.SendAsync("RestoreFailed", profile.Name);
                lastProgress = -1;
                return;
            }

            await Clients.All.SendAsync("SuccessInstalled", profile.Name);

            lastProgress = -1;
        }
        catch (Exception exception)
        {
            _gmlManager.BugTracker.CaptureException(exception);
            SendCallerMessage($"Не удалось восстановить профиль. {exception.Message}");
            Console.WriteLine(exception);
            await Clients.All.SendAsync("RestoreFailed", profileName);
        }
    }

    private async void SendProgress(string name, string profileName, double percentage)
    {
        try
        {
            if (Math.Abs(lastProgress - percentage) < 0.000) return;

            var percentageValue = Math.Round(percentage, 2);

            if (double.IsPositiveInfinity(percentageValue) || double.IsNegativeInfinity(percentageValue))
            {
                return;
            }

            if (double.IsNaN(percentageValue) || double.IsNaN(percentageValue))
            {
                return;
            }

            lastProgress = percentage;
            await Clients.All.SendAsync(name, profileName, percentageValue);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
        }
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        foreach (var profile in await _gmlManager.Profiles.GetProfiles())
        {
            if (profile.State is not (ProfileState.Loading or ProfileState.Packing))
                continue;

            using var logInfo = profile.GameLoader.LoadLog
                .Subscribe(logs => { Log(logs, profile.Name); });

            using var fullPercentage = profile.GameLoader.FullPercentages.Subscribe(percentage =>
            {
                SendProgress("FullProgress", profile.Name, percentage);
            });

            using var loadPercentage = profile.GameLoader.LoadPercentages.Subscribe(percentage =>
            {
                SendProgress("ChangeProgress", profile.Name, percentage);
            });
        }
    }

    public class ConfigureJsonOptions : IConfigureOptions<JsonOptions>
    {
        public void Configure(JsonOptions options)
        {
            options.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        }
    }
}
