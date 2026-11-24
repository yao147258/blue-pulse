using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using BluePulseHost.Ble;
using BluePulseHost.Metrics;

namespace BluePulseHost
{
    /// <summary>
    /// 程序入口：单实例互斥 + 托盘保活 + 组件装配（Task 7）。
    /// 装配关系：三个采集器 → MetricsCollector（1 秒调度）→ JsonFrameBuilder/PacketFramer → BleSender；
    /// BleSender.StatusChanged（后台线程触发）经 UI 线程调度后驱动托盘着色。
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

                // 捕获 UI 线程的 SynchronizationContext：BleSender.StatusChanged 在后台线程触发，
                // 托盘（NotifyIcon）跨线程访问会失效，所有着色调用必须 Post 回 UI 线程。
                // WinForms 环境下应使用 WindowsFormsSynchronizationContext（依赖 Application.Run 消息循环）。
                var uiContext = SynchronizationContext.Current as WindowsFormsSynchronizationContext;
                if (uiContext == null)
                {
                    uiContext = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(uiContext);
                }

                // 最近一次状态文本（悬停提示用，仅 UI 线程读写）
                string lastStatus = "BluePulse：启动中…";

                TrayUI tray = null;
                MetricsCollector collector = null;
                HardwareCollector hardware = null;
                BleSender bleSender = null;
                try
                {
                    // 1. 创建采集器与 BLE 发送端
                    var cpuMem = new CpuMemoryCollector();
                    hardware = new HardwareCollector();
                    var netDisk = new NetDiskCollector();
                    bleSender = new BleSender();

                    // 2. 状态变化 → 托盘着色（Post 回 UI 线程）：
                    //    绿 = 已连接，红 = 适配器不可用，其余（扫描中等）= 灰
                    bleSender.StatusChanged += status =>
                    {
                        lastStatus = "BluePulse：" + status;
                        Color color;
                        if (status == "已连接")
                        {
                            color = Color.LimeGreen;
                        }
                        else if (status == "适配器不可用")
                        {
                            color = Color.Red;
                        }
                        else
                        {
                            color = Color.Gray;
                        }

                        var trayRef = tray;
                        var colorLocal = color;
                        uiContext.Post(_ =>
                        {
                            try
                            {
                                if (trayRef != null)
                                {
                                    trayRef.SetStatus(lastStatus, colorLocal);
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLog.Error("托盘状态更新失败", ex);
                            }
                        }, null);
                    };

                    // 3. 托盘 UI：状态文本取最近状态；"立即重连"重启 BLE 发送端（Stop + Start）
                    tray = new TrayUI(
                        getStatus: () => lastStatus,
                        onReconnect: () =>
                        {
                            AppLog.Info("用户点击：立即重连，重启 BLE 发送端");
                            try
                            {
                                bleSender.Stop();
                                bleSender.Start();
                            }
                            catch (Exception ex)
                            {
                                AppLog.Error("手动重连失败", ex);
                            }
                        },
                        onExit: () => Application.Exit());

                    tray.SetStatus(lastStatus, Color.Gray);
                    AppLog.Info("托盘图标已初始化");

                    // 4. 打开硬件采集器（LHM 内核驱动加载），启动采集调度与 BLE 发送
                    hardware.Open();
                    collector = new MetricsCollector(cpuMem, hardware, netDisk, bleSender);
                    collector.Start();
                    bleSender.Start();

                    // 主线程消息循环保活托盘
                    Application.Run();
                }
                catch (Exception ex)
                {
                    AppLog.Error("主循环异常退出", ex);
                }
                finally
                {
                    // 逆序释放：调度器 → BLE → 硬件驱动 → 托盘
                    if (collector != null)
                    {
                        try
                        {
                            collector.Dispose();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("释放采集调度器失败", ex);
                        }
                    }
                    if (bleSender != null)
                    {
                        try
                        {
                            bleSender.Dispose();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("释放 BLE 发送端失败", ex);
                        }
                    }
                    if (hardware != null)
                    {
                        try
                        {
                            hardware.Close();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("关闭硬件采集器失败", ex);
                        }
                    }
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
