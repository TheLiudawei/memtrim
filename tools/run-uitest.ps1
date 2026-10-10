# 启动「无 UAC」UI 测试版，等主窗体出现后截全屏图，便于人工核对布局。
#
# 依赖：先运行 build-uitest.ps1 生成 dist\MemTrimUiTest.exe
# 产物：obj\ui-shot.png
#
# 注意：为了不碰用户真实的 %APPDATA%\MemTrim，本脚本通过环境变量
#       MEMTRIM_DATA_DIR 把数据目录改到 obj\uitest-data\。
$ErrorActionPreference = "Continue"

# 本脚本位于 <仓库根>\tools\ 下
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "dist\MemTrimUiTest.exe"
$shot = Join-Path $root "obj\ui-shot.png"
$dataDir = Join-Path $root "obj\uitest-data"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到 $exe —— 请先运行 tools\build-uitest.ps1"
}
if (Test-Path $shot) { Remove-Item $shot -Force }
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$sig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinEnum {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    public class Info { public IntPtr H; public string Cls; public string Title; public int W; public int Hh; public int X; public int Y; }

    public static List<Info> ForPid(uint target) {
        var list = new List<Info>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != target) return true;
            if (!IsWindowVisible(h)) return true;
            RECT r; GetWindowRect(h, out r);
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            var tb = new StringBuilder(256); GetWindowText(h, tb, 256);
            var i = new Info();
            i.H = h; i.Cls = sb.ToString(); i.Title = tb.ToString();
            i.W = r.Right - r.Left; i.Hh = r.Bottom - r.Top; i.X = r.Left; i.Y = r.Top;
            list.Add(i);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
Add-Type -TypeDefinition $sig

# 把数据目录改到仓库内，避免污染用户真实配置
$env:MEMTRIM_DATA_DIR = $dataDir

# -nocheck：未提权时不弹提权提示（本测试版是 asInvoker 清单）
$p = Start-Process -FilePath $exe -ArgumentList "-nocheck" -PassThru
Write-Host "launched pid=$($p.Id)  data=$dataDir"

# 轮询等主窗（最多 20 秒），而不是死等固定秒数
$main = $null
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 1
    $wins = [WinEnum]::ForPid([uint32]$p.Id)
    $main = $wins | Where-Object { $_.Cls -like 'WindowsForms10.Window*' } |
            Sort-Object { $_.W * $_.Hh } -Descending | Select-Object -First 1
    if ($null -ne $main) { Write-Host "主窗体已出现（第 $i 秒）"; break }
}

if ($null -eq $main) {
    Write-Host "未找到主窗体，列举该进程的所有可见顶层窗口："
    foreach ($x in [WinEnum]::ForPid([uint32]$p.Id)) {
        Write-Host ("  hwnd={0,-10} cls={1,-24} {2}x{3} at {4},{5}  title='{6}'" -f $x.H, $x.Cls, $x.W, $x.Hh, $x.X, $x.Y, $x.Title)
    }
} else {
    Write-Host "chosen main window: $($main.W)x$($main.Hh) cls=$($main.Cls) title=$($main.Title)"
    [void][WinEnum]::SetForegroundWindow($main.H)
    Start-Sleep -Milliseconds 900      # 留几帧给流光动画

    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
    $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($sw, $sh)))
    $g.Dispose()
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "screenshot saved: $shot  (${sw}x${sh})"
}

if (-not $p.HasExited) { $p.Kill(); Write-Host "killed test instance" }
Write-Host "done"
