$root   = Split-Path -Parent $PSScriptRoot            # lite 根目录
$outDir = Join-Path $PSScriptRoot 'results'          # 结果写到脚本同级的 results\
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$out = Join-Path $outDir 'selftest.txt'
# 优先用 dotnet publish 的输出；没有则回退到 dist\ 里的独立 exe
$exe = Join-Path $root 'build\publish\MiniProLite.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $root 'dist\MiniProLite.exe' }
"=== selftest $(Get-Date -Format o) ===" | Set-Content $out -Encoding UTF8
if (-not (Test-Path $exe)) { "exe 不存在" | Add-Content $out; exit 1 }
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = '--selftest'
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
try {
  $p = [System.Diagnostics.Process]::Start($psi)
  $o = $p.StandardOutput.ReadToEnd()
  $e = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  ("exit=" + $p.ExitCode) | Add-Content $out
  "--- stdout ---" | Add-Content $out
  $o | Add-Content $out
  "--- stderr ---" | Add-Content $out
  $e | Add-Content $out
} catch {
  ("启动失败: " + $_.Exception.Message) | Add-Content $out
}
