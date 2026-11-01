$root   = Split-Path -Parent $PSScriptRoot            # lite 根目录
$outDir = Join-Path $PSScriptRoot 'results'          # 结果写到脚本同级的 results\
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$out    = Join-Path $outDir 'diag.txt'
"=== diag $(Get-Date -Format o) ===" | Set-Content $out -Encoding UTF8
function W($s){ $s | Add-Content $out -Encoding UTF8 }
try {
  W "[1] 枚举 PowerSwitchInterface 实例"
  $all = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface -ErrorAction Stop
  foreach($i in $all){ W ("    InstanceName = " + $i.InstanceName + "   Active=" + $i.Active) }
} catch { W ("    枚举失败: " + $_.Exception.Message) }

$cands = @(
  'PowerSwitchInterface.InstanceName=''ACPI\PNP0C14\IP3POWERSWITCH_0''',
  'PowerSwitchInterface.InstanceName=''ACPI\PNP0C14\IP3POWERSWITCH''',
  'PowerSwitchInterface.InstanceName=''ACPI\PNP0C14\IP3POWERSWITCH_0''',
  'PowerSwitchInterface.InstanceName=''ACPI\PNP0C14\IP3POWERSWITCH'''
)
foreach($c in $cands){
  try {
    $o = [wmiclass]"root\WMI:$c"   # 仅用于验证路径能否解析
    $m = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface -Filter ($c -replace '^PowerSwitchInterface\.','') -ErrorAction Stop
    W ("[OK ] " + $c + "  -> GetPowerMode 试调")
    try {
      $r = Invoke-CimMethod -InputObject $m -MethodName GetPowerMode
      W ("      模式 = " + $r.CurrentPowerMode)
      $f = Invoke-CimMethod -InputObject $m -MethodName GetFanControl -Arguments @{FanNumber=[byte]1}
      W ("      扇1 RPM = " + ($f.FanDuty -band 0xFFFF))
      $t = Invoke-CimMethod -InputObject $m -MethodName GetHwTemp -Arguments @{HwTempType=[byte]1}
      W ("      温度原始值 = " + $t.Temp)
    } catch { W ("      方法调用失败: " + $_.Exception.Message) }
  } catch { W ("[ERR] " + $c + "  -> " + $_.Exception.Message) }
}
W "=== done ==="
