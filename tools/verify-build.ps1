# 校验最终产物的嵌入清单（requireAdministrator）、图标与版本信息。
# 路径全部相对本脚本推导，克隆到任何位置都能直接跑。
$ErrorActionPreference = "Stop"

# 本脚本位于 <仓库根>\tools\ 下，上跳一级即仓库根
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "dist\MemTrim.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到 $exe —— 请先运行 build.ps1"
}

$fi = Get-Item -LiteralPath $exe
Write-Host ("文件: {0}" -f $fi.FullName)
Write-Host ("大小: {0:N0} bytes ({1:N1} KB)" -f $fi.Length, ($fi.Length / 1KB))

$bytes = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)

Write-Host ""
Write-Host "== 嵌入清单检查 =="
$checks = @{
    "requestedExecutionLevel" = 'requestedExecutionLevel'
    "requireAdministrator"    = 'requireAdministrator'
    "supportedOS Win10/11"    = '8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a'
    "assemblyIdentity"        = 'MemTrim.app'
}
foreach ($k in $checks.Keys) {
    $found = $text.Contains($checks[$k])
    Write-Host ("  {0,-26} {1}" -f $k, $(if ($found) { "OK" } else { "缺失!" }))
}

Write-Host ""
Write-Host "== 资源检查 =="
Write-Host ("  RT_ICON 数据存在           {0}" -f $(if ($text.Contains("PNG")) { "OK (PNG 图标已嵌入)" } else { "未找到 PNG 图标数据" }))

Write-Host ""
Write-Host "== .NET 程序集属性 =="
try {
    $asm = [System.Reflection.AssemblyName]::GetAssemblyName($exe)
    Write-Host ("  Name    : {0}" -f $asm.Name)
    Write-Host ("  Version : {0}" -f $asm.Version)
    Write-Host ("  Runtime : CLR {0}.{1}" -f $asm.Version.Major, $asm.Version.Minor)
} catch {
    Write-Host ("  读取失败: {0}" -f $_.Exception.Message)
}

Write-Host ""
Write-Host "== 依赖检查（不应有第三方依赖）=="
# 用反射读出引用的程序集。注意两个坑：
#   1) 不能只 try 一次就完事 —— 若仓库在网络路径/受限挂载点上，LoadFile 会被
#      CAS 策略拒绝；把这种失败悄悄吞掉，这条检查就变成「永远静默跳过」，
#      而它恰恰是用来证明「零第三方依赖」的。所以失败后复制到本地临时目录重试。
#   2) 不用 Assembly.Load（会执行代码）；LoadFile 只读元数据。
$refs = $null
$refErr = $null
try {
    $asmObj = [System.Reflection.Assembly]::LoadFile($exe)
    $refs = @($asmObj.GetReferencedAssemblies() | ForEach-Object { $_.Name })
    $asmObj = $null
} catch {
    $refErr = $_.Exception.Message
}

if ($null -eq $refs) {
    # 退路：复制到本地临时文件再读（绕过「网络位置」的 CAS 限制）
    $tmp = Join-Path $env:TEMP ("memtrim-verify-{0}.exe" -f ([guid]::NewGuid().ToString("N")))
    try {
        Copy-Item -LiteralPath $exe -Destination $tmp -Force
        $asmObj = [System.Reflection.Assembly]::LoadFile($tmp)
        $refs = @($asmObj.GetReferencedAssemblies() | ForEach-Object { $_.Name })
        $asmObj = $null
    } catch {
        $refErr = $_.Exception.Message
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

if ($null -eq $refs -or $refs.Count -eq 0) {
    Write-Host ("  无法读取引用（未能证明零依赖）: {0}" -f $refErr) -ForegroundColor Yellow
} else {
    $refs | Sort-Object -Unique | ForEach-Object { Write-Host ("  -> {0}" -f $_) }
    $thirdParty = $refs | Where-Object {
        $_ -notmatch '^(System|Microsoft|mscorlib|WindowsBase|PresentationCore|PresentationFramework)'
    }
    if ($thirdParty) {
        Write-Host ("  第三方依赖: {0}" -f ($thirdParty -join ", ")) -ForegroundColor Yellow
    } else {
        Write-Host "  第三方依赖: 无" -ForegroundColor Green
    }
}
