// ============================================================================
//  MemTrim · Program.cs —— 程序入口
//
//  启动顺序（顺序有意义，勿随意调整）：
//      1) 开启 DPI 感知          —— 必须在创建任何窗口/控件之前
//      2) 单实例互斥体           —— 防止托盘出现两个图标
//      3) 视觉样式与异常兜底     —— EnableVisualStyles 必须在创建窗口前
//      4) 管理员权限自检         —— 清单已申请 requireAdministrator，这里是兜底
//      5) 创建主窗口并进入消息循环
//
//  支持的命令行参数：
//      -tray / --tray / /tray / -min / --minimized   启动后直接最小化到托盘
//      -nocheck / --no-elevation-check               未提权时不弹提权提示（自动化用）
// ============================================================================

using System;
using System.Threading;
using System.Windows.Forms;

namespace MemTrim
{
    internal static class Program
    {
        /// <summary>全局互斥体名：Global\ 前缀让「不同登录会话」也只能有一个实例。</summary>
        private const string MutexName = @"Global\MemTrim_SingleInstance_7f3c9a";

        [STAThread]
        private static void Main(string[] args)
        {
            Native.TryEnableDpiAwareness();

            bool startHidden;
            bool suppressElevationPrompt;
            ParseArguments(args, out startHidden, out suppressElevationPrompt);

            // ---- 单实例 ----
            bool createdNew;
            Mutex mutex = AcquireSingleInstance(out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("MemTrim 已在运行，请查看系统托盘图标。",
                    "MemTrim", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;     // 未持有互斥体，不需要（也不能）ReleaseMutex
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            InstallExceptionHandlers();

            // ---- 权限自检 ----
            // 清单正常情况下已经提权，只有清单被改写/失效时才会走到这里
            if (!Native.IsElevated() && !suppressElevationPrompt && AskToRelaunch()) return;

            // ---- 从旧版本（改名前的 %APPDATA%\MemTrim）迁移配置与历史 ----
            // 必须在 MainForm 构造之前：它一进去就会 AppConfig.Load()
            AppPaths.MigrateLegacyDataDir();

            try
            {
                MainForm form = new MainForm(startHidden);
                if (startHidden)
                {
                    form.WindowState = FormWindowState.Minimized;
                    form.ShowInTaskbar = false;
                }

                form.StartMonitoring();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                MessageBox.Show("MemTrim 启动失败：\n\n" + ex.Message,
                    "MemTrim", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // 共享字体是进程级 GDI 资源，必须在退出前显式释放
                try { UiFonts.DisposeAll(); }
                catch { }

                // 只有确实创建了互斥体才释放，否则抛 ApplicationException
                if (mutex != null)
                {
                    try { mutex.ReleaseMutex(); }
                    catch { }
                    try { mutex.Dispose(); }
                    catch { }
                }
            }
        }

        /// <summary>解析命令行；无法识别的参数一律忽略。</summary>
        private static void ParseArguments(string[] args, out bool startHidden, out bool suppressPrompt)
        {
            startHidden = false;
            suppressPrompt = false;
            if (args == null) return;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == null) continue;

                string arg = args[i].Trim().ToLowerInvariant();
                if (arg == "-tray" || arg == "--tray" || arg == "/tray" ||
                    arg == "-min" || arg == "--minimized")
                {
                    startHidden = true;
                }
                else if (arg == "-nocheck" || arg == "--no-elevation-check")
                {
                    suppressPrompt = true;
                }
            }
        }

        /// <summary>
        /// 尝试创建并持有单实例互斥体。
        /// 返回 null 且 createdNew 为 false：互斥体已被其他实例持有（或无权限打开）。
        /// </summary>
        private static Mutex AcquireSingleInstance(out bool createdNew)
        {
            try
            {
                return new Mutex(true, MutexName, out createdNew);
            }
            catch
            {
                // 极少见：无权限创建全局互斥体。此时放弃单实例保护，但保证程序能启动
                createdNew = true;
                return null;
            }
        }

        /// <summary>未提权时询问是否重新以管理员身份启动；返回 true 表示应当退出当前实例。</summary>
        private static bool AskToRelaunch()
        {
            DialogResult answer = MessageBox.Show(
                "MemTrim 的内存清理需要管理员权限：\n\n" +
                "· 清空系统待机列表需要 SeProfileSingleProcessPrivilege\n" +
                "· 清空其他进程的工作集需要更高的进程访问权限\n\n" +
                "当前未以管理员身份运行，清理效果会大打折扣。\n是否立即以管理员身份重新启动？",
                "MemTrim · 权限不足",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes) return false;

            if (Native.RelaunchElevated()) return true;

            MessageBox.Show("提权失败，将以当前权限继续运行。", "MemTrim",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        /// <summary>兜底异常处理：把异常显示出来，避免托盘图标残留成「幽灵进程」。</summary>
        private static void InstallExceptionHandlers()
        {
            Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("发生未处理的异常：\n\n" + e.Exception.Message,
                    "MemTrim", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                MessageBox.Show("发生未处理的异常：\n\n" + e.ExceptionObject,
                    "MemTrim", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
        }
    }
}
