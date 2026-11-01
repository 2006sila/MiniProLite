$root   = Split-Path -Parent $PSScriptRoot            # lite 根目录
$outDir = Join-Path $PSScriptRoot 'results'          # 结果写到脚本同级的 results\
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$out    = Join-Path $outDir 'fast-return-test.txt'
"=== fast-return-test $(Get-Date -Format o) ===" | Set-Content $out -Encoding UTF8
function W($s){ $s | Add-Content $out -Encoding UTF8 }
function Snap($ci) {
  $f = Invoke-CimMethod -InputObject $ci -MethodName GetFanControl -Arguments @{FanNumber=[byte]1}
  $t = Invoke-CimMethod -InputObject $ci -MethodName GetHwTemp -Arguments @{HwTempType=[byte]1}
  $raw = [uint32]$t.Temp
  @{ rpm = [int]($f.FanDuty -band 0xFFFF); temp = $(if ($raw -eq 2147483647) { -1 } else { [int]($raw -band 0xFF) }) }
}
function DutyFor([int]$t) {
  if ($t -le 78) { 40 } elseif ($t -le 80) { 50 } elseif ($t -le 85) { 55 }
  elseif ($t -eq 86) { 63 } elseif ($t -le 89) { 79 } else { 90 }
}
try {
  $ci = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface -ErrorAction Stop
  W "--- 1) 满转 100，等 10 秒"
  Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]100} | Out-Null
  Start-Sleep -Seconds 10
  $s = Snap $ci; W ("    起点 rpm=$($s.rpm)  temp=$($s.temp)")

  $duty = DutyFor $s.temp
  W "--- 2) 快速回自动：先写 duty=$duty（按温度查表），1 秒后写 101"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]$duty} | Out-Null
  Start-Sleep -Milliseconds 1000
  $r = Invoke-CimMethod -InputObject $ci -MethodName SetFanControl -Arguments @{FanNumber=[byte]1; FanDuty=[byte]101}
  W ("    101 ResultStatus=$($r.ResultStatus) ReturnValue=$($r.ReturnValue)")

  W "--- 3) 交回后每 5 秒采样，看多久落到 ~2200 以下"
  $done = $false
  for ($i=0; $i -lt 16; $i++) {
    Start-Sleep -Seconds 5
    $s = Snap $ci
    W ("    +{0,3}s  rpm={1}  temp={2}" -f [int]$sw.Elapsed.TotalSeconds, $s.rpm, $s.temp)
    if (-not $done -and $s.rpm -le 2250) { W ("    >>> 降到 2250 以下用时约 {0} 秒" -f [int]$sw.Elapsed.TotalSeconds); $done = $true }
  }
  if (-not $done) { W "    >>> 80 秒内未降到 2250" }
  W "=== done ==="
} catch { W ("失败: " + $_.Exception.Message) }
