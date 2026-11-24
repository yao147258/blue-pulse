# BluePulse 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows 托盘程序每秒通过 BLE GATT 把电脑指标（CPU/内存/GPU/网络/磁盘）推送到 ESP32-S3 触摸屏，设备端 LVGL 三页仪表盘展示。

**Architecture:** 两个独立交付物：`host/`（C# .NET Framework 4.8 托盘 exe，采集→JSON→BLE Write）与 `firmware/`（ESP-IDF 工程，NimBLE GATT Server 收包→解析→LVGL 渲染）。数据单向（电脑→设备），协议为自定义 2 字节包头 + UTF-8 JSON 分片。

**Tech Stack:** C# 7.3 / .NET Framework 4.8 / Microsoft.Windows.SDK.Contracts（WinRT BLE）/ LibreHardwareMonitor / NUnit；ESP-IDF v5.x / NimBLE / LVGL 9 / esp_lcd。

**Spec:** `docs/superpowers/specs/2026-11-24-bluepulse-design.md`（实现时与计划一起阅读，协议细节以规格 4.x 节为准）

## Global Constraints

- 电脑端目标框架固定 `net48`（C# 7.3），产出单 exe，禁止引入 >1MB 的 NuGet 依赖（LibreHardwareMonitor 与 Windows.SDK.Contracts 除外）。
- BLE 服务 UUID `6E40FE10-B5A3-F393-E0A9-E50E24DCCA9E`；Write 特征 `...FE11`（Write Without Response，电脑→设备）；Notify 特征 `...FE12`（保留不用）。设备广播名 `BluePulse`。
- 数据帧 1 秒/帧；MTU 247，单包载荷 ≤244 字节；包头 2 字节：字节0 高4位帧类型(0x01=指标帧)+低4位包序号，字节1 总包数(1~15)。
- JSON 字段规则：百分比 0~100 整数；温度整数、读不到省略字段；`used/total` GB 一位小数；`net.up/down` Kbps 整数；`net.today` GB 一位小数；盘符小写；不发 null。
- 固件端 JSON 解析必须容忍字段缺失；队列满丢旧帧；帧分片 3 秒超时丢弃。
- 所有代码注释使用中文（用户全局规范）。
- 每个 Task 结束必须 commit；测试先行（TDD），固件任务以编译+日志/真机表现为验证。

## 文件结构总览

```
E:\BluePulse\
├── host\BluePulseHost\                      # C# 托盘主程序
│   ├── BluePulseHost.csproj                 # net48 SDK 风格工程
│   ├── app.manifest                         # requireAdministrator（温度驱动需要）
│   ├── Program.cs                           # 入口：单实例、托盘、生命周期
│   ├── TrayUI.cs                            # NotifyIcon 与菜单
│   ├── Metrics\MetricsCollector.cs          # 每秒采集调度
│   ├── Metrics\CpuMemoryCollector.cs        # PerformanceCounter：CPU/内存
│   ├── Metrics\HardwareCollector.cs         # LibreHardwareMonitor：温度/频率/GPU
│   ├── Metrics\NetDiskCollector.cs          # 网络/磁盘
│   ├── Metrics\MetricsSnapshot.cs           # 指标数据模型
│   ├── Protocol\JsonFrameBuilder.cs         # 快照→JSON→分包字节
│   ├── Protocol\PacketFramer.cs             # 2 字节包头分片（纯函数，重点单测）
│   └── Ble\BleSender.cs                     # WinRT BLE：扫描/连接/写特征/重连
├── host\BluePulse.Tests\
│   ├── BluePulse.Tests.csproj
│   ├── PacketFramerTests.cs
│   ├── JsonFrameBuilderTests.cs
│   └── MetricsSnapshotTests.cs
└── firmware\
    ├── CMakeLists.txt
    ├── sdkconfig.defaults
    ├── partitions.csv
    ├── main\
    │   ├── CMakeLists.txt
    │   ├── idf_component.yml                # 依赖 lvgl / esp_lcd 等
    │   ├── main.c                           # 初始化编排
    │   ├── ble.c / ble.h                    # NimBLE GATT Server、收包
    │   ├── protocol.c / protocol.h          # 拼帧+JSON 解析（纯函数，自测覆盖）
    │   ├── metrics.h                        # 指标结构（与 host JSON 对应）
    │   ├── ui.c / ui.h                      # LVGL 三页 UI
    │   └── selftest.c                       # CONFIG_BP_SELFTEST 开机自测
    └── components\bsp_1_54\
        ├── CMakeLists.txt
        ├── bsp_1_54.h                       # 引脚宏、对外 API
        ├── bsp_display.c                    # ST7789 + 背光
        └── bsp_touch.c                      # CST816 I2C
```

