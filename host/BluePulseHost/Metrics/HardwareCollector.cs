using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace BluePulseHost.Metrics
{
    /// <summary>
    /// 硬件采集器：基于 LibreHardwareMonitor 采集 CPU 温度/频率与 GPU 负载/温度/显存。
    /// 读不到的字段保持 null（无管理员权限或虚拟机下温度传感器可能缺失）。
    /// SampleOnce() 每次生成一份新快照，Current 返回最近一次快照（线程安全的引用替换）。
    /// </summary>
    public class HardwareCollector : IVisitor
    {
        /// <summary>LHM 计算机对象（管理 CPU/GPU/内存等硬件）。</summary>
        private readonly Computer _computer = new Computer();

        /// <summary>最近一次快照，volatile 保证多线程读取时可见最新引用。</summary>
        private volatile MetricsSnapshot _current = new MetricsSnapshot();

        /// <summary>当前正在填充的采样快照（仅 SampleOnce 线程使用）。</summary>
        private MetricsSnapshot _sample;

        /// <summary>最近一次采集的快照（线程安全：引用替换）。</summary>
        public MetricsSnapshot Current
        {
            get { return _current; }
        }

        /// <summary>启动 LHM：启用 CPU/GPU/内存硬件并打开。</summary>
        public void Open()
        {
            _computer.IsCpuEnabled = true;
            _computer.IsGpuEnabled = true;
            _computer.IsMemoryEnabled = true;
            _computer.Open();
        }

        /// <summary>
        /// 采样一次：遍历所有硬件（Update 后读取传感器），填充可空字段并整体替换 Current。
        /// 注意：温度传感器通常需要管理员权限才能读到，读不到时保持 null。
        /// </summary>
        public void SampleOnce()
        {
            _sample = new MetricsSnapshot();
            _computer.Accept(this); // 触发 VisitComputer -> Traverse，逐硬件 Update 并提取传感器
            _current = _sample;
            _sample = null;
        }

        /// <summary>释放 LHM 资源（卸载内核驱动等）。</summary>
        public void Close()
        {
            _computer.Close();
        }

        /// <summary>从单个硬件的传感器中提取指标（hardware 须已 Update）。</summary>
        private void ExtractSensors(IHardware hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    ExtractCpu(hardware);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    // 优先 Nvidia；若已从 Nvidia 取到值则不被其他 GPU 覆盖
                    ExtractGpu(hardware);
                    break;
            }
        }

        /// <summary>提取 CPU 包温度与各核频率均值。</summary>
        private void ExtractCpu(IHardware cpu)
        {
            // CPU 包温度：优先 Package 命名（Intel "CPU Package"/AMD "Core (Tcase/Tdie)"）
            var temp = cpu.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                (s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 s.Name.IndexOf("Tcase/Tdie", StringComparison.OrdinalIgnoreCase) >= 0));
            if (temp != null)
            {
                _sample.CpuTempC = temp.Value;
            }

            // CPU 频率：各核 Clock 传感器（"Core #1"/"CPU Core" 等）取均值
            var clocks = cpu.Sensors.Where(s =>
                s.SensorType == SensorType.Clock && s.Value.HasValue &&
                (s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) ||
                 s.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase))).ToList();
            if (clocks.Count > 0)
            {
                _sample.CpuFreqMhz = (float)Math.Round(clocks.Average(s => s.Value.Value), 1);
            }
        }

        /// <summary>提取 GPU 负载/温度/显存占用（同一字段已有值时不覆盖，保证 Nvidia 优先）。</summary>
        private void ExtractGpu(IHardware gpu)
        {
            // GPU 负载："GPU Core" 负载传感器
            if (!_sample.GpuLoad.HasValue)
            {
                var load = gpu.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.Load && s.Value.HasValue &&
                    s.Name.IndexOf("GPU Core", StringComparison.OrdinalIgnoreCase) >= 0);
                if (load != null) _sample.GpuLoad = load.Value;
            }

            // GPU 温度："GPU Core" 温度传感器
            if (!_sample.GpuTempC.HasValue)
            {
                var t = gpu.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                    s.Name.IndexOf("GPU Core", StringComparison.OrdinalIgnoreCase) >= 0);
                if (t != null) _sample.GpuTempC = t.Value;
            }

            // 显存占用："GPU Memory Used"（SmallData，单位 MB）换算为 GB
            if (!_sample.GpuVramGb.HasValue)
            {
                var mem = gpu.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.SmallData && s.Value.HasValue &&
                    s.Name.IndexOf("GPU Memory Used", StringComparison.OrdinalIgnoreCase) >= 0);
                if (mem != null) _sample.GpuVramGb = (float)Math.Round(mem.Value.Value / 1024f, 1);
            }
        }

        #region IVisitor 实现

        /// <summary>遍历整台计算机的所有硬件。</summary>
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        /// <summary>访问单个硬件：先 Update 刷新传感器读数，再提取；继续遍历子硬件（如主板 EC）。</summary>
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            ExtractSensors(hardware);
            foreach (IHardware sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        /// <summary>传感器在 VisitHardware 中批量提取，此处无需处理。</summary>
        public void VisitSensor(ISensor sensor)
        {
        }

        /// <summary>参数不参与采集。</summary>
        public void VisitParameter(IParameter parameter)
        {
        }

        #endregion
    }
}
