// ============================================================================
//  MemTrim · Log.cs —— 极简文本日志缓冲区
//
//  只做一件事：把多行文本拼成字符串，供「清理结果弹窗 / 详情」使用。
//  单独成文件是为了让 MemoryCleaner 专注业务流程，不再夹杂排版细节；
//  同时避免在热路径里反复拼接字符串产生的临时对象。
// ============================================================================

using System.Text;

namespace MemTrim
{
    /// <summary>顺序追加的文本行集合，ToString() 得到完整文本。</summary>
    internal sealed class Log
    {
        private readonly StringBuilder _sb;

        public Log()
        {
            _sb = new StringBuilder(512);
        }

        /// <summary>追加一行不带缩进的文本。</summary>
        public Log Line(string text)
        {
            _sb.Append(text).Append("\r\n");
            return this;
        }

        /// <summary>追加一行「标签: 值」形式的文本。</summary>
        public Log Pair(string label, string value)
        {
            _sb.Append(label).Append(": ").Append(value).Append("\r\n");
            return this;
        }

        /// <summary>追加一行「标签: 数值」形式的文本（避免调用方到处写 ToString("N0")）。</summary>
        public Log Pair(string label, double value, string format)
        {
            _sb.Append(label).Append(": ").Append(value.ToString(format)).Append("\r\n");
            return this;
        }

        /// <summary>追加一行带缩进的明细。</summary>
        public Log Detail(string text)
        {
            _sb.Append("  ").Append(text).Append("\r\n");
            return this;
        }

        /// <summary>追加一个空行。</summary>
        public Log Blank()
        {
            _sb.Append("\r\n");
            return this;
        }

        public override string ToString()
        {
            return _sb.ToString();
        }
    }
}