---

### Task 1: Host 骨架 — 解决方案、托盘程序、单实例、日志

**Files:**
- Create: `host/BluePulseHost/BluePulseHost.csproj`, `host/BluePulseHost/Program.cs`, `host/BluePulseHost/TrayUI.cs`, `host/BluePulseHost/app.manifest`, `host/BluePulseHost/AppLog.cs`, `host/BluePulse.sln`

**Interfaces:**
- Produces: `AppLog.Info(string)`, `AppLog.Error(string, Exception)`（写 `%LOCALAPPDATA%\BluePulse\host.log`，1MB 滚动）；`TrayUI` 构造 `TrayUI(Func<string> getStatus, Action onReconnect, Action onExit)`；`Program.Main()` 单实例互斥（互斥量名 `BluePulse_SingleInstance`）。

- [ ] **Step 1: 创建工程文件**

`host/BluePulseHost/BluePulseHost.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>BluePulseHost</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.SDK.Contracts" Version="10.0.19041.1" />
    <PackageReference Include="LibreHardwareMonitorLib" Version="0.9.4" />
  </ItemGroup>
</Project>
```

`app.manifest`（`requestedExecutionLevel level="requireAdministrator"`；同时加 DPI awareness）。

- [ ] **Step 2: 写 AppLog 与 TrayUI**

`AppLog.cs`：追加式写文件，超 1MB 改名 `.old` 覆盖重建；try/catch 包裹全部 IO（日志失败不影响主程序）。

`TrayUI.cs`：`NotifyIcon`，图标按状态着色（绿/灰/红用 `System.Drawing` 画 16×16 圆点生成，不需要图标资源文件）；右键菜单：状态文本（禁用项）、立即重连、退出。悬停提示显示状态。

- [ ] **Step 3: 写 Program.cs 并运行验证**

单实例：`Mutex(true, "BluePulse_SingleInstance", out createdNew)`，非首个实例直接退出。主线程 `Application.Run()` 保活托盘。

Run: `dotnet build host/BluePulse.sln -c Release` 然后 `host/BluePulseHost/bin/Release/net48/BluePulseHost.exe`
Expected: 托盘出现灰色图标，右键菜单可退出；`%LOCALAPPDATA%\BluePulse\host.log` 有启动日志；二次启动立即退出。

- [ ] **Step 4: Commit**

```bash
git add host && git commit -m "feat(host): 托盘程序骨架——单实例、日志、状态图标"
```

---

### Task 2: CPU/内存采集（PerformanceCounter）

**Files:**
- Create: `host/BluePulseHost/Metrics/MetricsSnapshot.cs`, `host/BluePulseHost/Metrics/CpuMemoryCollector.cs`
- Test: `host/BluePulse.Tests/MetricsSnapshotTests.cs`, `host/BluePulse.Tests/BluePulse.Tests.csproj`

