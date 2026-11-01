$root   = Split-Path -Parent $PSScriptRoot            # lite 根目录
$outDir = Join-Path $PSScriptRoot 'results'          # 结果写到脚本同级的 results\
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$out    = Join-Path $outDir 'lag-test.txt'
"=== lag-test $(Get-Date -Format o) ===" | Set-Content $out -Encoding UTF8
function W($s){ $s | Add-Content $out -Encoding UTF8 }
try {
  $ci = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface -ErrorAction Stop
  $orig = (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode
  W ("原始模式 = $orig")
  $target = if ($orig -eq 0) { 1 } else { 0 }
  W ("写入目标 = $target，然后每 100ms 回读一次，共 20 次（2 秒）")
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Invoke-CimMethod -InputObject $ci -MethodName SetPowerMode -Arguments @{PowerMode=[byte]$target} | Out-Null
  $seq = @()
  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 100
    $v = (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode
    $seq += ("{0}ms={1}" -f $sw.ElapsedMilliseconds, $v)
    if ($v -eq $target -and $i -ge 1) { break }
  }
  W ("回读序列: " + ($seq -join "  "))
  W ("最终模式 = " + (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode + " (目标 $target)")
  Invoke-CimMethod -InputObject $ci -MethodName SetPowerMode -Arguments @{PowerMode=[byte]$orig} | Out-Null
  Start-Sleep -Milliseconds 400
  W ("已恢复 $orig -> 回读=" + (Invoke-CimMethod -InputObject $ci -MethodName GetPowerMode).CurrentPowerMode)
  W "=== done ==="
} catch { W ("失败: " + $_.Exception.Message) }
