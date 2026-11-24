using System;
using System.Text;
using System.Threading;
using BluePulseHost.Ble;
using BluePulseHost.Protocol;

namespace BluePulseHost.Metrics
{
    /// <summary>
    /// 采集调度器（Task 7）：System.Timers.Timer 1 秒周期，依次调用三个采集器的
    /// SampleOnce()，把各自 Current 快照聚合为一份发送快照，再经
    /// JsonFrameBuilder.Build → PacketFramer.Frame → BleSender.Send 推送出去。
    /// 任何一步异常只记日志、跳过本次采集，绝不中断定时循环（网卡热插拔等场景下不崩进程）。
    /// 说明：三个采集器各自持有独立的 Current 快照（构造函数不可注入共享快照），
    /// 因此这里采用"聚合"方式——逐个读取属性合并到发送快照，不改动已有接口。
    /// （internal：仅在 Host 程序集内使用，与 internal 的 BleSender 保持一致的可访问性）
    /// </summary>
    internal class MetricsCollector : IDisposable
    {
        /// <summary>采集周期（毫秒）</summary>
        private const int IntervalMs = 1000;

        /// <summary>CPU/内存采集器</summary>
        private readonly CpuMemoryCollector _cpuMem;

        /// <summary>硬件（温度/频率/GPU）采集器</summary>
        private readonly HardwareCollector _hardware;

        /// <summary>网络/磁盘采集器</summary>
        private readonly NetDiskCollector _netDisk;

        /// <summary>BLE 发送端</summary>
        private readonly BleSender _sender;

        /// <summary>1 秒周期定时器（未设 SynchronizingObject，Elapsed 在线程池线程触发）</summary>
        private readonly System.Timers.Timer _timer;

        /// <summary>防重入标志：上一拍未完成时跳过本拍（0=空闲，1=采集中）</summary>
        private int _busy;

        /// <summary>是否已 Start（Stop 后 Elapsed 不再执行）</summary>
        private volatile bool _started;

        /// <summary>
        /// 构造采集调度器：传入三个采集器与 BLE 发送端（均由装配方创建并管理生命周期）。
        /// </summary>
        public MetricsCollector(CpuMemoryCollector cpuMem, HardwareCollector hardware,
            NetDiskCollector netDisk, BleSender sender)
        {
            if (cpuMem == null) throw new ArgumentNullException("cpuMem");
            if (hardware == null) throw new ArgumentNullException("hardware");
            if (netDisk == null) throw new ArgumentNullException("netDisk");
            if (sender == null) throw new ArgumentNullException("sender");

            _cpuMem = cpuMem;
            _hardware = hardware;
            _netDisk = netDisk;
            _sender = sender;

            _timer = new System.Timers.Timer(IntervalMs)
            {
                AutoReset = true
            };
            _timer.Elapsed += OnTick;
        }

        /// <summary>启动 1 秒周期采集循环</summary>
        public void Start()
        {
            _started = true;
            _timer.Start();
            AppLog.Info("采集调度器已启动（周期 " + IntervalMs + "ms）");
        }

        /// <summary>停止采集循环（不再触发 Elapsed）</summary>
        public void Stop()
        {
            _started = false;
            _timer.Stop();
            AppLog.Info("采集调度器已停止");
        }

        /// <summary>释放定时器资源</summary>
        public void Dispose()
        {
            Stop();
            _timer.Dispose();
        }

        /// <summary>
        /// 定时器拍：逐个采样（各自 try/catch，失败只记日志跳过该项），
        /// 聚合快照并构建帧发送。整体再兜一层 try/catch，保证定时循环永不中断。
        /// </summary>
        private void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            // 防重入：某拍耗时超过 1 秒（如磁盘枚举卡顿）时跳过本拍，避免堆积
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return;
            }

            try
            {
                if (!_started)
                {
                    return;
                }

                // 1. 三个采集器各自采样：任一失败只记日志、跳过本次，不影响其他采集器
                try
                {
                    _cpuMem.SampleOnce();
                }
                catch (Exception ex)
                {
                    AppLog.Error("CPU/内存采样失败，跳过本次", ex);
                }

                try
                {
                    _hardware.SampleOnce();
                }
                catch (Exception ex)
                {
                    AppLog.Error("硬件（温度/GPU）采样失败，跳过本次", ex);
                }

                try
                {
                    _netDisk.SampleOnce();
                }
                catch (Exception ex)
                {
                    AppLog.Error("网络/磁盘采样失败，跳过本次", ex);
                }

                // 2. 聚合三份 Current 快照为发送快照，构建 JSON 帧并分包发送
                try
                {
                    MetricsSnapshot snapshot = Aggregate();
                    string json = JsonFrameBuilder.Build(snapshot);
                    var packets = PacketFramer.Frame(Encoding.UTF8.GetBytes(json));
                    _sender.Send(packets);
                }
                catch (Exception ex)
                {
                    AppLog.Error("指标帧构建/发送失败，跳过本次", ex);
                }
            }
            catch (Exception ex)
            {
                // 兜底：保证定时循环本身绝不被任何异常打断
                AppLog.Error("采集拍异常", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>
        /// 聚合三个采集器的 Current 快照：逐个读取属性合并到一份发送快照。
        /// PerCore/Disks 列表直接引用（各 SampleOnce 每次新建列表，无共享可变状态）。
        /// </summary>
        private MetricsSnapshot Aggregate()
        {
            var cpu = _cpuMem.Current;
            var hw = _hardware.Current;
            var net = _netDisk.Current;

            return new MetricsSnapshot
            {
                // CPU/内存（CpuMemoryCollector）
                CpuLoad = cpu.CpuLoad,
                MemPct = cpu.MemPct,
                MemUsedGb = cpu.MemUsedGb,
                MemTotalGb = cpu.MemTotalGb,
                PerCore = cpu.PerCore,

                // 硬件（HardwareCollector）：可空字段读不到时保持 null
                CpuTempC = hw.CpuTempC,
                CpuFreqMhz = hw.CpuFreqMhz,
                GpuLoad = hw.GpuLoad,
                GpuTempC = hw.GpuTempC,
                GpuVramGb = hw.GpuVramGb,

                // 网络/磁盘（NetDiskCollector）
                NetUpKbps = net.NetUpKbps,
                NetDownKbps = net.NetDownKbps,
                NetTodayGb = net.NetTodayGb,
                Disks = net.Disks,
            };
        }
    }
}
