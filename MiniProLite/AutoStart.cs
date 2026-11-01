using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MiniProLite;

/// <summary>
/// 开机自启用「最高权限的计划任务」实现。
/// 不能用 Run 键：那样登录后启动的是未提权进程，操作 WMI 会直接 Access Denied。
/// </summary>
internal static class AutoStart
{
    /// <summary>计划任务名（--tray 启动）。</summary>
    public const string TaskName = "MiniProLite";

    private static readonly Lazy<Encoding> OemEncoding = new(() =>
    {
        try
        {
            // 中文系统上 schtasks 输出是 GBK，按 UTF-8 解码会变乱码
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    });

    /// <summary>
    /// 是否已启用：优先看 /Query /XML 里 Settings 节点的 Enabled。
    /// 「任务存在但被禁用」的退出码也是 0，只看退出码会误报成已启用；
    /// XML 标签是英文，不受系统语言影响。
    /// 拿不到 XML / 解析失败时回落到退出码判断（不要在无权限等情况下误报“未启用”）。
    /// </summary>
    public static bool IsEnabled()
    {
        if (QueryXml() is { } xml && TryReadEnabled(xml) is { } enabled)
        {
            return enabled;
        }

        return Run("/Query", "/TN", TaskName).ExitCode == 0;
    }

    /// <summary>
    /// 校正已存在任务绑定的 exe 路径（用户把程序换了目录后，旧路径会导致开机启动静默失败）。
    /// 任务不存在、读不到 XML、路径一致 → 返回 null（不擅自创建任务）；
    /// 路径不一致 → 用 <see cref="Enable"/>（内部 /F 覆盖）重建，返回 null 或错误摘要。
    /// </summary>
    public static string? Repair()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }

        // 用户手动禁用过的任务不要自愈重建：/F 会把它重新启用，等于覆盖用户意图。
        // IsEnabled 对「不存在」和「被禁用」都返回 false，两种情况都不该动。
        if (!IsEnabled())
        {
            return null;
        }

        if (QueryXml() is not { } xml)
        {
            return null;
        }

        var command = ReadCommand(xml);
        if (command is null || SamePath(command, exe))
        {
            return null;
        }

        return Enable();
    }

    /// <summary>创建登录时以最高权限启动的任务。成功返回 null，失败返回错误摘要。</summary>
    public static string? Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return "取不到当前 exe 路径，无法创建计划任务";
        }

        // /TR 的值是「"<exe 绝对路径>" --tray」，交给 ArgumentList 处理引号转义
        var result = Run(
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"{exe}\" --tray",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F");
        return result.ExitCode == 0 ? null : Describe("创建", result);
    }

    /// <summary>删除计划任务。成功返回 null，失败返回错误摘要。</summary>
    public static string? Disable()
    {
        var result = Run("/Delete", "/TN", TaskName, "/F");
        return result.ExitCode == 0 ? null : Describe("删除", result);
    }

    private static string Describe(string action, Result result)
    {
        var detail = result.Error.Trim();
        if (detail.Length > 200)
        {
            detail = detail[..200] + "…";
        }

        return detail.Length == 0
            ? $"计划任务{action}失败（schtasks 退出码 {result.ExitCode}）"
            : $"计划任务{action}失败（exit {result.ExitCode}）：{detail}";
    }

    private readonly record struct Result(int ExitCode, string Output, string Error);

    /// <summary>取任务 XML；任务不存在 / 无权限 / 输出为空时返回 null。</summary>
    private static string? QueryXml()
    {
        var result = Run("/Query", "/TN", TaskName, "/XML");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) ? result.Output : null;
    }

    // schtasks /XML 的关键结构（标签是英文，不受系统语言影响；系统自带任务常常省略 Enabled，缺省视为已启用）：
    //   <Settings><Enabled>false</Enabled>...</Settings>
    //   <Actions Context="..."><Exec><Command>C:\path\MiniProLite.exe</Command><Arguments>--tray</Arguments></Exec></Actions>

    /// <summary>解析 Settings 节点下的 Enabled；缺失或解析失败返回 null。</summary>
    private static bool? TryReadEnabled(string xml)
    {
        try
        {
            var settings = Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings");
            var enabled = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled");
            if (enabled is null)
            {
                return null;
            }

            // 只认明确的 false；缺省 / true / 其它写法都当已启用
            return !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>取 Actions 里第一个 Exec 节点的 Command（命令有两种写法：纯路径，或「带引号的路径 + 参数」）；取不到返回 null。</summary>
    private static string? ReadCommand(string xml)
    {
        try
        {
            var exec = Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Exec");
            var command = exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Command");
            return string.IsNullOrWhiteSpace(command?.Value) ? null : command.Value.Trim();
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// schtasks 的 XML 可能按 UTF-16 输出，而我们按 OEM 代码页解码，
    /// 结果每个 ASCII 字符后面多一个 NUL；去掉才能解析。
    /// </summary>
    private static XDocument Parse(string xml) => XDocument.Parse(CleanXml(xml));

    private static string CleanXml(string xml)
    {
        if (xml.Contains('\0'))
        {
            xml = xml.Replace("\0", string.Empty, StringComparison.Ordinal);
        }

        return xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
    }

    /// <summary>比对计划任务里的 exe 路径与当前进程路径（忽略大小写、引号与首尾空白）。</summary>
    private static bool SamePath(string command, string exe)
    {
        var target = exe.Trim();
        var trimmed = command.Trim();

        // 先按「命令 + 参数」取第一个 token（能处理引号），再比整串是不是就是一个纯路径
        return string.Equals(FirstToken(trimmed), target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Unquote(trimmed), target, StringComparison.OrdinalIgnoreCase);
    }

    private static string Unquote(string value) => value.Trim().Trim('"').Trim();

    /// <summary>取命令里第一个 token，正确处理引号（XML 里 Command 一般只放路径，但不保证）。</summary>
    private static string FirstToken(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 0 ? text[1..end] : text.Trim('"');
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    private static Result Run(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = OemEncoding.Value,
                StandardErrorEncoding = OemEncoding.Value,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return new Result(-1, string.Empty, "无法启动 schtasks.exe");
            }

            // stdout/stderr 必须并发读：先读满一个流再读另一个，管道缓冲区满时会互相死锁。
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            var error = errorTask.GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult();
            return new Result(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new Result(-1, string.Empty, ex.Message);
        }
    }
}