**Interfaces:**
- Produces: `MetricsSnapshot`（含 `CpuLoad`, `MemPct`, `MemUsedGb`, `MemTotalGb`, `CpuTempC?`, `CpuFreqMhz?`, `GpuLoad?`, `GpuTempC?`, `GpuVramGb?`, `NetUpKbps`, `NetDownKbps`, `NetTodayGb`, `List<CoreLoad> PerCore`, `List<DiskInfo> Disks`，可空字段=读不到时省略）；`CpuMemoryCollector` 实现 `void SampleOnce()` + `MetricsSnapshot Current`（线程安全快照替换）。

- [ ] **Step 1: 建测试工程并写失败测试**

`host/BluePulse.Tests/BluePulse.Tests.csproj`：SDK 风格 `net48`，`PackageReference: NUnit 3.14.0, NUnit3TestAdapter 4.5.0, Microsoft.NET.Test.Sdk 17.9.0`，引用 BluePulseHost 工程。

`MetricsSnapshotTests.cs` 首个测试：

```csharp
[Test]
public void Snapshot_初始值_为空指标()
{
    var s = new MetricsSnapshot();
    Assert.IsNull(s.CpuTempC);          // 读不到的字段默认 null
    Assert.AreEqual(0, s.CpuLoad);
}
```

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: 编译失败（MetricsSnapshot 不存在）。

- [ ] **Step 2: 实现 MetricsSnapshot 与 CpuMemoryCollector**

`MetricsSnapshot`：按上述接口建 POCO（属性 + 中文注释字段含义）。
`CpuMemoryCollector`：构造时创建 `PerformanceCounter("Processor", "% Processor Time", "_Total")`、`PerformanceCounter("Memory", "Available MBytes")`、每核一个 `"% Processor Time"` 实例（索引 0..N-1）；`SampleOnce()` 按 PerformanceCounter 惯例丢第一次读数、读第二次；内存总量用 `Microsoft.VisualBasic.Devices.ComputerInfo`（需引用 `Microsoft.VisualBasic`）或 GlobalMemoryStatusEx P/Invoke，取 GB 一位小数。

- [ ] **Step 3: 加采集合理性测试**

```csharp
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
```

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: PASS（2/2）。

- [ ] **Step 4: Commit**

```bash
git add host && git commit -m "feat(host): CPU/内存采集（PerformanceCounter）"
```

---

### Task 3: 温度/频率/GPU 采集（LibreHardwareMonitor）

**Files:**
- Create: `host/BluePulseHost/Metrics/HardwareCollector.cs`
- Modify: `host/BluePulseHost/Metrics/MetricsSnapshot.cs`（如需补充字段）

**Interfaces:**
- Consumes: `MetricsSnapshot`（Task 2）
- Produces: `HardwareCollector` 实现 `void Open()`（启动 LHM `Computer`，IsCpuEnabled=IsGpuEnabled=IsMemoryEnabled=true，`Open()`）、`void SampleOnce()`（从 `sensor.SensorType.Temperature/Clock/Load` 提取 CPU 包温、CPU 平均频率、GPU 负载/温度/显存占用，写入 `Current`）、`void Close()`。

- [ ] **Step 1: 写失败测试**

```csharp
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
```

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: FAIL（HardwareCollector 不存在）。

- [ ] **Step 2: 实现 HardwareCollector**

用 `LibreHardwareMonitor.Hardware.Computer` + `IVisitor` 遍历；CPU 温度取 `Package` 温度传感器，频率取各核 `Clock` 均值，GPU 优先取 `GpuNvidia`（负载/温度/显存用 `GpuCore/_temperature/Used` 传感器），找不到时字段保持 null。遍历中调用 `hardware.Update()`。注意 LHM 在管理员权限下才完整工作（manifest 已声明）。

- [ ] **Step 3: 运行测试**

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: PASS（3/3）。

- [ ] **Step 4: Commit**

```bash
git add host && git commit -m "feat(host): LibreHardwareMonitor 温度/频率/GPU 采集"
```

---

### Task 4: 网络/磁盘采集

