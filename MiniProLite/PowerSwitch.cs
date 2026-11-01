using System.Globalization;
using System.Management;

namespace MiniProLite;

/// <summary>
/// root\WMI 下 PowerSwitchInterface 实例的极简封装。
/// 目标实例：ACPI\PNP0C14\IP3POWERSWITCH_0。
/// </summary>
internal static class PowerSwitch
{
    private const string ScopePath = @"root\WMI";
    private const string InstanceName = @"ACPI\PNP0C14\IP3POWERSWITCH_0";

    // 性能模式：静音=2、均衡=0、性能=1
    public const byte ModeSilent = 2;
    public const byte ModeBalanced = 0;
    public const byte ModePerformance = 1;

    // 风扇占空比：100=满转，101=交回 EC 自动
    public const byte FanFull = 100;
    public const byte FanEcAuto = 101;

    private const byte FanNumber = 1;

    /// <summary>绑定到目标实例；调用方负责 Dispose。</summary>
    public static ManagementObject Open()
    {
        var scope = new ManagementScope(ScopePath);
        scope.Connect();

        // 注意：这里构造的是 CIM 对象路径（ManagementPath），不是 WQL 查询语句！
        // 对象路径的键值里反斜杠必须保持【单个】，千万不要做 Replace("\\", "\\\\") 之类的 WQL 式转义。
        // 一旦把 InstanceName 里的反斜杠翻倍（ACPI\\PNP0C14\\...），WMI 就匹配不到实例，
        // Get() 会返回 NotFound，界面表现为“找不到”。厂商 Python 里的 'ACPI\\PNP0C14\\...'
        // 只是 Python 源码层面的转义，运行时同样是单反斜杠。
        // 请勿把这一行改回 .Replace(..) 形式，除非确认目标环境要求的确实是翻倍后的路径。
        var path = new ManagementPath($"PowerSwitchInterface.InstanceName='{InstanceName}'");

        ManagementObject? exact = null;
        Exception? exactError;
        try
        {
            exact = new ManagementObject(scope, path, null);
            exact.Get();
            return exact;
        }
        catch (Exception ex)
        {
            exactError = ex;
            exact?.Dispose();
            exact = null;
        }

        // 兜底：精确路径失败（实例名或路径语法变化）时，枚举全部实例自行匹配。
        var seen = new List<string>();
        ManagementObject? matched = null;
        ManagementObject? first = null;
        Exception? enumError = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT * FROM PowerSwitchInterface"));
            using var results = searcher.Get();
            foreach (ManagementObject instance in results)
            {
                var name = instance["InstanceName"] as string;
                seen.Add(name ?? "<null>");

                if (matched is null && string.Equals(name, InstanceName, StringComparison.OrdinalIgnoreCase))
                {
                    matched = instance;
                    continue;
                }

                if (first is null)
                {
                    first = instance;
                    continue;
                }

                instance.Dispose();
            }
        }
        catch (Exception ex)
        {
            enumError = ex;
        }

        // 命中的那个交给调用方，另一个多余的实例在这里释放，避免泄漏 WMI 对象。
        if (matched is not null && first is not null)
        {
            first.Dispose();
            first = null;
        }

        var chosen = matched ?? first;
        if (chosen is not null)
        {
            return chosen;
        }

        // 权限问题优先抛出，交给 ErrorText 统一翻译成“请以管理员身份运行”，
        // 不要被下面的“找不到实例”诊断信息掩盖。
        if (IsAccessDenied(enumError) || IsAccessDenied(exactError))
        {
            throw new UnauthorizedAccessException(
                "访问被拒绝：请以管理员身份运行", enumError ?? exactError);
        }

