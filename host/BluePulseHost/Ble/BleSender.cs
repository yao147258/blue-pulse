using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Timers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace BluePulseHost.Ble
{
    /// <summary>
    /// BLE 发送端（Task 6）：扫描广播名含 "BluePulse" 的设备 → 连接 → 获取 Write 特征 → 逐包写入。
    /// 断线后按指数退避（1s→2s→4s→…→30s 封顶）自动重连，写入成功即重置退避。
    /// 状态机：扫描中 / 已连接 / 适配器不可用，状态变化通过 StatusChanged 事件通知（Task 7 接托盘着色）。
    /// </summary>
    internal class BleSender : IDisposable
    {
        /// <summary>Nordic UART 风格服务 UUID（规格约定）</summary>
        public const string ServiceUuidString = "6E40FE10-B5A3-F393-E0A9-E50E24DCCA9E";

        /// <summary>Write 特征 UUID（WriteWithoutResponse）</summary>
        public const string WriteCharUuidString = "6E40FE11-B5A3-F393-E0A9-E50E24DCCA9E";

        /// <summary>设备广播名过滤关键字</summary>
        public const string DeviceNameFilter = "BluePulse";

        /// <summary>退避上限（秒）</summary>
        private const int MaxBackoffSeconds = 30;

        /// <summary>状态变化事件：参数为状态文本（"扫描中"/"已连接"/"适配器不可用"）</summary>
        public event Action<string> StatusChanged;

        /// <summary>全局锁：保护设备/特征/会话等字段与状态切换</summary>
        private readonly object _sync = new object();

        /// <summary>发送锁：保证包按顺序写入（Send 把写入挪到后台线程后仍需串行）</summary>
        private readonly object _sendLock = new object();

        /// <summary>广播扫描器</summary>
        private BluetoothLEAdvertisementWatcher _watcher;

        /// <summary>已连接的 BLE 设备</summary>
        private BluetoothLEDevice _device;

        /// <summary>保持连接用的 GATT 会话</summary>
        private GattSession _session;

        /// <summary>WriteWithoutResponse 特征</summary>
        private GattCharacteristic _writeChar;

        /// <summary>重连退避定时器（AutoReset=false，触发一次后需手动重启）</summary>
        private System.Timers.Timer _retryTimer;

        /// <summary>当前退避秒数（1 起，成功后重置）</summary>
        private int _backoffSeconds = 1;

        /// <summary>是否已 Start 且未 Stop</summary>
        private volatile bool _started;

        /// <summary>是否正处于连接流程中（防止广播重复触发/重连并发）</summary>
        private bool _connecting;

        /// <summary>当前对外状态文本</summary>
        private string _status;

        /// <summary>服务 UUID</summary>
        private static readonly Guid ServiceUuid = new Guid(ServiceUuidString);

        /// <summary>Write 特征 UUID</summary>
        private static readonly Guid WriteCharUuid = new Guid(WriteCharUuidString);

        /// <summary>
        /// 启动发送端：检查蓝牙适配器可用性后开始扫描。
        /// 适配器不可用时不抛异常，进入"适配器不可用"状态并按退避计划重试。
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_started)
                {
                    return;
                }
                _started = true;
                _backoffSeconds = 1;
            }

            AppLog.Info("BLE 发送端启动");
            ScheduleRetry(0); // 立即做第一次（扫描/连接）尝试
        }

        /// <summary>停止发送端：停扫描、断连接、取消重连计划</summary>
        public void Stop()
        {
            lock (_sync)
            {
                _started = false;
            }

            AppLog.Info("BLE 发送端停止");
            TeardownDevice();
            StopWatcher();

            lock (_sync)
            {
                if (_retryTimer != null)
                {
                    _retryTimer.Stop();
                    _retryTimer.Dispose();
                    _retryTimer = null;
                }
                SetStatusLocked(null); // 置空状态（已停止）
            }
        }

        /// <summary>
        /// 发送一组 BLE 包（PacketFramer.Frame 的返回值），逐包 WriteWithoutResponse 写入。
        /// 未连接时丢弃本次数据并记日志（指标是瞬时值，无重发意义）；
        /// 任意包写入失败即触发重连流程。
        /// 写入在后台线程执行（设备失联时单次写入可能等待数秒，不能阻塞托盘 UI 线程），
        /// 多次调用间用发送锁串行，保证包顺序。
        /// </summary>
        /// <param name="packets">待发送的包列表</param>
        public void Send(List<byte[]> packets)
        {
            if (packets == null || packets.Count == 0)
            {
                return;
            }

            GattCharacteristic writeChar;
            lock (_sync)
            {
                if (!_started || _writeChar == null)
                {
                    AppLog.Info("BLE 未连接，丢弃 " + packets.Count + " 包数据");
                    return;
                }
                writeChar = _writeChar;
            }

            Task.Run(() =>
            {
                try
                {
                    lock (_sendLock)
                    {
                        // 逐包写入：同一特征上串行等待结果，保证包顺序
                        foreach (var packet in packets)
                        {
                            var op = writeChar.WriteValueAsync(
                                packet.AsBuffer(),
                                GattWriteOption.WriteWithoutResponse);
                            var result = WaitForResultAsync(op).GetAwaiter().GetResult();

                            if (result != GattCommunicationStatus.Success)
                            {
                                AppLog.Error("BLE 写入失败：" + result, null);
                                OnWriteFailed();
                                return;
                            }
                        }
                    }

                    // 全部包写入成功即重置退避（规格要求"收到写入成功即重置"）
                    lock (_sync)
                    {
                        _backoffSeconds = 1;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("BLE 写入异常", ex);
                    OnWriteFailed();
                }
            });
        }

        /// <summary>释放资源</summary>
        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------------
        // 扫描与连接
        // ------------------------------------------------------------------

        /// <summary>
        /// 一次连接尝试：适配器检查 → 开始扫描。
        /// 由退避定时器（或 Start）触发，在后台线程执行。
        /// </summary>
        /// <param name="sender">定时器</param>
        /// <param name="e">定时器参数（未用）</param>
        private void AttemptConnect(object sender, ElapsedEventArgs e)
        {
            try
            {
                lock (_sync)
                {
                    if (!_started || _connecting)
                    {
                        return;
                    }
                    _connecting = true;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        // 1. 检查蓝牙适配器（Radio）是否可用
                        if (!await IsRadioAvailableAsync())
                        {
                            AppLog.Info("蓝牙适配器不可用");
                            SetStatus("适配器不可用");
                            RescheduleRetry();
                            return;
                        }

                        // 2. 开始扫描，广播名含 "BluePulse" 即触发连接
                        SetStatus("扫描中");
                        StartWatcher();
                        // 扫描是事件驱动的，这里结束本次尝试；收到目标广播后进入 ConnectAsync
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("BLE 连接尝试异常", ex);
                        RescheduleRetry();
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _connecting = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("BLE 连接尝试调度异常", ex);
                lock (_sync)
                {
                    _connecting = false;
                }
                RescheduleRetry();
            }
        }

        /// <summary>检查蓝牙适配器是否开启（无适配器或已关闭均视为不可用）</summary>
        private static async Task<bool> IsRadioAvailableAsync()
        {
            try
            {
                // Radio 没有 GetDefaultAsync：先取全部收音机，再按 Bluetooth 类型过滤
                var radios = await WaitForResultAsync(Radio.GetRadiosAsync());
                if (radios == null)
                {
                    return false;
                }
                foreach (var radio in radios)
                {
                    if (radio.Kind == RadioKind.Bluetooth)
                    {
                        return radio.State == RadioState.On;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("检查蓝牙适配器状态失败", ex);
                return false;
            }
        }

        /// <summary>启动广播扫描（若未启动）</summary>
        private void StartWatcher()
        {
            lock (_sync)
            {
                if (!_started || _watcher != null)
                {
                    return;
                }

                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };
                _watcher.Received += OnAdvertisementReceived;
                _watcher.Stopped += OnWatcherStopped;
                _watcher.Start();
                AppLog.Info("BLE 扫描已启动，过滤名：" + DeviceNameFilter);
            }
        }

        /// <summary>停止广播扫描</summary>
        private void StopWatcher()
        {
            BluetoothLEAdvertisementWatcher watcher;
            lock (_sync)
            {
                watcher = _watcher;
                _watcher = null;
            }

            if (watcher != null)
            {
                try
                {
                    watcher.Received -= OnAdvertisementReceived;
                    watcher.Stopped -= OnWatcherStopped;
                    watcher.Stop();
                }
                catch (Exception ex)
                {
                    AppLog.Error("停止 BLE 扫描失败", ex);
                }
            }
        }

        /// <summary>扫描器停止回调：意外停止（如适配器被关闭）时按退避重试</summary>
        private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            bool started;
            lock (_sync)
            {
                started = _started;
            }

            if (!started)
            {
                return; // 主动 Stop 引起的，忽略
            }

            if (args.Error != BluetoothError.Success)
            {
                AppLog.Error("BLE 扫描异常停止：" + args.Error, null);
                SetStatus("适配器不可用");
                StopWatcher();
                RescheduleRetry();
            }
        }

        /// <summary>收到广播：名字含 "BluePulse" 则停止扫描并发起连接</summary>
        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            string localName = args.Advertisement != null ? args.Advertisement.LocalName : null;
            if (string.IsNullOrEmpty(localName) ||
                localName.IndexOf(DeviceNameFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            ulong address = args.BluetoothAddress;
            bool shouldConnect = false;
            lock (_sync)
            {
                // 已在连接/已连接，或已停止，忽略重复广播
                if (_started && !_connecting && _device == null)
                {
                    _connecting = true;
                    shouldConnect = true;
                }
            }

            if (!shouldConnect)
            {
                return;
            }

            StopWatcher();
            AppLog.Info("发现目标设备 " + localName + "（地址：" + address + "），开始连接");

            Task.Run(async () =>
            {
                try
                {
                    await ConnectAsync(address, localName);
                }
                catch (Exception ex)
                {
                    AppLog.Error("BLE 连接失败", ex);
                    TeardownDevice();
                    RescheduleRetry();
                }
                finally
                {
                    lock (_sync)
                    {
                        _connecting = false;
                    }
                }
            });
        }

        /// <summary>
        /// 连接设备并初始化 GATT：获取服务/特征，建立保持连接会话。
        /// 失败时抛异常，由调用方负责清理与重连调度。
        /// </summary>
        /// <param name="address">设备蓝牙地址</param>
        /// <param name="localName">设备名（用于日志）</param>
        private async Task ConnectAsync(ulong address, string localName)
        {
            // 1. 连接设备（超时/失败返回 null）
            var device = await WaitForResultAsync(BluetoothLEDevice.FromBluetoothAddressAsync(address));
            if (device == null)
            {
                throw new InvalidOperationException("无法连接设备 " + localName);
            }

            // 提前登记到 _device 并订阅断连事件：后续任何一步失败（包括 GattSession
            // 创建抛异常），调用方的 TeardownDevice 都能兜底释放设备，避免泄漏
            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            lock (_sync)
            {
                _device = device;
            }

            try
            {
                // 2. 按 UUID 获取服务（失败不在此处 Dispose 设备，统一由 TeardownDevice 兜底）
                var servicesResult = await WaitForResultAsync(
                    device.GetGattServicesForUuidAsync(ServiceUuid,
                        BluetoothCacheMode.Uncached));
                if (servicesResult.Status != GattCommunicationStatus.Success ||
                    servicesResult.Services.Count == 0)
                {
                    throw new InvalidOperationException(
                        "未找到服务 " + ServiceUuidString + "（" + servicesResult.Status + "）");
                }
                var service = servicesResult.Services[0];

                // 3. 在服务下找 WriteWithoutResponse 特征
                var charsResult = await WaitForResultAsync(
                    service.GetCharacteristicsForUuidAsync(WriteCharUuid,
                        BluetoothCacheMode.Uncached));
                if (charsResult.Status != GattCommunicationStatus.Success ||
                    charsResult.Characteristics.Count == 0)
                {
                    throw new InvalidOperationException(
                        "未找到特征 " + WriteCharUuidString + "（" + charsResult.Status + "）");
                }

                var writeChar = charsResult.Characteristics[0];
                if ((writeChar.CharacteristicProperties &
                     GattCharacteristicProperties.WriteWithoutResponse) == 0)
                {
                    throw new InvalidOperationException("特征不支持 WriteWithoutResponse");
                }

                // 4. 建立 GATT 会话并保持连接
                var session = await WaitForResultAsync(
                    GattSession.FromDeviceIdAsync(device.BluetoothDeviceId));
                if (session != null)
                {
                    session.MaintainConnection = true;
                    AppLog.Info("GATT 会话已建立，MaxPduSize=" + session.MaxPduSize);
                }

                // 5. 登记：锁内复查 _started，Stop 之后到达这里的连接不得"复活"
                lock (_sync)
                {
                    if (!_started)
                    {
                        // 已 Stop：不登记任何字段，把设备/会话就地释放，避免僵尸连接。
                        // Stop() 的 TeardownDevice 可能已并发释放过，故整体 try/catch 兜底
                        _device = null; // 清掉提前登记的引用，防止 TeardownDevice 重复释放
                        try
                        {
                            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                            if (session != null)
                            {
                                session.MaintainConnection = false;
                                session.Dispose();
                            }
                            device.Dispose();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("放弃已停止的连接时释放资源失败", ex);
                        }
                        AppLog.Info("连接完成时发送端已停止，放弃该连接");
                        return;
                    }

                    _session = session;
                    _writeChar = writeChar;
                    _backoffSeconds = 1; // 连接成功重置退避
                }

                SetStatus("已连接");
                AppLog.Info("BLE 已连接：" + localName);
            }
            catch
            {
                // 设备已提前登记到 _device，交给调用方的 TeardownDevice 统一释放
                throw;
            }
        }

        /// <summary>设备断连回调：清理并按退避重连</summary>
        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            bool needReconnect = false;
            lock (_sync)
            {
                if (_started && sender != null &&
                    sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected &&
                    _device == sender)
                {
                    needReconnect = true;
                }
            }

            if (needReconnect)
            {
                AppLog.Info("BLE 连接已断开，准备重连");
                SetStatus("扫描中"); // 立即刷新状态，避免托盘滞留"已连接"
                TeardownDevice();
                RescheduleRetry();
            }
        }

        /// <summary>写入失败：清理并按退避重连</summary>
        private void OnWriteFailed()
        {
            bool needReconnect = false;
            lock (_sync)
            {
                if (_started)
                {
                    needReconnect = true;
                }
            }

            if (needReconnect)
            {
                SetStatus("扫描中"); // 立即刷新状态，避免托盘滞留"已连接"
                TeardownDevice();
                RescheduleRetry();
            }
        }

        // ------------------------------------------------------------------
        // 退避重连
        // ------------------------------------------------------------------

        /// <summary>按当前退避秒数安排下一次连接尝试，并使退避翻倍（封顶 30s）。
        /// 已有重连定时器在途时跳过（断连与写失败可能并发触发，避免退避多翻倍）。
        /// 不在此处改状态：状态由各触发方自行设置（适配器不可用须保持该状态）。</summary>
        private void RescheduleRetry()
        {
            int delaySeconds;
            lock (_sync)
            {
                if (!_started)
                {
                    return; // 已停止，不再安排重试
                }

                if (_retryTimer != null && _retryTimer.Enabled)
                {
                    return; // 已有待触发的重连，跳过本次重复调度
                }

                delaySeconds = _backoffSeconds;
                _backoffSeconds = Math.Min(_backoffSeconds * 2, MaxBackoffSeconds);
            }

            ScheduleRetry(delaySeconds * 1000);
            AppLog.Info("将在 " + delaySeconds + " 秒后重试连接");
        }

        /// <summary>启动一次性定时器，delayMs 毫秒后触发 AttemptConnect</summary>
        private void ScheduleRetry(int delayMs)
        {
            lock (_sync)
            {
                if (!_started)
                {
                    return;
                }

                // 复用定时器：先停旧的再重启
                if (_retryTimer == null)
                {
                    _retryTimer = new System.Timers.Timer
                    {
                        AutoReset = false
                    };
                    _retryTimer.Elapsed += AttemptConnect;
                }
                else
                {
                    _retryTimer.Stop();
                }

                _retryTimer.Interval = Math.Max(delayMs, 1); // Interval 不允许 0
                _retryTimer.Start();
            }
        }

        // ------------------------------------------------------------------
        // 状态与清理
        // ------------------------------------------------------------------

        /// <summary>对外发布状态（线程安全；须在 _sync 内调用 SetStatusLocked）</summary>
        private void SetStatus(string status)
        {
            Action<string> handler;
            lock (_sync)
            {
                if (_status == status)
                {
                    return;
                }
                _status = status;
                handler = StatusChanged;
            }

            if (handler != null)
            {
                try
                {
                    handler(status);
                }
                catch (Exception ex)
                {
                    AppLog.Error("StatusChanged 回调异常", ex);
                }
            }
        }

        /// <summary>在已持有 _sync 的上下文里改状态（Stop 时用）</summary>
        private void SetStatusLocked(string status)
        {
            _status = status;
            // 停止时状态置空，不对外通知（Task 7 可据此判断运行态）
        }

        /// <summary>清理设备/会话/特征引用并释放资源（保持 _writeChar 等为 null）</summary>
        private void TeardownDevice()
        {
            BluetoothLEDevice device;
            GattSession session;
            lock (_sync)
            {
                device = _device;
                session = _session;
                _device = null;
                _session = null;
                _writeChar = null;
            }

            if (device != null)
            {
                try
                {
                    device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                }
                catch
                {
                    // 忽略退订异常
                }
            }

            if (session != null)
            {
                try
                {
                    session.MaintainConnection = false;
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Error("释放 GATT 会话失败", ex);
                }
            }

            if (device != null)
            {
                try
                {
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Error("释放 BLE 设备失败", ex);
                }
            }
        }

        // ------------------------------------------------------------------
        // WinRT 异步桥接
        // ------------------------------------------------------------------

        /// <summary>
        /// 把 WinRT IAsyncOperation 桥接为 .NET Task（C# 7.3 / net48 下不依赖
        /// System.Runtime.WindowsRuntime 的 AsTask 扩展，直接用 Completed 回调实现）。
        /// </summary>
        /// <typeparam name="T">异步结果类型</typeparam>
        /// <param name="operation">WinRT 异步操作</param>
        /// <returns>对应的 Task</returns>
        private static Task<T> WaitForResultAsync<T>(IAsyncOperation<T> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException("operation");
            }

            var result = new TaskCompletionSource<T>();
            operation.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                    {
                        result.SetResult(op.GetResults());
                    }
                    else if (status == AsyncStatus.Error)
                    {
                        result.SetException(op.ErrorCode);
                    }
                    else
                    {
                        result.SetCanceled();
                    }
                }
                catch (Exception ex)
                {
                    // GetResults 可能抛出，转为 Task 异常
                    result.SetException(ex);
                }
            };
            return result.Task;
        }
    }
}
