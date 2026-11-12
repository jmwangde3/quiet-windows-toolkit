using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuietMode.Core.Configuration
{
    /// <summary>
    /// 特权路径上的读写纪律（RC2-04）。
    ///
    /// 威胁模型：计划任务以 SYSTEM 运行，会在 C:\ProgramData\QuietMode\run 下写请求、
    /// 读结果。如果普通用户能在那两个路径上「预先放一个东西」，就可能是：
    ///   * 预置同名的符号链接 / junction → SYSTEM 的写入被重定向到任意位置；
    ///   * 预置结果文件 → qmctl 把攻击者的内容当成 Worker 的真实输出；
    ///   * 猜中文件名 → 抢占写入。
    ///
    /// 三条对策，全部只依赖文件名与文件属性，不依赖调用者权限：
    ///   1. 文件名带随机 nonce，不可预测；
    ///   2. 写请求用独占新建（目标已存在即失败），绝不覆盖、绝不跟随已有文件；
    ///   3. 目录是重解析点就直接拒绝，一个字节都不写。
    /// </summary>
    public static class PrivilegedIo
    {
        /// <summary>
        /// 生成形如 reconcile-request-20261112-101500-123-a1b2c3d4.json 的名字。
        /// nonce 为空时也保证不与时间戳冲突。
        /// </summary>
        public static string UniqueFileName(string prefix, DateTime now, string nonce)
        {
            string stamp = now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string tail = Sanitize(nonce);
            if (tail.Length == 0) tail = Guid.NewGuid().ToString("N").Substring(0, 8);
            return (prefix ?? "file") + "-" + stamp + "-" + tail + ".json";
        }

        /// <summary>文件名里只允许字母数字，杜绝 ../ 与路径分隔符。</summary>
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>文件属性里是否带重解析点（符号链接 / junction / 挂载点）。</summary>
        public static bool IsReparse(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }

        /// <summary>
        /// 确保目录存在且不是重解析点。是重解析点就抛 —— 调用方据此拒绝写入。
        /// </summary>
        public static void EnsurePlainDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 为空", "path");
            Directory.CreateDirectory(path);

            FileAttributes attrs = File.GetAttributes(path);
            if (IsReparse(attrs))
                throw new IOException("目录是重解析点（符号链接 / junction），拒绝在其下写入：" + path);
            if ((attrs & FileAttributes.Directory) != FileAttributes.Directory)
                throw new IOException("路径不是目录：" + path);
        }

        /// <summary>
        /// 同样的检查，但用于「已经存在的文件」：读结果之前先确认它不是链接。
        /// 不存在返回 false（交给调用方按「Worker 没产出」处理）。
        /// </summary>
        public static bool IsPlainExistingFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                FileAttributes attrs = File.GetAttributes(path);
                if (IsReparse(attrs)) return false;
                return (attrs & FileAttributes.Directory) != FileAttributes.Directory;
            }
            catch { return false; }
        }

        /// <summary>
        /// 独占新建写入：目标已存在（无论是文件还是链接）就抛 IOException。
        /// 这是「绝不跟随预置路径」的落地方式 —— 用 CreateNew 而不是 Create/Truncate。
        /// </summary>
        public static void WriteNewText(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 为空", "path");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) EnsurePlainDirectory(dir);

            byte[] bytes = new UTF8Encoding(false).GetBytes(content ?? "");
            using (FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
        }
    }
}
