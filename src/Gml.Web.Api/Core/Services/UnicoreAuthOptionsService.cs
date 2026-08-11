using System.Text.Json;

namespace Gml.Web.Api.Core.Services;

/// <summary>
/// Локальные опции Unicore (не в Gml.Domains.Settings).
/// useExternalTokens=true — AccessToken игрока = JWT Unicore;
/// false — AccessToken = JWT Gml, а Unicore access/refresh держатся в ExternalPlayerTokenStore для кабинета.
/// </summary>
public class UnicoreAuthOptionsService
{
    private readonly string _filePath;
    private readonly object _sync = new();
    private UnicoreAuthOptionsState _state = new();

    public UnicoreAuthOptionsService(IHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "database");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "unicore-auth.json");
        Load();
    }

    public bool UseExternalTokens
    {
        get
        {
            lock (_sync) return _state.UseExternalTokens;
        }
    }

    public void SetUseExternalTokens(bool value)
    {
        lock (_sync)
        {
            _state.UseExternalTokens = value;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<UnicoreAuthOptionsState>(json);
            if (loaded is not null)
                _state = loaded;
        }
        catch
        {
            // default: true (текущее поведение)
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private sealed class UnicoreAuthOptionsState
    {
        public bool UseExternalTokens { get; set; } = true;
    }
}
