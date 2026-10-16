using System.Text.Json;
using System.Text.Json.Serialization;

namespace wsld.Core.Settings;

/// <summary>
/// 读写 %LOCALAPPDATA%\wsld\settings.json(功能 009 · AC2)。
/// 读失败(文件不存在、格式坏掉、没权限)一律返回默认值,写失败只是不保存 ——
/// 设置是锦上添花,绝不让它把应用启动或退出搞崩。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>设置文件位置;目录不可用时退回临时目录。</summary>
    public static string FilePath
    {
        get
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.GetTempPath();
            return Path.Combine(dir, "wsld", "settings.json");
        }
    }

    public static AppSettings Load() => LoadFrom(FilePath);

    /// <summary>从指定文件读取(单测用固定路径,不碰真实用户设置)。</summary>
    public static AppSettings LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();

            // 记事本 / Visual Studio 可能把它存成带 BOM 的 UTF-8,而 JSON 解析器把 BOM 当非法字符。
            // 异常会被下面的 catch 吞掉并退回默认值,看上去就像"设置被莫名其妙清空了",所以先剥掉。
            var text = File.ReadAllText(path).TrimStart('\uFEFF');
            var loaded = JsonSerializer.Deserialize<AppSettings>(text, ReadOptions);
            return loaded ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>保存设置;成功返回 true。</summary>
    public static bool Save(AppSettings settings) => SaveTo(settings, FilePath);

    /// <summary>写到指定文件;目录不存在会建,写不动只当没保存成功。</summary>
    public static bool SaveTo(AppSettings settings, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, WriteOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
