using System.IO;
using System.Text.Json;

namespace MiniProLite;

/// <summary>
/// 「上次退出时风扇是不是手动满转」的落盘标记，位于 %LOCALAPPDATA%\MiniProLite\fan-state.json。
/// EC 的手动/自动状态读不回来：进程被强杀或断电后风扇可能还停在满转，
/// 下次启动界面却显示「自动」。靠这个标记在启动时提醒用户一次。
/// 所有磁盘操作都吞异常（只读盘、无权限、JSON 损坏），绝不影响主流程。
/// </summary>
internal static class FanState
{
    /// <summary>启动时的提醒文案。</summary>
    public const string ManualFullHint =
        "上次退出时风扇为手动满转，当前 EC 状态无法回读；若转速不降请点「EC 自动」";

    private const string FalseJson = "{\"manualFull\":false}";
    private const string TrueJson = "{\"manualFull\":true}";

    /// <summary>写入标记；任何失败都静默忽略。</summary>
    public static void Save(bool manualFull)
    {
        try
        {
            var directory = StateDirectory();
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "fan-state.json"), manualFull ? TrueJson : FalseJson);
        }
        catch
        {
            // 磁盘只读 / 权限不足 / 路径异常：只影响下次启动的提示，不能影响风扇操作
        }
    }

    /// <summary>读取标记；文件缺失、损坏或读不到一律当作 false。</summary>
    public static bool WasManualFull()
    {
        try
        {
            var directory = StateDirectory();
            if (directory is null)
            {
                return false;
            }

            var path = Path.Combine(directory, "fan-state.json");
            if (!File.Exists(path))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("manualFull", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string? StateDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "MiniProLite");
    }
}
