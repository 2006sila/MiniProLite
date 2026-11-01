# MiniProLite

> 一台迷你主机（Lecoo MINI PRO-AHP / AMD Ryzen 7 8745H）的**极简风扇与性能模式控制台**。
> 只做两件事：切「静音 / 均衡 / 性能」三档性能模式，以及把风扇「满转 100%」或「交回 EC 自动」。
> 用来替代厂商自带的重型控制中心（Python + PyQt + 服务 + 联网升级那一整套）。

```
温度 46°C · 风扇 2100 RPM · 模式 均衡 · 风扇控制 自动

性能模式   [静音] [均衡] [性能]
风扇       [满转 100%] [EC 自动]

点 X 只收进托盘；要彻底退出请用托盘菜单「退出（交回 EC 自动）」
```

## 下载

| 文件 | 说明 |
|---|---|
| `MiniProLite-Setup-1.0.0.exe` | 安装版（Inno Setup；装到 `Program Files\MiniProLite`，带开始菜单与卸载项） |
| `MiniProLite-1.0.0-portable.zip` | 绿色版（解压即用，单文件 exe） |
| `MiniProLite-1.0.0-source.zip` | 源码包（内容与本仓库一致） |
| `SHA256SUMS.txt` | 三个包的 SHA-256 |

全部在 [Releases](https://github.com/2006sila/MiniProLite/releases) 页。
前置条件：Windows 10/11 x64 + [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)。
首次运行会弹 UAC —— WMI 类 `PowerSwitchInterface` 只允许提权访问。

---

## 1. 它解决什么问题

厂商软件的问题（逆向之后看到的）：

| 厂商实现 | 后果 |
|---|---|
| ControlCenter 是 Python 3.6 + PyQt5 打包的 40 MB 大块头 | 启动慢、占内存、依赖一堆 DLL |
| 风扇 RPM 显示用 `random.randint(0,100)` 伪造 | 界面上那个转速数字是**假的** |
| 风扇「自动」= 写一个哨兵值 101 交回 EC，但界面不说 | 用户以为软件在调速，其实什么都没做 |
| 内置 FTP 升级（硬编码账号密码） | 服务器已下线，点了只会报错 |
| 额外常驻 OSD 程序 + Windows 服务 | 只为画个音量/CapsLock 小图标 |

MiniProLite 的选择：**只保留真正起作用的两条硬件命令**，全部逻辑不到 2000 行，无服务、无网络、无遥测。

---

## 2. 这台主机的硬件控制是怎么实现的（重点）

### 2.1 链路：WMI → ACPI → EC

```
MiniProLite.exe
   └─ System.Management（C#）
        └─ WMI 命名空间 root\WMI
             └─ 类 PowerSwitchInterface
                  └─ 实例 ACPI\PNP0C14\IP3POWERSWITCH_0
                       └─ wmiacpi.sys（Windows 自带的 ACPI-WMI 映射驱动）
                            └─ BIOS 里的 ACPI 方法（ASL，编译成 BMOF）
                                 └─ 嵌入式控制器 EC（ACPI\PNP0C09）
                                      └─ 风扇 PWM / 性能模式寄存器
```

关键点：**没有任何厂商内核驱动参与**。用户态只能碰 WMI，真正写寄存器的是 BIOS 的 ACPI 方法（ASL）经 EC 完成。所以：

- 不需要装驱动、不需要动内核
- 也不会（不能）修改 EC 固件 —— 想做自定义风扇曲线只能"手动接管"，不能改曲线本身

### 2.2 BIOS 侧的分发器（反编译 DSDT 得到）

`\_SB.PCI0.SBRG.EC0_` 上有一个 WMI 分发方法，按命令号分派（这是本机 BIOS 的真实实现）：

| cmd | 语义 | 触及的 EC 字段 |
|---|---|---|
| 1 | SetPowerMode | `FCMI = 0x80 \| mode`，并写 AMI 邮箱 `AMW0.FEBC[0]=1` |
| 2 | GetPowerMode | `FCMO`（EC 侧当前模式，**回读有约 100 ms 延迟**） |
| 3 | SetFanControl | `FAN1/FAN2 = 0x80 \| duty`（duty ≤ 0x65，**101 = 交回自动**） |
| 4 | GetFanControl | 把 `FN1L/FN1H`、`FN2L/FN2H` 打包成一个 DWORD：**低 16 位 = 风扇1 RPM** |
| 9 / 10 | 键盘背光 / Fn 位（本机无键盘背光，未使用） | `KBF*`、`KBCH/KBCL` |
| 11 | 读温度 | 打包 `CPUT / GPUT / CPAD / GPAD` |
| 12 | SetFeatureValue | 写 `SEF0..SEF3` + `SEF7` 提交位 |
| 13 | GetFeatureValue | 读 `OSD0` 状态字节 / 常量 `PMNB`、`FANB`、`GPUS` |

BIOS 里写死的三个能力常量（决定了本机的行为边界）：

```
PMNB = 3   → 只有三档性能模式（无 Super 模式）
FANB = 1   → 单风扇
GPUS = 0   → 无独立显卡
```

### 2.3 这台机器的实测常数（本文档的数字都来自真机测量）

| 项 | 值 | 说明 |
|---|---|---|
| 模式编号 | `0=均衡`、`1=性能`、`2=静音` | 写入后 `GetPowerMode` 回读有 **≈100 ms** 延迟 |
| 写成功判定 | `ResultStatus=0` 且 `ReturnValue=True` | EC 拒绝时 `ResultStatus=0xFF` |
| 风扇满转 | `FanDuty=100` → 稳态 **≈4083 RPM** | 升速快，约 8 秒到位 |
| EC 自动 | `FanDuty=101` | 超出 0–100 的哨兵值，EC 回到自己的温控曲线 |
| 温度 | `GetHwTemp(1)` 低字节 = **°C** | `0x7FFFFFFF` = 不可用 |
| 占空比→转速 | 20%→1200、35%→1936、50%→2536、65%→3042、80%→3521、100%→4030 | 明显次线性 |
| 自动曲线 | ≤76°C→≈2100、81–85°C→≈2800、87–88°C→≈3500 | **三级阶梯**，不是线性 |
| EC 自动降速斜率 | **≈15 RPM/s**（4000→2100 要 130 秒） | 固件防抖，故意很慢 |
| 手动低占空比降速 | **≈90 RPM/s**（4000→2200 只要 20 秒） | 风扇本体响应，时间常数约 20 秒 |

### 2.4 三个"坑"（都踩过，代码里也有注释）

1. **WMI 对象路径不要转义反斜杠**
   `ManagementPath` 是 CIM 对象路径，不是 WQL 查询。写成 `ACPI\\PNP0C14\\...` 会直接 `NotFound`。
   （厂商 Python 源码里的 `\\` 只是 Python 字符串转义，运行时是单反斜杠。）

2. **模式回读有延迟，不能"写完立刻比对"**
   写完马上读会看到"上一次"的值（写 1 读回 0），看起来像失败。本程序改成**轮询等待**（8×120ms，追上即返回），超时也不报错——因为 `ResultStatus=0` 已经证明 EC 接受了命令。

3. **EC 自动模式降速极慢，且"点一下手动低 duty"没用**
   必须**持续保持**十几秒手动低占空比，风扇才会真的降下来（时间常数 ~20 秒）；只保持 1 秒等于没做（实测 4007→2414 花了 81 秒，和纯 101 一样）。

---

## 3. 程序做了什么

### 3.1 两个功能

| 操作 | 底层调用 |
|---|---|
| 静音 / 均衡 / 性能 | `SetPowerMode(2/0/1)` → 轮询回读确认 → 刷新界面 |
| 满转 100% | `SetFanControl(FanNumber=1, FanDuty=100)` |
| EC 自动 | **快速回自动**：按当前温度写一个接近 EC 曲线的占空比 → 保持 15 秒 → 写 101 |

「快速回自动」的实测效果：**4000 → 2100 从 130 秒缩短到约 35 秒**，且整个过程温度不上升。
期间有温度守卫：温度比开始时涨 5 °C 或达到 88 °C 会**立即交回**，不让手动值压着散热。

### 3.2 安全设计（7 条）

1. 写操作校验 `ResultStatus` / `ReturnValue`，EC 拒绝时不假装成功
2. 模式回读轮询等待，避免假失败
3. WMI 绑定失效自动丢弃并重连（打开失败 5 秒退避）
4. **注销 / 关机**时在 `OnSessionEnding` 里写 101 交回 EC（幂等，绝不阻塞关机）
5. 「上次退出是手动满转」会落盘（`%LOCALAPPDATA%\MiniProLite\fan-state.json`），下次启动弹气泡提醒（覆盖"被强杀"场景）
6. 开机自启用**最高权限计划任务**（不是 Run 键：Run 键启的进程未提权，碰 WMI 会 Access Denied）
7. 单实例 Mutex；`schtasks` 调用并发读 stdout/stderr（避免管道死锁）

### 3.3 代码结构

| 文件 | 职责 |
|---|---|
| `PowerSwitch.cs` | WMI 封装：绑定实例（精确路径 + 枚举兜底）、读模式/温度/转速、写模式/风扇、`ResultStatus` 校验、错误文案翻译 |
| `PowerMonitor.cs` | 唯一状态持有者：1 秒轮询、写操作、快速回自动的 15 秒保持与温度守卫、`Changed` 事件、退出时交回 EC |
| `AutoStart.cs` | 计划任务增删查（`schtasks` + `/XML` 解析）：识别"被禁用"的任务、exe 换路径时自动重建 |
| `FanState.cs` | 手动满转状态落盘（仅本地 JSON，无网络） |
| `TrayIcon.cs` | 托盘图标 + 右键菜单（状态、模式、风扇、开机自启、退出）|
| `MainWindow.xaml(.cs)` | 主窗口：状态行 + 3 个模式按钮 + 2 个风扇按钮；X 只收进托盘 |
| `App.xaml(.cs)` | 生命周期：单实例、`--tray`、`--selftest`、启动提示、`OnSessionEnding` 兜底 |
| `app.ico` / `app.manifest` | 图标（仪表盘）/ `requireAdministrator` 清单 |

### 3.4 命令行

```text
MiniProLite.exe              正常启动（弹 UAC → 显示窗口）
MiniProLite.exe --tray       静默进托盘、只读状态、不接管风扇（开机自启用的就是它）
MiniProLite.exe --selftest   读一次 模式/温度/转速 打印后退出（0=成功，2=失败，需提权）
```

---

## 4. 构建

需要 **.NET 9 SDK** 和 Windows 10/11 x64。

```powershell
dotnet build MiniProLite\MiniProLite.csproj -c Release
dotnet publish MiniProLite\MiniProLite.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o build\publish
```

产物是单个 `MiniProLite.exe`（约 680 KB，framework-dependent，需要 **.NET 9 Desktop Runtime**）。

> 想免运行时：把 `--self-contained false` 换成 `true`，体积会变成 70 MB 左右。

### 4.1 打安装包

需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)：

