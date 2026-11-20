using Waio.Kit.Features;

namespace Waio.Core.Storage;

/// <summary>
/// WAIO 的数据位置(ARCHITECTURE §7)。配置在漫游目录(可备份/同步),数据与日志在本机目录。
/// 测试用 <see cref="ForRoot"/> 指到临时目录,不碰真实用户数据。
/// </summary>
public sealed record AppPaths(string ConfigDirectory, string DataDirectory, string LogsDirectory)
{
    public static AppPaths ForCurrentUser()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(
            Path.Combine(roaming, "WAIO", "config"),
            Path.Combine(local, "WAIO", "data"),
            Path.Combine(local, "WAIO", "logs"));
    }

    public static AppPaths ForRoot(string root) =>
        new(Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "logs"));

    public string AppSettingsFile => Path.Combine(ConfigDirectory, "app.json");

    public string BindingsFile => Path.Combine(ConfigDirectory, "bindings.json");

    public string FeatureSettingsFile(FeatureId id) => Path.Combine(ConfigDirectory, "features", id + ".json");

    public string FeatureDataDirectory(FeatureId id) => Path.Combine(DataDirectory, id.Value);
}
