using System;
using System.Globalization;
using System.Text;
using BluePulseHost.Metrics;

namespace BluePulseHost.Protocol
{
    /// <summary>
    /// JSON 帧构建器（规格 4.3 字段表）。
    /// 手写 StringBuilder 序列化：零依赖，且便于按"null 字段整个省略"逐字段控制输出。
    /// </summary>
    public static class JsonFrameBuilder
    {
        /// <summary>把指标快照序列化为 JSON 字符串；可空字段为 null 时整个键省略。</summary>
        public static string Build(MetricsSnapshot s)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            sb.Append(",\"cpu\":{");
            sb.Append("\"load\":").Append(Num(s.CpuLoad));
            if (s.CpuTempC.HasValue) sb.Append(",\"temp\":").Append(Temp(s.CpuTempC.Value));
            if (s.CpuFreqMhz.HasValue) sb.Append(",\"freq\":").Append(Num(s.CpuFreqMhz.Value));
            sb.Append(",\"percore\":[");
            for (int i = 0; i < s.PerCore.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Num(s.PerCore[i].Load));
            }
            sb.Append(']');
            sb.Append('}');
            sb.Append(",\"mem\":{");
            sb.Append("\"pct\":").Append(Num(s.MemPct));
            sb.Append(",\"used\":").Append(Gb(s.MemUsedGb));
            sb.Append(",\"total\":").Append(Gb(s.MemTotalGb));
            sb.Append('}');
            if (s.GpuLoad.HasValue || s.GpuTempC.HasValue || s.GpuVramGb.HasValue)
            {
                sb.Append(",\"gpu\":{");
                bool first = true;
                if (s.GpuLoad.HasValue)
                {
                    sb.Append("\"load\":").Append(Num(s.GpuLoad.Value));
                    first = false;
                }
                if (s.GpuTempC.HasValue)
                {
                    if (!first) sb.Append(',');
                    sb.Append("\"temp\":").Append(Temp(s.GpuTempC.Value));
                    first = false;
                }
                if (s.GpuVramGb.HasValue)
                {
                    if (!first) sb.Append(',');
                    sb.Append("\"vram\":").Append(Gb(s.GpuVramGb.Value));
                }
                sb.Append('}');
            }
            sb.Append(",\"net\":{");
            sb.Append("\"up\":").Append(Num(s.NetUpKbps));
            sb.Append(",\"down\":").Append(Num(s.NetDownKbps));
            sb.Append(",\"today\":").Append(Gb(s.NetTodayGb));
            sb.Append('}');
            if (s.Disks != null && s.Disks.Count > 0)
            {
                sb.Append(",\"disk\":{");
                for (int i = 0; i < s.Disks.Count; i++)
                {
                    var d = s.Disks[i];
                    if (i > 0) sb.Append(',');
                    // 盘符在 DiskInfo.Name（小写字母），pct = 已用/总量 ×100 四舍五入取整
                    int pct = (int)Math.Round(d.UsedGb / d.TotalGb * 100f);
                    sb.Append('\"').Append(d.Name).Append("\":{\"pct\":").Append(pct).Append('}');
                }
                sb.Append('}');
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>百分比/整数型数值：四舍五入取整输出。</summary>
        private static string Num(float v)
        {
            return Math.Round(v).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>温度：整数输出。</summary>
        private static string Temp(float v)
        {
            return Math.Round(v).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>GB 容量：一位小数，InvariantCulture（避免本地化小数点）。</summary>
        private static string Gb(float v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