```powershell
iscc installer\MiniProLite.iss
```

### 4.2 重新生成图标

候选方案在 `icons\`（8 套，含 `preview-all.png` 对比图），生成器在 `icon-gen\`：

```powershell
dotnet run --project icon-gen -c Release -- icons
```

---

## 5. 已知限制（改不了，属于硬件/固件与设计取舍）

1. **风扇控制状态读不回来**：WMI 只暴露 RPM，没有"当前是手动还是自动"的读法。所以「自动/满转」是进程内状态；在厂商软件里改过风扇后，这里显示会不同步
2. **强杀进程 / 断电**兜不住：注销和关机会写 101 交回 EC，但强杀不会（靠下次启动的气泡提醒）
3. **轻载下三档几乎没区别**：模式改的是功耗墙，只有跑到功耗墙（满载）才看得出温度/功耗差异
4. **开机自启绑绝对路径**：换了 exe 位置/换机器要重新勾一次（程序会尝试自动重建，但换盘符仍需手点）
5. **只支持这一台机型**：实例名和 `PMNB/FANB/GPUS` 都来自该 BIOS；别的机器上会报「未找到 PowerSwitchInterface 实例」
6. 托盘图标在高 DPI 下用 ico 里 24/32 帧，未逐档实拍验证

---

## 6. 验收清单（真机，需管理员）

1. 双击 `MiniProLite.exe` → UAC → 窗口出现，状态行有温度/转速、无红字
2. 点「性能」→ 状态行变「性能」，高亮跟上；点「静音」→ 同理（不应出现假错误）
3. 点「满转 100%」→ 转速冲到 4000+；点「EC 自动」→ 状态行显示**「回落中」约 15 秒**，转速快速掉到 2600 左右，随后变回「自动」
4. 点 X → 收进托盘，风扇保持当前状态；双击托盘 → 窗口回来
5. 托盘菜单「退出（交回 EC 自动）」→ 退出后转速应回落
6. 托盘勾「开机自启」→ `schtasks /Query /TN MiniProLite /FO LIST` 能看到，Run Level 为 `Highest`
7. 切到「性能」后直接关机再开机 → 风扇不应停在满转

命令行自检（提权 PowerShell）：

```powershell
& .\MiniProLite.exe --selftest   # 期望：模式/温度/风扇 三行 + "结果: OK"，exit 0
```

---

## 7. 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| `错误：访问被拒绝：请以管理员身份运行` | 未提权（比如计划任务没配 `/RL HIGHEST`） |
| `未找到 PowerSwitchInterface 实例（可能不是受支持的机型）` | 机器不是该 BIOS 的机型；实例名见 §2.1 |
| `模式切换未生效：目标 性能，回读 均衡` | 极罕见：EC 未在 ~1 秒内更新 `FCMO`；再点一次即可 |
| 风扇一直很吵不降速 | 正常现象：EC 自动降速只有 15 RPM/s。用「EC 自动」按钮走快速回自动，或直接点满转再交回 |
| 关窗后程序"消失"了 | 点 X 只是收进托盘；从托盘菜单「打开窗口」或在托盘图标上双击 |
| 托盘没有图标 | 检查是否被系统折叠到隐藏区；高 DPI 下可在 `icons` 里挑 24/32 帧特化版本 |

`diagnostics\` 下有几个可直接跑的诊断脚本（都需要管理员，输出写到同目录）：

| 脚本 | 用途 |
|---|---|
| `selftest.ps1` | 跑 `--selftest` 并落盘 |
| `diag.ps1` | 枚举 WMI 实例、验证实例名与路径 |
| `write-test.ps1` | 三档写入 + 回读 + 风扇 100/101，验证 `ResultStatus` |
| `lag-test.ps1` | 测模式回读延迟（本机 ≈100 ms） |
| `decay-test.ps1` | 满转后交回 101 的完整衰减曲线 |
| `slew-test.ps1` | 手动低占空比的降速速率（≈90 RPM/s） |
| `hold-test.ps1` | 保持 15 秒再交回的实际效果（≈35 秒落地） |

> 这些 `.ps1` 必须保存为 **UTF-8 with BOM**，否则 PowerShell 5.1 会按 GBK 读而语法报错。

---

## 8. 隐私与安全声明

- **无网络**：程序不发起任何网络请求，没有遥测、没有升级检查、没有账号
- **无上传**：唯一写盘的是 `%LOCALAPPDATA%\MiniProLite\fan-state.json`（一个布尔值），用于"上次是否手动满转"的提醒
- **最小权限**：需要管理员仅因为 WMI 类 `PowerSwitchInterface` 只允许提权访问；不安装驱动、不改固件、不碰内核
- **可回退**：卸载/退出会把风扇交回 EC 自动；删掉程序目录 + 计划任务 `MiniProLite` 即完全清理

---

## 9. 许可

MIT，见 `LICENSE`。这是一个独立开发的实验性工具，与 Lecoo / 联想 及其代工厂无关，未获其认可或支持。
硬件控制有风险（风扇、温度、保修），请自行判断。

---

## 10. 目录结构

```
（仓库根目录）
├─ MiniProLite/          可执行程序源码（C# / WPF / .NET 9）
├─ icon-gen/             图标生成器（生成 8 套候选 + ico）
├─ icons/                8 套候选图标（.ico / .png / preview-all.png）
├─ installer/            Inno Setup 打包脚本
├─ diagnostics/          诊断脚本（需管理员）
├─ build/                构建中间产物（不入库）
├─ dist/                 发布产物（安装包 / 绿色版 / 源码包 / 校验和）
└─ README.md             本文档
```
