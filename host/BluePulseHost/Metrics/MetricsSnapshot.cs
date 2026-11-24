using System.Collections.Generic;

namespace BluePulseHost.Metrics
{
    /// <summary>
    /// 一次采集得到的指标快照（每次采样新建实例的 POCO）。
    /// 可空字段表示当前读不到时省略（值为 null），后续任务填充。
    /// </summary>
    public class MetricsSnapshot
    {
        /// <summary>CPU 总体负载（0-100，百分比）。</summary>
        public float CpuLoad { get; set; }

        /// <summary>内存使用率（0-100，百分比）。</summary>
        public float MemPct { get; set; }

        /// <summary>已用内存（GB，一位小数）。</summary>
        public float MemUsedGb { get; set; }

        /// <summary>内存总量（GB，一位小数）。</summary>
        public float MemTotalGb { get; set; }

        /// <summary>CPU 温度（摄氏度），读不到时为 null。</summary>
        public float? CpuTempC { get; set; }

        /// <summary>CPU 频率（MHz），读不到时为 null。</summary>
        public float? CpuFreqMhz { get; set; }

        /// <summary>GPU 负载（0-100，百分比），读不到时为 null。</summary>
        public float? GpuLoad { get; set; }

        /// <summary>GPU 温度（摄氏度），读不到时为 null。</summary>
        public float? GpuTempC { get; set; }

        /// <summary>GPU 显存总量（GB），读不到时为 null。</summary>
        public float? GpuVramGb { get; set; }

        /// <summary>上行网速（Kbps）。</summary>
        public float NetUpKbps { get; set; }

        /// <summary>下行网速（Kbps）。</summary>
        public float NetDownKbps { get; set; }

        /// <summary>今日已用流量（GB）。</summary>
        public float NetTodayGb { get; set; }

        /// <summary>每核 CPU 负载列表（索引 0..N-1 对应逻辑核心）。</summary>
        public List<CoreLoad> PerCore { get; set; } = new List<CoreLoad>();

        /// <summary>磁盘信息列表。</summary>
        public List<DiskInfo> Disks { get; set; } = new List<DiskInfo>();
    }

    /// <summary>单个逻辑核心的 CPU 负载。</summary>
    public class CoreLoad
    {
        /// <summary>核心索引（0..N-1）。</summary>
        public int Index { get; set; }

        /// <summary>该核心负载（0-100，百分比）。</summary>
        public float Load { get; set; }
    }

    /// <summary>单个磁盘的占用信息。</summary>
    public class DiskInfo
    {
        /// <summary>盘符（如 "C:"）。</summary>
        public string Name { get; set; }

        /// <summary>已用空间（GB，一位小数）。</summary>
        public float UsedGb { get; set; }

        /// <summary>总空间（GB，一位小数）。</summary>
        public float TotalGb { get; set; }
    }
}