**Files:**
- Create: `host/BluePulseHost/Metrics/NetDiskCollector.cs`

**Interfaces:**
- Consumes: `MetricsSnapshot`（Task 2）
- Produces: `NetDiskCollector` 实现 `void SampleOnce()`；网络速率用 `PerformanceCounter("Network Interface", "Bytes Sent/sec", <实例>)` 累加所有非 loopback 实例（Bytes→Kbps×8）；当日流量跨日重置（记录 `DateTime.Today`）；磁盘用 `DriveInfo.GetDrives()` 过滤 `IsReady && DriveType.Fixed`，`pct = (total-free)/total`；`List<DiskInfo>`（`DriveLetter` 小写、`Pct`）。

- [ ] **Step 1: 写失败测试**

```csharp
[Test]
public void NetDiskCollector_采样后_磁盘列表非空且含系统盘()
{
    var c = new NetDiskCollector();
    c.SampleOnce();
    Assert.IsNotEmpty(c.Current.Disks);
    Assert.IsTrue(c.Current.Disks.Exists(d => d.DriveLetter == "c"));
}
```

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: FAIL（类型不存在）。

- [ ] **Step 2: 实现 NetDiskCollector**

按接口描述实现。网络速率取所有 `"Network Interface"` 实例中名字不含 `Loopback`/`isatap` 的，Sent/Received 各建计数器求和。

- [ ] **Step 3: 运行测试**

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: PASS（4/4）。

- [ ] **Step 4: Commit**

```bash
git add host && git commit -m "feat(host): 网络速率与磁盘占用采集"
```

---

### Task 5: JSON 帧构建与分包（纯函数，重点单测）

**Files:**
- Create: `host/BluePulseHost/Protocol/JsonFrameBuilder.cs`, `host/BluePulseHost/Protocol/PacketFramer.cs`
- Test: `host/BluePulse.Tests/JsonFrameBuilderTests.cs`, `host/BluePulse.Tests/PacketFramerTests.cs`

**Interfaces:**
- Consumes: `MetricsSnapshot`（Task 2-4）
- Produces:
  - `JsonFrameBuilder.Build(MetricsSnapshot) → string`：按规格 4.3 字段表序列化，null 字段整个省略，`t` 为 Unix 秒。
  - `PacketFramer.Frame(byte[] jsonUtf8) → List<byte[]>`：按规格 4.2 分包——字节0=`(0x01<<4)|seq`，字节1=总包数，载荷 ≤244；>15 包时抛 `ArgumentException`。
  - `PacketFramer.Assemble(List<byte[]> packets) → byte[]`（固件侧镜像逻辑，供测试互验）。

- [ ] **Step 1: 写 PacketFramer 失败测试**

```csharp
[Test]
public void Frame_短载荷_单包()
{
    var pkts = PacketFramer.Frame(Encoding.UTF8.GetBytes("{\"a\":1}"));
    Assert.AreEqual(1, pkts.Count);
    Assert.AreEqual(0x01, pkts[0][0]);      // 类型01+序号0
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
```

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: FAIL（类型不存在）。

- [ ] **Step 2: 实现 PacketFramer**

```csharp
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
```

- [ ] **Step 3: 运行 PacketFramer 测试**

Run: `dotnet test host/BluePulse.Tests --filter PacketFramer -v n`
Expected: PASS（3/3）。

- [ ] **Step 4: 写 JsonFrameBuilder 失败测试**

```csharp
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
        MemPct = 61, MemUsedGb = 9.8, MemTotalGb = 16.0,
        NetUpKbps = 120, NetDownKbps = 850, NetTodayGb = 3.2,
        PerCore = new List<CoreLoad> { new CoreLoad { Load = 35 } }
    };
    var json = JsonFrameBuilder.Build(s);
    StringAssert.Contains("\"temp\":52", json);
    StringAssert.Contains("\"used\":9.8", json);
    StringAssert.Contains("\"percore\":[35", json);
    StringAssert.Contains("\"t\":", json);
}
```

