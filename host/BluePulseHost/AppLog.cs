using System;
using System.IO;

namespace BluePulseHost
{
    /// <summary>
    /// 简单的文件日志：追加写入 %LOCALAPPDATA%\BluePulse\host.log，超过 1MB 时滚动为 .old。
    /// 所有 IO 均用 try/catch 包裹，日志失败绝不影响主程序运行。
    /// </summary>
    internal static class AppLog
    {
        /// <summary>日志目录：%LOCALAPPDATA%\BluePulse</summary>
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BluePulse");

        /// <summary>日志文件完整路径</summary>
        private static readonly string LogFile = Path.Combine(LogDir, "host.log");

        /// <summary>滚动阈值：1MB</summary>
        private const long MaxLogBytes = 1024 * 1024;

        /// <summary>保证各线程写日志互不交错</summary>
        private static readonly object SyncRoot = new object();

        /// <summary>记录一条信息日志</summary>
        /// <param name="message">日志内容</param>
        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        /// <summary>记录一条错误日志，附带异常详情</summary>
        /// <param name="message">日志内容</param>
        /// <param name="ex">异常（可为 null）</param>
        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        /// <summary>实际写日志的核心方法</summary>
        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDir);

                    // 超过 1MB 时滚动：把当前日志改名为 .old（覆盖旧的 .old），重新开始记录
                    if (File.Exists(LogFile))
                    {
                        var info = new FileInfo(LogFile);
                        if (info.Length >= MaxLogBytes)
                        {
                            string oldFile = LogFile + ".old";
                            if (File.Exists(oldFile))
                            {
                                File.Delete(oldFile);
                            }
                            File.Move(LogFile, oldFile);
                        }
                    }

                    string line = string.Format(
                        "[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}",
                        DateTime.Now, level, message);

                    if (ex != null)
                    {
                        line += Environment.NewLine + ex;
                    }

                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志写失败时静默忽略，不影响主程序
            }
        }
    }
}
