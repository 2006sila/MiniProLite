$root   = Split-Path -Parent $PSScriptRoot            # lite 根目录
$outDir = Join-Path $PSScriptRoot 'results'          # 结果写到脚本同级的 results\
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$out    = Join-Path $outDir 'write-test.txt'
"=== write-test $(Get-Date -Format o) ===" | Set-Content $out -Encoding UTF8
function W($s){ $s | Add-Content $out -Encoding UTF8 }
try {
  $ci = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface -ErrorAction Stop
  $orig = (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode
  W ("原始模式 = $orig")

  foreach($m in 0,1,2){
    $r = Invoke-CimMethod -InputObject $ci -MethodName SetPowerMode -Arguments @{PowerMode=[byte]$m}
    $back = (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode
    W ("SetPowerMode($m) -> ResultStatus=" + $r.ResultStatus + "  ReturnValue=" + $r.ReturnValue + "  回读=" + $back)
  }

  foreach($d in 100,101){
    $r = Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]$d}
    Start-Sleep -Milliseconds 800
    $f = Invoke-CimMethod -InputObject $ci -MethodName GetFanControl -Arguments @{FanNumber=[byte]1}
    W ("SetFanControl(duty=$d) -> ResultStatus=" + $r.ResultStatus + "  ReturnValue=" + $r.ReturnValue + "  RPM=" + ($f.FanDuty -band 0xFFFF))
  }

  $r = Invoke-CimMethod -InputObject $ci -MethodName SetPowerMode -Arguments @{PowerMode=[byte]$orig}
  $back = (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode
  W ("恢复模式 $orig -> ResultStatus=" + $r.ResultStatus + "  回读=" + $back)
  W "=== done ==="
} catch { W ("失败: " + $_.Exception.Message) }