Run: `dotnet test host/BluePulse.Tests --filter JsonFrameBuilder -v n`
Expected: FAIL（类型不存在）。

- [ ] **Step 5: 实现 JsonFrameBuilder**

手写 `StringBuilder` 序列化（不用 Newtonsoft，控制零依赖与字段省略逻辑）；GB 一位小数用 `value.ToString("0.0", CultureInfo.InvariantCulture)`。

- [ ] **Step 6: 运行全部测试**

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: PASS（8/8）。

- [ ] **Step 7: Commit**

```bash
git add host && git commit -m "feat(host): JSON 帧构建与 BLE 分包（含互验重组）"
```

---

### Task 6: BLE 发送端（扫描/连接/写特征/重连）

**Files:**
- Create: `host/BluePulseHost/Ble/BleSender.cs`

**Interfaces:**
- Consumes: `PacketFramer.Frame`（Task 5）
- Produces: `BleSender` 实现 `event Action<string> StatusChanged`（状态："扫描中"/"已连接"/"适配器不可用"）、`void Start()`、`void Stop()`、`void Send(List<byte[]> packets)`（逐包 `WriteValueAsync(GattWriteOption.WriteWithoutResponse)`）、内部指数退避重连 1s→2s→4s→…→30s 封顶，收到写入成功即重置。

- [ ] **Step 1: 实现 BleSender**

用 `BluetoothLEAdvertisementWatcher`（`LocalName` 过滤 `BluePulse`）→ `BluetoothLEDevice.FromBluetoothAddressAsync` → `GetGattServicesForUuidAsync(SERVICE_UUID)` → 取 FE11 特征（`GattCharacteristicProperties.WriteWithoutResponse`）。请求 `GattSession.MaintainConnection = true` 并把 `MaxPduSize` 记入日志。重连用 `System.Timers.Timer` 退避。**说明：此任务无法可靠单测（依赖蓝牙适配器），验证走 Step 2 手动步骤。**

- [ ] **Step 2: 手动验证（无设备时用手机模拟）**

Run: `dotnet build host/BluePulse.sln -c Release`，运行 exe，用手机 nRF Connect 建"BluePulse"外设（服务 `6E40FE10-...`+Write 特征）或观察日志扫描状态流转。
Expected: 日志出现"扫描中"→（有模拟设备时）"已连接"；托盘图标变色；无适配器时状态"适配器不可用"。

- [ ] **Step 3: Commit**

```bash
git add host && git commit -m "feat(host): BLE 扫描/连接/写特征与指数退避重连"
```

---

### Task 7: Host 集成 — 采集调度 + 托盘状态 + 端到端发包

**Files:**
- Create: `host/BluePulseHost/Metrics/MetricsCollector.cs`
- Modify: `host/BluePulseHost/Program.cs`（装配三个 collector + BleSender + TrayUI）

**Interfaces:**
- Consumes: Task 2-6 全部组件
- Produces: `MetricsCollector`：`System.Timers.Timer` 1s 周期，依次调三个 collector 的 `SampleOnce()`，`JsonFrameBuilder.Build` → `PacketFramer.Frame` → `BleSender.Send`；异常只记日志不中断。

- [ ] **Step 1: 实现 MetricsCollector 并装配**

`Program.Main` 中创建组件、接线：`BleSender.StatusChanged` → 托盘着色；三个 collector 共享一个 `MetricsSnapshot` 实例由各自 `SampleOnce` 填充（`lock` 保护）。托盘菜单"立即重连"调 `BleSender.Restart()`。

- [ ] **Step 2: 运行并验证（nRF Connect 观察）**

Run: 构建并运行 exe，nRF Connect 连接模拟设备观察写入。
Expected: nRF Connect 每秒收到 1~N 包，包头符合 `0x01xx`，拼起来是合法 JSON；断开模拟设备后日志显示退避重连。

