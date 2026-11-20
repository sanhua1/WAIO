namespace Waio.Shell.Hosting;

/// <summary>
/// 程序图标文件(<c>Assets\waio.ico</c>,随 exe 一起输出;由 <c>scripts/build-icon.ps1</c> 从 <c>design/icon</c> 的 SVG 生成)。
/// 各尺寸都是同一个完整图标(托盘与任务栏一致);Windows 按需要的尺寸自动挑选。
/// </summary>
internal static class AppIcon
{
    public static string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "waio.ico");

    public static bool Exists => File.Exists(Path);
}