        var names = seen.Count == 0 ? "（无）" : string.Join("、", seen);
        var detail = enumError is null ? "枚举成功" : $"枚举失败：{enumError.Message}";
        throw new InvalidOperationException(
            $"未找到 PowerSwitchInterface 实例（可能不是受支持的机型）："
            + $"精确路径 {path.Path} 失败（{exactError?.Message}）；"
            + $"枚举 {ScopePath} 得到 {seen.Count} 个实例：{names}；{detail}",
            enumError ?? exactError);
    }

    private static bool IsAccessDenied(Exception? ex)
    {
        if (ex is null)
        {
            return false;
        }

        if (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }

        if (ex is ManagementException me)
        {
            var message = me.Message ?? string.Empty;
            return me.ErrorCode == ManagementStatus.AccessDenied
                || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || message.Contains("拒绝访问", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>读取当前性能模式。</summary>
    public static byte GetPowerMode(ManagementObject target)
    {
        using var result = target.InvokeMethod("GetPowerMode", null, null);
        return Convert.ToByte(result["CurrentPowerMode"], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 校验写操作的结果：出参 ResultStatus 必须为 0（0xFF = EC 拒绝/不支持），
    /// 方法自身的 Boolean 返回值也不能是 false。任一条件不满足都抛异常，
    /// 避免“EC 明明拒绝、界面却报成功”的假成功。
    /// </summary>
    /// <param name="result">InvokeMethod 的返回值对象。</param>
    /// <param name="method">方法名，用于异常消息。</param>
    private static void EnsureOk(ManagementBaseObject result, string method)
    {
        // 出参不一定存在（不同固件/驱动版本的签名不完全一致），取不到就当“无此信息”处理
        var status = TryGetStatusByte(result, "ResultStatus");
        if (status is not null && status.Value != 0)
        {
            throw new InvalidOperationException(
                $"{method} 被 EC 拒绝：ResultStatus=0x{status.Value:X2}"
                + (status.Value == 0xFF ? "（EC 不支持该操作或参数）" : string.Empty));
        }

        var returned = TryGetResultBoolean(result, "ReturnValue");
        if (returned is false)
        {
            var detail = status is null ? "无 ResultStatus 出参" : $"ResultStatus=0x{status.Value:X2}";
            throw new InvalidOperationException($"{method} 返回失败（ReturnValue=false，{detail}）");
        }
    }

    /// <summary>取出参里的字节值；属性不存在或无法转换时返回 null。</summary>
    private static byte? TryGetStatusByte(ManagementBaseObject result, string name)
    {
        try
        {
            var value = result.Properties[name]?.Value;
            return value is null ? null : Convert.ToByte(value, CultureInfo.InvariantCulture);
        }
        catch (ManagementException)
        {
            // 没有这个属性
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            // 值类型不符合预期：不据此判定成功，只当缺失
            return null;
        }
    }

    /// <summary>取返回值里的布尔字段；不存在或无法转换时返回 null。</summary>
    private static bool? TryGetResultBoolean(ManagementBaseObject result, string name)
    {
        try
        {
            var value = result.Properties[name]?.Value;
            return value is null ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>设置性能模式。</summary>
    public static void SetPowerMode(ManagementObject target, byte mode)
    {
        using var inParams = target.GetMethodParameters("SetPowerMode");
        inParams["PowerMode"] = mode;
        using var result = target.InvokeMethod("SetPowerMode", inParams, null);
        EnsureOk(result, "SetPowerMode");
    }

    /// <summary>设置风扇占空比（100=满转，101=EC 自动）。</summary>
    public static void SetFanDuty(ManagementObject target, byte duty)
    {
        using var inParams = target.GetMethodParameters("SetFanControl");
        inParams["FanNumber"] = FanNumber;
        inParams["FanDuty"] = duty;
        using var result = target.InvokeMethod("SetFanControl", inParams, null);
        EnsureOk(result, "SetFanControl");
    }

    /// <summary>读取风扇转速（FanDuty 低 16 位 = RPM）。</summary>
    public static ushort GetFanRpm(ManagementObject target)
    {
        using var inParams = target.GetMethodParameters("GetFanControl");
        inParams["FanNumber"] = FanNumber;
        using var result = target.InvokeMethod("GetFanControl", inParams, null);
        var raw = Convert.ToUInt32(result["FanDuty"], CultureInfo.InvariantCulture);
        return (ushort)(raw & 0xFFFF);
    }

    /// <summary>读取温度（低字节=摄氏度）；0x7FFFFFFF 表示不可用，返回 null。</summary>
    public static int? GetTemperature(ManagementObject target)
    {
        using var inParams = target.GetMethodParameters("GetHwTemp");
        inParams["HwTempType"] = (byte)1;
        using var result = target.InvokeMethod("GetHwTemp", inParams, null);
        var raw = Convert.ToUInt32(result["Temp"], CultureInfo.InvariantCulture);
        if (raw == 0x7FFFFFFF)
        {
            return null;
        }

        return (int)(raw & 0xFF);
    }

    /// <summary>模式取值转中文。</summary>
    public static string ModeText(byte mode) => mode switch
    {
        ModeSilent => "静音",
        ModeBalanced => "均衡",
        ModePerformance => "性能",
        _ => $"未知({mode})",
    };
}

/// <summary>把异常翻译成界面上能看懂的一句话。</summary>
internal static class ErrorText
{
    public static string Describe(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return "访问被拒绝：请以管理员身份运行";
        }

        if (ex is ManagementException me)
        {
            var message = me.Message ?? string.Empty;
            var denied = me.ErrorCode == ManagementStatus.AccessDenied
                || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || message.Contains("拒绝访问", StringComparison.Ordinal);
            if (denied)
            {
                return "访问被拒绝：请以管理员身份运行";
            }

            // NotFound / InvalidClass 基本都是机型不支持（或实例名对不上），给一句人话。
            if (me.ErrorCode is ManagementStatus.NotFound or ManagementStatus.InvalidClass
                || message.Contains("Not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("找不到", StringComparison.Ordinal))
            {
                return "未找到 PowerSwitchInterface 实例（可能不是受支持的机型）";
            }

            return $"WMI 调用失败（{me.ErrorCode}）：{message}";
        }

        return ex.Message;
    }
}
