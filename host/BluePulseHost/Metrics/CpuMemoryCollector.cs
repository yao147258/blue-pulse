using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BluePulseHost.Metrics
{
    /// <summary>
    /// CPU/内存采集器：基于 PerformanceCounter 采集 CPU 总负载、每核负载与内存使用情况。
    /// SampleOnce() 每次生成一份新快照，Current 返回最近一次快照（线程安全的引用替换）。
    /// </summary>
    public class CpuMemoryCollector
    {
        /// <summary>CPU 总负载计数器。</summary>
        private readonly PerformanceCounter _cpuTotal;

        /// <summary>可用内存计数器（MB）。</summary>
        private readonly PerformanceCounter _memAvailable;

        /// <summary>每核负载计数器（索引 0..N-1 对应逻辑核心）。</summary>
        private readonly List<PerformanceCounter> _perCoreCounters = new List<PerformanceCounter>();

        /// <summary>最近一次快照，volatile 保证多线程读取时可见最新引用。</summary>
        private volatile MetricsSnapshot _current = new MetricsSnapshot();

        /// <summary>构造时创建各性能计数器。</summary>
        public CpuMemoryCollector()
        {
            // CPU 总负载（"_Total" 实例）
            _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuTotal.NextValue(); // PerformanceCounter 惯例：第一次读数丢弃（区间基线）

            // 可用内存（MB）
            _memAvailable = new PerformanceCounter("Memory", "Available MBytes");
            _memAvailable.NextValue(); // 丢弃第一次读数

            // 每逻辑核心一个计数器实例（索引 0..N-1）
            int cores = Environment.ProcessorCount;
            for (int i = 0; i < cores; i++)
            {
                var c = new PerformanceCounter("Processor", "% Processor Time", i.ToString());
                c.NextValue(); // 丢弃第一次读数
                _perCoreCounters.Add(c);
            }
        }

        /// <summary>最近一次采集的快照（线程安全：引用替换）。</summary>
        public MetricsSnapshot Current
        {
            get { return _current; }
        }

        /// <summary>
        /// 采样一次：读取各计数器并生成新快照，整体替换 Current。
        /// 注意：CPU 百分比类计数器需要两次采样才有有效值，调用方应至少连续调用两次。
        /// </summary>
        public void SampleOnce()
        {
            var s = new MetricsSnapshot();

            // CPU 总负载：0-100
            s.CpuLoad = Clamp(_cpuTotal.NextValue());

            // 每核负载
            for (int i = 0; i < _perCoreCounters.Count; i++)
            {
                s.PerCore.Add(new CoreLoad { Index = i, Load = Clamp(_perCoreCounters[i].NextValue()) });
            }

            // 内存总量：用 GlobalMemoryStatusEx P/Invoke 取物理内存总量（GB，一位小数）
            ulong totalKb, availKb;
            if (TryGetGlobalMemory(out totalKb, out availKb))
            {
                s.MemTotalGb = (float)Math.Round(totalKb / 1024.0 / 1024.0, 1);
                // 已用内存优先用可用内存计数器推算，保证与 PerformanceCounter 口径一致
                float availMb = _memAvailable.NextValue();
                if (availMb > 0)
                {
                    float usedKb = (float)totalKb - availMb * 1024f;
                    s.MemUsedGb = (float)Math.Round(Math.Max(usedKb, 0) / 1024.0 / 1024.0, 1);
                    s.MemPct = Clamp(s.MemTotalGb > 0 ? usedKb / (float)(totalKb / 1024.0 / 1024.0) * 100f : 0);
                }
                else
                {
                    // 计数器读数无效时退回 GlobalMemoryStatusEx 的可用内存
                    float usedKb = (float)(totalKb - availKb);
                    s.MemUsedGb = (float)Math.Round(usedKb / 1024.0 / 1024.0, 1);
                    s.MemPct = Clamp(totalKb > 0 ? (float)usedKb / totalKb * 100f : 0);
                }
            }

            // 可空字段（CpuTempC 等）本任务不填充，后续任务用

            // 原子替换快照引用（赋值给 volatile 字段）
            _current = s;
        }

        /// <summary>把数值限制在 0-100 区间，并过滤非法值。</summary>
        private static float Clamp(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f) return 0f;
            if (v > 100f) return 100f;
            return v;
        }

        #region GlobalMemoryStatusEx P/Invoke

        /// <summary>MEMORYSTATUSEX 结构体（GlobalMemoryStatusEx 用）。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            /// <summary>结构体大小（须先赋值）。</summary>
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

            /// <summary>内存占用率（千分比）。</summary>
            public uint dwMemoryLoad;

            /// <summary>物理内存总量（字节）。</summary>
            public ulong ullTotalPhys;

            /// <summary>可用物理内存（字节）。</summary>
            public ulong ullAvailPhys;

            /// <summary>页面文件总量（字节）。</summary>
            public ulong ullTotalPageFile;

            /// <summary>可用页面文件（字节）。</summary>
            public ulong ullAvailPageFile;

            /// <summary>虚拟内存总量（字节）。</summary>
            public ulong ullTotalVirtual;

            /// <summary>可用虚拟内存（字节）。</summary>
            public ulong ullAvailVirtual;

            /// <summary>可用扩展虚拟内存（字节）。</summary>
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>GlobalMemoryStatusEx 导入。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        /// <summary>读取全局内存状态，返回（物理总量 KB, 可用物理 KB）。</summary>
        private static bool TryGetGlobalMemory(out ulong totalKb, out ulong availKb)
        {
            totalKb = 0;
            availKb = 0;
            try
            {
                var st = new MEMORYSTATUSEX();
                if (!GlobalMemoryStatusEx(st)) return false;
                totalKb = st.ullTotalPhys / 1024UL;
                availKb = st.ullAvailPhys / 1024UL;
                return totalKb > 0;
            }
            catch
            {
                // P/Invoke 失败时返回 false，调用方跳过内存填充
                return false;
            }
        }

        #endregion
    }
}
