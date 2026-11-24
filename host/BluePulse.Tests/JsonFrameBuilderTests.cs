using System.Collections.Generic;
using BluePulseHost.Metrics;
using BluePulseHost.Protocol;
using NUnit.Framework;

namespace BluePulse.Tests
{
    /// <summary>JsonFrameBuilder 的 JSON 帧序列化单元测试。</summary>
    [TestFixture]
    public class JsonFrameBuilderTests
    {
        [Test]
        public void Build_空字段_不输出null()
        {
            var s = new MetricsSnapshot { CpuLoad = 37, MemPct = 61 };
            var json = JsonFrameBuilder.Build(s);
            StringAssert.Contains("\"cpu\":{\"load\":37", json);
            StringAssert.DoesNotContain("null", json);
            StringAssert.DoesNotContain("temp", json);   // 温度未采集则整个键省略
        }

        [Test]
        public void Build_完整字段_数值格式正确()
        {
            var s = new MetricsSnapshot {
                CpuLoad = 37, CpuTempC = 52, CpuFreqMhz = 3600,
                MemPct = 61, MemUsedGb = 9.8f, MemTotalGb = 16.0f,   // C# 7.3 下 double 字面量不可隐式转 float，故加 f 后缀（简报提示"字面量要能用"）
                NetUpKbps = 120, NetDownKbps = 850, NetTodayGb = 3.2f,
                PerCore = new List<CoreLoad> { new CoreLoad { Load = 35 } }
            };
            var json = JsonFrameBuilder.Build(s);
            StringAssert.Contains("\"temp\":52", json);
            StringAssert.Contains("\"used\":9.8", json);
            StringAssert.Contains("\"percore\":[35", json);
            StringAssert.Contains("\"t\":", json);
        }
    }
}
