using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoDaily.Models;

namespace AutoDaily.Services;

public static class ConfigService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>設定檔放在程式旁邊，整個資料夾可直接搬移。</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    public static string ConfigPath => Path.Combine(BaseDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (cfg != null)
                {
                    cfg.Settings ??= new AppSettings();
                    cfg.Tasks ??= new List<GameTask>();
                    // 說明文字不開放編輯，跟著程式版本更新
                    foreach (var t in cfg.Tasks)
                    {
                        if (Presets.All.FirstOrDefault(p => p.Key == t.Preset) is { } preset)
                            t.Tip = preset.Tip;
                        Presets.FillMissing(t);
                    }
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            // 設定檔壞掉時先備份，避免直接覆蓋掉使用者的設定
            try { File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true); } catch { }
            Logger.Write($"設定檔讀取失敗，已改用預設值（原檔備份為 config.json.bad）：{ex.Message}", LogLevel.Error);
        }

        return new AppConfig { Tasks = Presets.All.Select(Presets.CreateTask).ToList() };
    }

    public static void Save(AppConfig config)
    {
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