- [ ] **Step 3: 全量测试回归**

Run: `dotnet test host/BluePulse.Tests -v n`
Expected: 全部 PASS。

- [ ] **Step 4: Commit**

```bash
git add host && git commit -m "feat(host): 采集调度与端到端 BLE 发送集成"
```

---

### Task 8: 固件工程骨架 + 板级支持（点亮屏幕）

**Files:**
- Create: `firmware/CMakeLists.txt`, `firmware/sdkconfig.defaults`, `firmware/partitions.csv`, `firmware/main/CMakeLists.txt`, `firmware/main/idf_component.yml`, `firmware/main/main.c`, `firmware/components/bsp_1_54/CMakeLists.txt`, `firmware/components/bsp_1_54/bsp_1_54.h`, `firmware/components/bsp_1_54/bsp_display.c`, `firmware/components/bsp_1_54/bsp_touch.c`

**Interfaces:**
- Produces: `bsp_1_54_init()`（按微雪 wiki 引脚初始化 SPI 屏 + I2C 触摸 + 背光 PWM，返回句柄结构 `bsp_handle_t`）；`bsp_backlight_set(uint8_t pct)`。`idf_component.yml` 依赖：`lvgl/lvgl~9`、`espressif/esp_lcd`。
- 注：引脚定义以微雪 ESP32-S3-Touch-LCD-1.54 wiki 原理图为准（执行时查 https://www.waveshare.net/wiki/ESP32-S3-Touch-LCD-1.54 ，把查到的引脚写入 `bsp_1_54.h`）。

- [ ] **Step 1: 建工程骨架**

`sdkconfig.defaults` 预置：`CONFIG_IDF_TARGET="esp32s3"`、PSRAM OCTAL 开启、`CONFIG_BT_ENABLED=y` + `CONFIG_BT_NIMBLE_ENABLED=y`、LVGL 用 PSRAM 的堆分配。`partitions.csv`：nvs 24K / phy 4K / app 3M / spiffs 可选。

- [ ] **Step 2: 实现 BSP 显示与触摸**

`bsp_display.c`：`esp_lcd_new_panel_st7789()`，SPI 时钟 80MHz，RGB565 交换字节；背光 LEDC PWM 20kHz。`bsp_touch.c`：`esp_lcd_touch_new_cst816s()`（I2C 400kHz，INT 引脚）。引脚宏集中放 `bsp_1_54.h` 并附中文注释标注来源（wiki 原理图）。

- [ ] **Step 3: 编译 + 烧录验证**

Run: `cd firmware && idf.py build && idf.py flash monitor`
Expected: 编译零警告；监视器打印 BSP 初始化成功日志；屏幕显示测试色块（main.c 临时画满屏渐变色验证方向与颜色正确）。

- [ ] **Step 4: Commit**

```bash
git add firmware && git commit -m "feat(fw): 工程骨架与 1.54 寸板级支持（屏幕点亮）"
```

---

### Task 9: LVGL 三页 UI（假数据）

**Files:**
- Create: `firmware/main/ui.c`, `firmware/main/ui.h`
- Modify: `firmware/main/main.c`（LVGL 初始化 + 假数据定时器）

**Interfaces:**
- Consumes: `bsp_1_54_init()`（Task 8）
- Produces: `ui_init(lv_disp_t*)`、`ui_update_metrics(const bp_metrics_t*)`（`metrics.h` 中的结构）、`ui_set_connected(bool)`。三页布局按规格 6.2：页1 圆环×3+温度、页2 曲线+每核条形、页3 网络/磁盘；左右滑动切页（LVGL `lv_tileview`）+ 底部指示点。

- [ ] **Step 1: 实现 metrics.h 数据结构**

