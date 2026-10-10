# 运行自检程序并收集日志（quick / clean / smoke / all）。
#
# 为什么要用脚本而不是直接在命令行里跑：命令行传含中文的参数时，
# PowerShell 5.1 的编码处理会出问题；这里用 ProcessStartInfo 显式指定
# UTF-8 输出编码，中文结果不会变成乱码。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1            # 默认 quick
#   powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1 clean      # 真实清理（建议管理员）
param(
    [string]$Mode = "quick"
)

$ErrorActionPreference = "Continue"

# 本脚本位于 <仓库根>\tools\ 下
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "dist\MemTrimTest.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "未找到自检程序，先构建..." -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root "build-test.ps1")
    if (-not (Test-Path -LiteralPath $exe)) { throw "自检程序构建失败" }
}

$log = Join-Path $root "obj\selftest.log"
if (-not (Test-Path (Split-Path -Parent $log))) { New-Item -ItemType Directory -Path (Split-Path -Parent $log) | Out-Null }
if (Test-Path $log) { Remove-Item $log -Force }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = $Mode
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
$psi.WorkingDirectory = (Join-Path $root "dist")

$proc = [System.Diagnostics.Process]::Start($psi)
$out = $proc.StandardOutput.ReadToEnd()
$err = $proc.StandardError.ReadToEnd()
$proc.WaitForExit(180000) | Out-Null

[System.IO.File]::WriteAllText($log, $out + "`r`n--- STDERR ---`r`n" + $err,
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "mode  = $Mode"
Write-Host "exit  = $($proc.ExitCode)"
Write-Host "binary still present: $(Test-Path -LiteralPath $exe)   # 若为 False，多半是被杀软删了"
Write-Host "log   = $log"
Write-Host "----- output -----"
Write-Host $out
Write-Host "----- stderr -----"
Write-Host $err
