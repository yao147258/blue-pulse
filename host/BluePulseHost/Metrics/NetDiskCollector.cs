using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace BluePulseHost.Metrics
{
    /// <summary>
    /// 网络/磁盘采集器：
    /// - 网络速率：对所有非 loopback 物理网卡的 "Bytes Sent/sec"/"Bytes Received/sec" 计数器求和，换算为 Kbps；
    /// - 当日流量：累计 Sent/Received 字节，跨日（DateTime.Today 变化）自动重置，以 GB（一位小数）呈现；
    /// - 磁盘占用：DriveInfo.GetDrives() 过滤就绪的本地固定磁盘，产出 DiskInfo 列表。
    /// SampleOnce() 每次生成一份新快照，Current 返回最近一次快照（线程安全的引用替换）。
    /// </summary>
    public class NetDiskCollector
    {
        /// <summary>上行速率计数器（每个网卡实例一个）。</summary>
        private readonly List<PerformanceCounter> _sentCounters = new List<PerformanceCounter>();

        /// <summary>下行速率计数器（每个网卡实例一个）。</summary>
        private readonly List<PerformanceCounter> _recvCounters = new List<PerformanceCounter>();

        /// <summary>当日累计发送字节（含今日开始前无法追溯的部分，从程序启动起算）。</summary>
        private long _todaySentBytes;

        /// <summary>当日累计接收字节。</summary>
        private long _todayRecvBytes;

        /// <summary>当日流量归属的日期（DateTime.Today），跨日时重置累计值。</summary>
        private DateTime _today = DateTime.Today;

        /// <summary>最近一次快照，volatile 保证多线程读取时可见最新引用。</summary>
        private volatile MetricsSnapshot _current = new MetricsSnapshot();

        /// <summary>最近一次采集的快照（线程安全：引用替换）。</summary>
        public MetricsSnapshot Current
        {
            get { return _current; }
        }

        /// <summary>构造时枚举网卡实例并为每个非 loopback 实例建一对速率计数器。</summary>
        public NetDiskCollector()
        {
            // PerformanceCounter("Network Interface") 的实例名即网卡名（如 "Realtek PCIe GbE Family Controller"）
            var category = new PerformanceCounterCategory("Network Interface");
            foreach (var name in category.GetInstanceNames())
            {
                // 过滤环回与 isatap 隧道伪接口
                if (name.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("isatap", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", name);
                var recv = new PerformanceCounter("Network Interface", "Bytes Received/sec", name);
                sent.NextValue(); // 丢弃第一次读数（区间基线）
                recv.NextValue();
                _sentCounters.Add(sent);
                _recvCounters.Add(recv);
            }
        }

        /// <summary>
        /// 采样一次：读取各网卡速率计数器并累加当日流量，枚举本地磁盘，生成新快照并整体替换 Current。
        /// 注意：速率类计数器需要两次采样才有有效值，调用方应至少连续调用两次。
        /// </summary>
        public void SampleOnce()
        {
            var s = new MetricsSnapshot();

            // 跨日检测：日期变化时重置当日流量累计
            if (DateTime.Today != _today)
            {
                _today = DateTime.Today;
                _todaySentBytes = 0;
                _todayRecvBytes = 0;
            }

            // 网络速率：多网卡求和；Kbps = Bytes/s × 8 / 1000
            float upBps = 0f, downBps = 0f;
            for (int i = 0; i < _sentCounters.Count; i++)
            {
                upBps += _sentCounters[i].NextValue();
                downBps += _recvCounters[i].NextValue();
            }
            s.NetUpKbps = upBps * 8f / 1000f;
            s.NetDownKbps = downBps * 8f / 1000f;

            // 当日流量：按本次采样间隔内经过的速率近似累计字节（采样间隔 = 平均速率反推）
            // 说明：PerformanceCounter 只能给平均速率，无法拿到精确字节数，
            // 这里用"速率 × 采样间隔"近似累计；首次采样无间隔基准，跳过。
            if (_lastSampleTime.HasValue)
            {
                double seconds = (DateTime.UtcNow - _lastSampleTime.Value).TotalSeconds;
                if (seconds > 0 && seconds < 3600) // 间隔过长（如系统休眠唤醒）时放弃本次近似，避免累计失真
                {
                    _todaySentBytes += (long)(upBps * seconds);
                    _todayRecvBytes += (long)(downBps * seconds);
                }
            }
            _lastSampleTime = DateTime.UtcNow;
            s.NetTodayGb = (float)Math.Round((_todaySentBytes + _todayRecvBytes) / 1073741824.0, 1);

            // 磁盘：只取就绪的本地固定磁盘（排除 U 盘/光驱/网络驱动器）
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

                    float totalGb = (float)Math.Round(drive.TotalSize / 1073741824.0, 1);
                    float usedGb = (float)Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / 1073741824.0, 1);
                    // DiskInfo.Name 存盘符小写字母（如 "c"），Pct 由 UsedGb/TotalGb 推算，不另加属性
                    s.Disks.Add(new DiskInfo
                    {
                        Name = drive.Name.Substring(0, 1).ToLowerInvariant(),
                        UsedGb = usedGb,
                        TotalGb = totalGb,
                    });
                }
                catch
                {
                    // 个别驱动器读数失败（权限/被移除）时跳过该盘
                }
            }

            // 原子替换快照引用（赋值给 volatile 字段）
            _current = s;
        }

        /// <summary>上次采样时刻（UTC），用于按采样间隔近似累计当日流量。</summary>
        private DateTime? _lastSampleTime;
    }
}
