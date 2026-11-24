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
    }
}
