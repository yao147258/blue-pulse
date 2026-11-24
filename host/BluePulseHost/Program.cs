using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace BluePulseHost
{
    /// <summary>
    /// 程序入口：单实例互斥 + 托盘保活。
    /// 后续任务会把 BLE 推送、指标采集挂到这里。
    /// </summary>
    internal static class Program
    {
        /// <summary>单实例互斥量名</summary>
        private const string MutexName = "BluePulse_SingleInstance";

        /// <summary>程序入口</summary>
        [STAThread]
        private static void Main()
        {
            // 单实例：非首个实例直接退出
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在运行，立即退出
                    return;
                }

                AppLog.Info("BluePulseHost 启动");

                TrayUI tray = null;
                try
                {
                    // 托盘 UI：状态文本、重连、退出回调
                    // 当前阶段getStatus 返回占位状态，后续任务接入真实指标/BLE
                    tray = new TrayUI(
                        getStatus: () => "BluePulse：未连接（骨架）",
                        onReconnect: () => AppLog.Info("用户点击：立即重连"),
                        onExit: () => Application.Exit());

                    tray.SetStatus("BluePulse：未连接（骨架）", Color.Gray);
                    AppLog.Info("托盘图标已初始化");

                    // 主线程消息循环保活托盘
                    Application.Run();
                }
                catch (Exception ex)
                {
                    AppLog.Error("主循环异常退出", ex);
                }
                finally
                {
                    if (tray != null)
                    {
                        tray.Dispose();
                    }
                    AppLog.Info("BluePulseHost 退出");
                }

                // 释放互斥量
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // 忽略释放异常
                }
            }
        }
    }
}