```c
typedef struct { uint8_t load; } bp_core_t;
typedef struct {
    uint32_t t;            // Unix 秒
    uint8_t cpu_load; int16_t cpu_temp; bool cpu_temp_valid; uint16_t cpu_freq;
    uint8_t mem_pct; float mem_used_gb, mem_total_gb;
    uint8_t gpu_load; int16_t gpu_temp; bool gpu_temp_valid; float gpu_vram_gb;
    uint32_t net_up, net_down; float net_today_gb;
    struct { char letter; uint8_t pct; } disks[8]; uint8_t disk_count;
    bp_core_t cores[8]; uint8_t core_count;
} bp_metrics_t;
```

- [ ] **Step 2: 实现 ui.c 三页 + 动画**

`lv_tileview` 三块 tile；页1 `lv_arc`×3 + `lv_label`；页2 `lv_chart`（60 点 LINE_MODE）+ 8 个 `lv_bar`；页3 `lv_label`（速率/流量）+ `lv_bar`（磁盘）。`ui_update_metrics()` 用 `lv_anim` 从旧值插值到新值（300ms）。断连时 `ui_set_connected(false)` 顶部显示"未连接"标签。

- [ ] **Step 3: main.c 加假数据定时器验证**

`esp_timer` 1s 生成模拟指标（正弦波动）调 `ui_update_metrics()`。

Run: `idf.py build flash monitor`
Expected: 三页可滑动切换，指示点跟随，数值动画流畅（30fps 无撕裂），假数据曲线滚动。

- [ ] **Step 4: Commit**

```bash
git add firmware && git commit -m "feat(fw): LVGL 三页仪表盘 UI（假数据驱动）"
```

---

### Task 10: 固件 BLE GATT Server + 收包

**Files:**
- Create: `firmware/main/ble.c`, `firmware/main/ble.h`
- Modify: `firmware/main/main.c`（初始化 NimBLE、收包回调接线）

**Interfaces:**
- Consumes: `PacketFramer` 协议（规格 4.2）
- Produces: `ble_start(void (*on_frame)(const uint8_t *json, size_t len))`；GATT：服务 `6E40FE10-...`，FE11 Write Without Response（接收），FE12 Notify（保留）；广播名 `BluePulse`；收包→按 `(type<<4)|seq` 分片重组，3 秒超时丢帧，收齐回调 `on_frame`；连接状态变化回调 `ui_set_connected()`。

- [ ] **Step 1: 实现 ble.c**

NimBLE GAP 广播（180ms 间隔，含完整设备名）+ GATT 注册；Write 回调中按包头解析：帧类型≠0x01 丢弃；seq 与期望不符或超时（`esp_timer_get_time()` 对比）则重置重组缓冲；重组缓冲 1.5KB 静态分配。连接/断连事件切广播与 UI 状态。

- [ ] **Step 2: 编译并验证（nRF Connect 模拟主机）**

Run: `idf.py build flash monitor`，手机 nRF Connect 连接设备、向 FE11 写入 Task 5 产生的分包（可从 host 日志复制 hex）。
Expected: 监视器打印"帧收齐: N 字节"；断连后 1 秒内恢复广播。

- [ ] **Step 3: Commit**

```bash
git add firmware && git commit -m "feat(fw): NimBLE GATT Server 与分片重组"
```

---

### Task 11: 固件 JSON 解析 + 数据接入 UI

**Files:**
- Create: `firmware/main/protocol.c`, `firmware/main/protocol.h`, `firmware/main/selftest.c`
- Modify: `firmware/main/main.c`（`ble_start` 回调 → `protocol_parse` → `ui_update_metrics`）；`firmware/main/CMakeLists.txt`（加 selftest 条件编译）

**Interfaces:**
- Consumes: `ble_start`（Task 10）、`bp_metrics_t`（Task 9）
- Produces: `bool protocol_parse(const uint8_t *json, size_t len, bp_metrics_t *out)`——手写轻量解析（不引 cJSON 以省堆），容忍字段缺失（valid 位不动即保持旧值/无效）；`CONFIG_BP_SELFTEST=y` 时开机跑内置断言用例并打日志。

