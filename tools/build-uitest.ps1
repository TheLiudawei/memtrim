# 构建"无 UAC"UI 测试版（asInvoker 清单），仅用于在自动化环境中检查界面布局。
# 正常运行版本必须使用 src\app.manifest（requireAdministrator）。
$ErrorActionPreference = "Stop"
$w = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "找不到 csc.exe" }
$out = Join-Path $w "dist\MemTrimUiTest.exe"
if (Test-Path $out) { Remove-Item $out -Force }

# 无 UAC 清单：asInvoker，仅用于界面检查
$testManifest = Join-Path $w "obj\app-test.manifest"
$manifestXml = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="MemTrim.app" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
</assembly>
'@
[System.IO.File]::WriteAllText($testManifest, $manifestXml, (New-Object System.Text.UTF8Encoding($false)))

# 源码清单改为动态枚举，新增 .cs 文件不会被漏掉
$src = Get-ChildItem -Path (Join-Path $w "src") -Filter "*.cs" -Recurse |
    Select-Object -ExpandProperty FullName

$lines = @(
    "/nologo","/target:winexe","/platform:anycpu","/optimize+","/warn:4","/langversion:5","/codepage:65001",
    "/out:`"$out`"",
    "/win32manifest:`"$testManifest`"",
    "/win32icon:`"$w\src\app.ico`"",
    "/reference:System.dll","/reference:System.Core.dll","/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll"
) + ($src | ForEach-Object { "`"$_`"" })

$rsp = Join-Path $w "obj\uitest.rsp"
[System.IO.File]::WriteAllLines($rsp, $lines, (New-Object System.Text.UTF8Encoding($true)))
& $csc ("@" + $rsp) 2>&1 | ForEach-Object { Write-Host $_ }
if (-not (Test-Path $out)) { throw "UI 测试版编译失败" }
Write-Host "UI TEST BUILD OK -> $out ($((Get-Item $out).Length) bytes)" -ForegroundColor Green
