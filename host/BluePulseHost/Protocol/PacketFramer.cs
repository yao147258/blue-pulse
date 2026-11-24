using System;
using System.Collections.Generic;

namespace BluePulseHost.Protocol
{
    /// <summary>
    /// BLE 帧分包器（规格 4.2）。
    /// 每包 2 字节头 + 载荷：字节0 = (类型 0x01 << 4) | 序号，字节1 = 总包数，载荷 ≤ 244 字节。
    /// </summary>
    public static class PacketFramer
    {
        /// <summary>固件侧镜像逻辑：把分包重新拼回完整 JSON 字节（忽略包头）。</summary>
        public static byte[] Assemble(List<byte[]> packets)
        {
            // 按包头低 4 位的序号排序，容忍乱序到达
            var sorted = new List<byte[]>(packets);
            sorted.Sort((a, b) => (a[0] & 0x0F).CompareTo(b[0] & 0x0F));
            int totalLen = 0;
            foreach (var p in sorted) totalLen += p.Length - 2;
            var result = new byte[totalLen];
            int offset = 0;
            foreach (var p in sorted)
            {
                Array.Copy(p, 2, result, offset, p.Length - 2);
                offset += p.Length - 2;
            }
            return result;
        }

        /// <summary>把 UTF-8 JSON 字节流切分为 BLE 包；超过 15 包时抛 ArgumentException。</summary>
        public static List<byte[]> Frame(byte[] jsonUtf8)
        {
            const int MaxPayload = 244;   // MTU247 - 3字节ATT头
            int total = (jsonUtf8.Length + MaxPayload - 1) / MaxPayload;
            if (total > 15) throw new ArgumentException("帧超过15包上限");
            var list = new List<byte[]>(total);
            for (int seq = 0; seq < total; seq++) {
                int len = Math.Min(MaxPayload, jsonUtf8.Length - seq * MaxPayload);
                var pkt = new byte[2 + len];
                pkt[0] = (byte)((0x01 << 4) | seq);
                pkt[1] = (byte)total;
                Array.Copy(jsonUtf8, seq * MaxPayload, pkt, 2, len);
                list.Add(pkt);
            }
            return list;
        }
    }
}
