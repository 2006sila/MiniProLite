$root   = Split-Path -Parent $PSScriptRoot            # lite 根目录
$outDir = Join-Path $PSScriptRoot 'results'          # 结果写到脚本同级的 results\
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$out    = Join-Path $outDir 'decay-test.txt'
"=== decay-test $(Get-Date -Format o) ===" | Set-Content $out -Encoding UTF8
function W($s){ Write-Host $s; $s | Add-Content $out -Encoding UTF8 }
function Snap($ci) {
  $f = Invoke-CimMethod -InputObject $ci -MethodName GetFanControl -Arguments @{FanNumber=[byte]1}
  $t = Invoke-CimMethod -InputObject $ci -MethodName GetHwTemp -Arguments @{HwTempType=[byte]1}
  $raw = [uint32]$t.Temp
  $temp = if ($raw -eq 2147483647) { -1 } else { [int]($raw -band 0xFF) }
  return @{ rpm = [int]($f.FanDuty -band 0xFFFF); temp = $temp }
}
try {
  $ci = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface -ErrorAction Stop
  $m = (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode
  $s = Snap $ci
  W ("起始: 模式=$m  温度=$($s.temp)C  转速=$($s.rpm)")

  W "--- 写入 FanDuty=100（满转）"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $r = Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]100}
  W ("    ResultStatus=$($r.ResultStatus) ReturnValue=$($r.ReturnValue)")
  for ($i=0; $i -lt 6; $i++) {
    Start-Sleep -Seconds 2
    $s = Snap $ci
    W ("    +{0,3}s  温度={1}C  转速={2}" -f [int]$sw.Elapsed.TotalSeconds, $s.temp, $s.rpm)
  }

  W "--- 写入 FanDuty=101（交回 EC 自动），之后每 5 秒采样"
  $sw2 = [Diagnostics.Stopwatch]::StartNew()
  $r = Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]101}
  W ("    ResultStatus=$($r.ResultStatus) ReturnValue=$($r.ReturnValue)")
  for ($i=0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 5
    $s = Snap $ci
    W ("    +{0,3}s  温度={1}C  转速={2}" -f [int]$sw2.Elapsed.TotalSeconds, $s.temp, $s.rpm)
  }
  W "=== done ==="
} catch { W ("失败: " + $_.Exception.Message) }
