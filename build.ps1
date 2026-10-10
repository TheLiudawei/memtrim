<#
    MemTrim 构建脚本
    ---------------------------------------------------------------
    使用 Windows 自带的 .NET Framework C# 编译器 (csc.exe) 编译，
    不依赖 .NET SDK / NuGet / 任何第三方运行时。
    用法： powershell -ExecutionPolicy Bypass -File build.ps1
    产物： dist\MemTrim.exe
#>
param(
    [string]$Configuration = "Release",
    [switch]$NoIcon
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$dist = Join-Path $root "dist"
$obj = Join-Path $root "obj"

Write-Host "== MemTrim build ==" -ForegroundColor Cyan
Write-Host "root: $root"

# ---- 1. 定位 csc.exe ----
$cscCandidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "找不到 csc.exe（.NET Framework 4.x 未安装？）" }
Write-Host "csc : $csc"

# ---- 2. 准备输出目录 ----
foreach ($d in @($dist, $obj)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}
$outExe = Join-Path $dist "MemTrim.exe"
if (Test-Path $outExe) {
    # 正在运行的 MemTrim 会锁住自己；普通权限也删不掉提权进程的镜像。
    # 这里只提示，不中断 —— 测试版 exe 不受影响，等它退出后再重建即可。
    try { Remove-Item $outExe -Force -ErrorAction Stop }
    catch { Write-Host "提示：旧的 MemTrim.exe 正被占用（可能还在运行），无法删除。" -ForegroundColor Yellow
            Write-Host "      请先从托盘退出 MemTrim，否则本次编译会失败。" -ForegroundColor Yellow }
}

# ---- 3. 生成图标（用 tools\IconGen.cs 编译的小工具，纯 GDI+ 绘制）----
$iconPath = Join-Path $src "app.ico"
if (-not (Test-Path $iconPath)) {
    Write-Host "icon: 生成中..." -ForegroundColor Cyan
    $iconGenExe = Join-Path $obj "IconGen.exe"
    & $csc /nologo /target:exe /optimize+ "/out:$iconGenExe" /reference:System.dll /reference:System.Drawing.dll (Join-Path $root "tools\IconGen.cs") | Out-Null
    if (-not (Test-Path $iconGenExe)) { throw "IconGen 编译失败" }
    & $iconGenExe $iconPath
    if (-not (Test-Path $iconPath)) { throw "图标生成失败" }
}
Write-Host "icon: $iconPath ($((Get-Item $iconPath).Length) bytes)"

# ---- 4. 收集源文件 ----
$sources = Get-ChildItem -Path $src -Filter "*.cs" -Recurse | Select-Object -ExpandProperty FullName
if (-not $sources) { throw "src 目录下没有 .cs 源文件" }
Write-Host "sources: $($sources.Count) file(s)"

# ---- 5. 调用编译器 ----
# 注意：这里只列真正用到的程序集。多引一个 System.Management 会让 csc 把
# 程序集引用写进元数据（虽然不会被加载），而本项目并不需要 WMI。
$refs = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll"
)

$argList = New-Object System.Collections.Generic.List[string]
$argList.Add("/nologo")
$argList.Add("/target:winexe")
$argList.Add("/platform:anycpu")
$argList.Add("/optimize+")
$argList.Add("/warn:4")
$argList.Add("/langversion:5")
$argList.Add("/codepage:65001")
$argList.Add("/out:`"$outExe`"")
$argList.Add("/win32manifest:`"$(Join-Path $src 'app.manifest')`"")
if ($iconPath -and (Test-Path $iconPath)) { $argList.Add("/win32icon:`"$iconPath`"") }
foreach ($r in $refs) { $argList.Add("/reference:$r") }
foreach ($s in $sources) { $argList.Add("`"$s`"") }

# 通过 UTF-8 响应文件传参：避免含中文的路径在命令行传递时被错误编码
$rspPath = Join-Path $obj "app.rsp"
[System.IO.File]::WriteAllLines($rspPath, $argList.ToArray(), (New-Object System.Text.UTF8Encoding($true)))

Write-Host "compiling..." -ForegroundColor Cyan
$buildStarted = Get-Date
& $csc ("@" + $rspPath) 2>&1 | ForEach-Object { Write-Host $_ }

# 不能只判断「文件存在」：若旧的 MemTrim.exe 正被占用，csc 会写入失败，
# 而旧文件仍在原地 —— 那样脚本会把失败的编译报成 BUILD OK。
if (-not (Test-Path $outExe)) {
    throw "编译失败：未生成 $outExe"
}
$fi = Get-Item $outExe
if ($fi.LastWriteTime -lt $buildStarted) {
    # 注意：-f 必须写在括号内、throw 之外不能挂运算符
    $msg = "编译失败：{0} 是旧文件（{1:HH:mm:ss}），本次编译没能写入它。`n" +
           "它多半正被运行中的 MemTrim 占用 —— 请先从托盘退出 MemTrim 再重建。"
    throw ($msg -f $fi.Name, $fi.LastWriteTime)
}

Write-Host ""
Write-Host ("BUILD OK -> {0}  ({1:N0} bytes / {2:N1} KB)" -f $fi.FullName, $fi.Length, ($fi.Length / 1KB)) -ForegroundColor Green
Write-Host "提示：exe 需要管理员权限（UAC），双击即可。" -ForegroundColor DarkGray