- [ ] **Step 1: 写 selftest.c 内置用例**

```c
// 用例：①规格 4.3 完整 JSON → 断言各字段  ②缺 temp 字段 → cpu_temp_valid==false
// ③非法 JSON → 返回 false  ④Task 5 的 3 包帧重组后解析 → 与 ① 等价
```

- [ ] **Step 2: 实现 protocol.c**

单遍扫描解析器：只识别规格 4.3 的键名与类型（整数/一位小数浮点/数组），跳过未知键；数字用 `strtol`/`strtof`。

- [ ] **Step 3: 开 selftest 编译验证**

Run: `idf.py -D SDKCONFIG_DEFAULTS=sdkconfig.defaults build flash monitor`（先临时 `CONFIG_BP_SELFTEST=y`）
Expected: 监视器打印 4 条用例全 PASS；关掉 selftest 再编译一次确认常规构建干净。

- [ ] **Step 4: 接入 UI 并联调**

main.c 接线后，host 端 exe 运行：设备三页应显示真实电脑数据，数值与任务管理器一致（±2%）。

- [ ] **Step 5: Commit**

```bash
git add firmware && git commit -m "feat(fw): JSON 解析与真实数据接入 UI"
```

---

### Task 12: 触摸交互、断连打磨、背光

**Files:**
- Modify: `firmware/main/ui.c`（滑动切页手势完善、指示点）、`firmware/components/bsp_1_54/bsp_display.c`（背光 API 已有，接状态机）
- Create: `firmware/main/power.c`（断连 10 秒降背光 30%，恢复 100%）

**Interfaces:**
- Consumes: `ble_start` 的连接状态回调（Task 10）、`bsp_backlight_set`（Task 8）
- Produces: `power_tick(bool connected)`（main.c 1s 调用）；触摸切页用 LVGL indev 驱动（Task 8 的 CST816 已注册）。

- [ ] **Step 1: 完善滑动切页与指示点**

`lv_tileview` 自带滑动；补指示点随 `lv_tileview_event` 更新。

- [ ] **Step 2: 实现背光状态机**

`power.c`：连接时 100%；断连即时 UI 变"未连接"，持续 10 秒 `bsp_backlight_set(30)`；重连恢复。

- [ ] **Step 3: 真机验证**

Run: `idf.py flash monitor` + host exe 运行/停止
Expected: 滑动切页顺滑；关掉 host exe，10 秒后背光变暗、顶部"未连接"；重启 host 后恢复。

- [ ] **Step 4: Commit**

```bash
git add firmware && git commit -m "feat(fw): 触摸切页、断连背光状态机"
```

---

### Task 13: 端到端稳定性验收

**Files:**
- Modify: `host/BluePulseHost/AppLog.cs`（加 heap 水位/帧计数日志，每 60s 一条）
- Create: `README.md`（构建说明：host `dotnet build`、firmware `idf.py`、开机自启方法、nRF Connect 联调方法）

**Interfaces:**
- Consumes: 全部
- Produces: 可交付系统。

- [ ] **Step 1: 写 README**

内容：硬件清单、两端的构建/烧录/运行步骤、协议速查（UUID/包头/JSON 字段）、常见问题（管理员权限、蓝牙适配器要求）。

- [ ] **Step 2: 全量回归**

Run: `dotnet test host/BluePulse.Tests -v n`；`idf.py build`
Expected: 测试全 PASS，固件零警告编译。

- [ ] **Step 3: 24 小时连续运行**

电脑 + 设备连续运行 24 小时：观察 `host.log` 无连续报错、设备不重启、`heap 水位` 无持续下降（host 进程内存与固件 minimum free heap）、断连重连至少人为触发 3 次均恢复。

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs: README 与稳定性验收记录"
```


