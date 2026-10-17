namespace wsld.Tests;

/// <summary>读取与 wslc 3.0.1 真实输出对齐的夹具文件。</summary>
public static class FixtureLoader
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
