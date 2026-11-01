$root   = Split-Path -Parent $PSScriptRoot            # lite 根目录
$outDir = Join-Path $PSScriptRoot 'results'          # 结果写到脚本同级的 results\
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$out    = Join-Path $outDir 'hold-test.txt'
"=== hold-test $(Get-Date -Format o) ===" | Set-Content $out -Encoding UTF8
function W($s){ $s | Add-Content $out -Encoding UTF8 }
function Snap($ci) {
  $f = Invoke-CimMethod -InputObject $ci -MethodName GetFanControl -Arguments @{FanNumber=[byte]1}
  $t = Invoke-CimMethod -InputObject $ci -MethodName GetHwTemp -Arguments @{HwTempType=[byte]1}
  $raw = [uint32]$t.Temp
  @{ rpm = [int]($f.FanDuty -band 0xFFFF); temp = $(if ($raw -eq 2147483647) { -1 } else { [int]($raw -band 0xFF) }) }
}
try {
  $ci = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface -ErrorAction Stop
  W "--- 1) 满转 100，等 12 秒"
  Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]100} | Out-Null
  Start-Sleep -Seconds 12
  $s = Snap $ci; W ("    起点 rpm=$($s.rpm)  temp=$($s.temp)")

  W "--- 2) 写 duty=40 并【保持 15 秒】（每 3 秒采样）"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]40} | Out-Null
  for ($i=0; $i -lt 5; $i++) {
    Start-Sleep -Seconds 3
    $s = Snap $ci
    W ("    +{0,3}s  rpm={1}  temp={2}" -f [int]$sw.Elapsed.TotalSeconds, $s.rpm, $s.temp)
  }

  W "--- 3) 交回 101，再采样 25 秒"
  Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]101} | Out-Null
  for ($i=0; $i -lt 5; $i++) {
    Start-Sleep -Seconds 5
    $s = Snap $ci
    W ("    +{0,3}s  rpm={1}  temp={2}" -f [int]$sw.Elapsed.TotalSeconds, $s.rpm, $s.temp)
  }
  W "=== done ==="
} catch { W ("失败: " + $_.Exception.Message) }
