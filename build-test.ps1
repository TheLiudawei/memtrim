# 构建控制台自检程序 dist\MemTrimTest.exe（用于无 GUI 环境下验证核心逻辑）
# 说明：所有编译器参数通过 UTF-8 响应文件（.rsp）传递，
#       避免 PowerShell 5.1 在含中文字符的路径上出现参数编码问题。
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$dist = Join-Path $root "dist"
$obj = Join-Path $root "obj"

$csc = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "找不到 csc.exe" }

foreach ($d in @($dist, $obj)) { if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null } }
$outExe = Join-Path $dist "MemTrimTest.exe"
if (Test-Path $outExe) {
    # 上一次自检若仍在运行会锁住文件；只提示不中断，交由编译器报错。
    try { Remove-Item $outExe -Force -ErrorAction Stop }
    catch { Write-Host "提示：旧的 MemTrimTest.exe 正被占用（自检可能还在跑）。" -ForegroundColor Yellow }
}

Copy-Item (Join-Path $root "tools\SelfTest.cs") (Join-Path $obj "SelfTest.cs") -Force

# 自检程序包含 src 全部源文件（连 Ui.cs / Program.cs 一起编译），
# 因此必须显式指定入口点，否则会与 Program.Main 冲突。
# 源码清单动态枚举，新增 .cs 文件不会被漏掉。
$coreSources = Get-ChildItem -Path $src -Filter "*.cs" -Recurse |
    Select-Object -ExpandProperty FullName

$rspPath = Join-Path $obj "test.rsp"
$lines = @(
    "/nologo",
    "/target:exe",
    "/platform:anycpu",
    "/optimize+",
    "/warn:4",
    "/langversion:5",
    "/codepage:65001",
    "/main:MemTrim.SelfTest",
    "/out:`"$outExe`"",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll"
) + ($coreSources | ForEach-Object { "`"$_`"" }) + @("`"$(Join-Path $obj 'SelfTest.cs')`"")
[System.IO.File]::WriteAllLines($rspPath, $lines, (New-Object System.Text.UTF8Encoding($true)))

$buildStarted = Get-Date
& $csc ("@" + $rspPath) 2>&1 | ForEach-Object { Write-Host $_ }

# 与 build.ps1 同理：只看「文件是否存在」会把「编译失败但旧文件还在」报成成功
if (-not (Test-Path $outExe)) { throw "自检程序编译失败：未生成 $outExe" }
$fi = Get-Item $outExe
if ($fi.LastWriteTime -lt $buildStarted) {
    throw "自检程序编译失败：$($fi.Name) 是旧文件（$($fi.LastWriteTime.ToString('HH:mm:ss'))），本次编译没能写入它（可能仍在运行）。"
}
Write-Host ("TEST BUILD OK -> {0} ({1:N0} bytes)" -f $outExe, $fi.Length) -ForegroundColor Green
