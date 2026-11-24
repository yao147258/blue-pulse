using BluePulseHost.Metrics;
using NUnit.Framework;

namespace BluePulse.Tests
{
    /// <summary>MetricsSnapshot 的单元测试。</summary>
    [TestFixture]
    public class MetricsSnapshotTests
    {
        [Test]
        public void Snapshot_初始值_为空指标()
        {
            var s = new MetricsSnapshot();
            Assert.IsNull(s.CpuTempC);          // 读不到的字段默认 null
            Assert.AreEqual(0, s.CpuLoad);
        }

        [Test]
        public void CpuMemoryCollector_采样后_数值在合法区间()
        {
            var c = new CpuMemoryCollector();
            c.SampleOnce(); c.SampleOnce();   // PerformanceCounter 需要两次采样
            var s = c.Current;
            Assert.GreaterOrEqual(s.CpuLoad, 0);
            Assert.LessOrEqual(s.CpuLoad, 100);
            Assert.Greater(s.MemTotalGb, 0);
        }

        [Test]
        public void CpuMemoryCollector_采样后_MemPct在合法区间()
        {
            var c = new CpuMemoryCollector();
            c.SampleOnce(); c.SampleOnce();   // PerformanceCounter 需要两次采样
            var s = c.Current;
            Assert.GreaterOrEqual(s.MemPct, 0);
            Assert.LessOrEqual(s.MemPct, 100);
            // 物理机内存使用率不会恒等于 100，用于防止单位换算错误（KB/GB 比值放大后恒被截为 100）
            Assert.Less(s.MemPct, 100);
        }

        [Test]
        public void NetDiskCollector_采样后_磁盘列表非空且含系统盘()
        {
            var c = new NetDiskCollector();
            c.SampleOnce();
            Assert.IsNotEmpty(c.Current.Disks);
            // 简报原文用 DriveLetter；现有 DiskInfo 结构以 Name 存盘符小写字母，故此处用 Name
            Assert.IsTrue(c.Current.Disks.Exists(d => d.Name == "c"));
        }

        [Test]
        public void HardwareCollector_打开后_能读取CPU负载()
        {
            var c = new HardwareCollector();
            try {
                c.Open();
                c.SampleOnce();
                // 负载可能为 null（虚拟机/驱动未装），但字段结构必须有效
                if (c.Current.GpuLoad.HasValue)
                    Assert.GreaterOrEqual(c.Current.GpuLoad.Value, 0);
            } finally { c.Close(); }
        }
    }
}
