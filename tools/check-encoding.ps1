# ============================================================
#  校验所有文本文件的编码与行尾是否符合仓库约定
#
#  为什么需要这个检查：
#  Windows PowerShell 5.1 读取 **无 BOM** 的 .ps1 时会按系统 ANSI
#  代码页（简体中文系统 = GBK）解码。本项目源码里有大量中文注释与
#  中文字符串，一旦 BOM 被编辑器/脚本抹掉，PowerShell 解析器就会在
#  中文行附近报「缺少右 }」之类的语法错误，而 .cs 文件也会被 csc
#  按错误编码读取（虽有 /codepage:65001 兜底，但仍是隐患）。
#
#  约定：文本文件统一 UTF-8 + CRLF；.ps1/.cs/.md 等必须带 BOM；
#       .gitignore / .gitattributes 必须无 BOM（带 BOM 会让首个条目失效）。
#
#  用法：powershell -ExecutionPolicy Bypass -File .\tools\check-encoding.ps1
#  退出码：0 = 全部合规，1 = 存在不合规文件
# ============================================================
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# 需要带 BOM 的文本类型
$needBom = @('.cs', '.ps1', '.md', '.xml', '.manifest', '.json', '.tsv', '.yml')
# 必须无 BOM 的（git 直接解析）
$mustNoBom = @('.gitignore', '.gitattributes')

$problems = New-Object System.Collections.Generic.List[string]
$checked = 0

$files = Get-ChildItem $root -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\dist\\|\\\.git\\' }

foreach ($f in $files) {
    $isBomTarget = ($needBom -contains $f.Extension)
    $isNoBomTarget = ($mustNoBom -contains $f.Name)
    if (-not $isBomTarget -and -not $isNoBomTarget) { continue }

    $checked++
    $rel = $f.FullName.Replace($root + '\', '')
    $b = [System.IO.File]::ReadAllBytes($f.FullName)

    $hasBom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)

    if ($isBomTarget -and -not $hasBom) {
        $problems.Add("$rel : 缺少 UTF-8 BOM —— PowerShell 5.1 会按 ANSI/GBK 读取，中文会坏")
    }
    if ($isNoBomTarget -and $hasBom) {
        $problems.Add("$rel : 不应带 BOM —— git 会把 BOM 当成首个条目的内容")
    }

    # 行尾：全部应为 CRLF（有裸 LF 即为混杂）
    $crlf = 0; $bareLf = 0
    for ($i = 0; $i -lt $b.Length; $i++) {
        if ($b[$i] -eq 10) {
            if ($i -gt 0 -and $b[$i - 1] -eq 13) { $crlf++ } else { $bareLf++ }
        }
    }
    if ($bareLf -gt 0) {
        $problems.Add("$rel : 含 $bareLf 处裸 LF（CRLF=$crlf）—— 请统一为 CRLF")
    }
}

Write-Host "已检查 $checked 个文本文件"
if ($problems.Count -eq 0) {
    Write-Host "编码与行尾全部合规（UTF-8 BOM + CRLF）" -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "发现 $($problems.Count) 处不合规：" -ForegroundColor Red
foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
Write-Host ""
Write-Host "修复提示：用支持 UTF-8 BOM + CRLF 的编辑器重存；"
Write-Host "          VS Code 可设置 `"files.encoding`": `"utf8Bom`"、"
Write-Host "          `"files.eol`": `"\r\n`"，并在 .editorconfig / .gitattributes 中固定下来。"
exit 1
