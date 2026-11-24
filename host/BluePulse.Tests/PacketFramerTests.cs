using System;
using System.Collections.Generic;
using System.Text;
using BluePulseHost.Protocol;
using NUnit.Framework;

namespace BluePulse.Tests
{
    /// <summary>PacketFramer 的 BLE 分包单元测试。</summary>
    [TestFixture]
    public class PacketFramerTests
    {
        [Test]
        public void Frame_短载荷_单包()
        {
            var pkts = PacketFramer.Frame(Encoding.UTF8.GetBytes("{\"a\":1}"));
            Assert.AreEqual(1, pkts.Count);
            Assert.AreEqual(0x10, pkts[0][0]);      // 类型01<<4 + 序号0（规格4.2：字节0=(0x01<<4)|seq，简报原文 0x01 与其接口定义及测试2矛盾，以规格为准）
            Assert.AreEqual(1, pkts[0][1]);         // 总包数1
        }

        [Test]
        public void Frame_超长载荷_多包且可重组()
        {
            var payload = Encoding.UTF8.GetBytes(new string('x', 600));  // 3包
            var pkts = PacketFramer.Frame(payload);
            Assert.AreEqual(3, pkts.Count);
            Assert.AreEqual((0x01 << 4) | 2, pkts[2][0]);
            CollectionAssert.AreEqual(payload, PacketFramer.Assemble(pkts));
        }

        [Test]
        public void Frame_超过15包_抛异常()
        {
            Assert.Throws<ArgumentException>(
                () => PacketFramer.Frame(new byte[15 * 244 + 1]));
        }
    }
}
