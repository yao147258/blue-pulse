using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BluePulseHost
{
    /// <summary>
    /// 托盘 UI：NotifyIcon + 右键菜单（状态文本、立即重连、退出）。
    /// 图标不需要资源文件，用 System.Drawing 动态画一个 16x16 圆点，
    /// 颜色随状态变化：绿色 = 已连接，灰色 = 未连接，红色 = 错误。
    /// </summary>
    internal class TrayUI : IDisposable
    {
        /// <summary>
        /// 销毁图标句柄（GetHicon 返回的 HICON 不归 Icon 对象管，必须手动销毁，否则句柄泄漏）
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>状态文本提供者（每次显示菜单/提示时调用）</summary>
        private readonly Func<string> _getStatus;

        /// <summary>"立即重连"回调</summary>
        private readonly Action _onReconnect;

        /// <summary>"退出"回调</summary>
        private readonly Action _onExit;

        /// <summary>托盘图标</summary>
        private readonly NotifyIcon _notifyIcon;

        /// <summary>右键菜单</summary>
        private readonly ContextMenuStrip _menu;

        /// <summary>菜单中的状态文本项（禁用，仅用于展示）</summary>
        private readonly ToolStripMenuItem _statusItem;

        /// <summary>当前图标颜色</summary>
        private Color _currentColor = Color.Gray;

        /// <summary>当前图标原始 HICON（由 GetHicon 产生，需用 DestroyIcon 销毁）</summary>
        private IntPtr _currentHIcon = IntPtr.Zero;

        /// <summary>
        /// 构造托盘 UI
        /// </summary>
        /// <param name="getStatus">获取状态文本的委托</param>
        /// <param name="onReconnect">点击"立即重连"时执行的回调</param>
        /// <param name="onExit">点击"退出"时执行的回调</param>
        public TrayUI(Func<string> getStatus, Action onReconnect, Action onExit)
        {
            _getStatus = getStatus;
            _onReconnect = onReconnect;
            _onExit = onExit;

            // 右键菜单：状态文本（禁用项）+ 立即重连 + 退出
            _menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem("初始化…")
            {
                Enabled = false // 状态文本仅展示，不可点击
            };
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());

            var reconnectItem = new ToolStripMenuItem("立即重连");
            reconnectItem.Click += OnReconnectClick;
            _menu.Items.Add(reconnectItem);

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += OnExitClick;
            _menu.Items.Add(exitItem);

            // 托盘图标：动态画一个灰色圆点起步（记录 HICON 以便销毁）
            _currentHIcon = MakeIcon(_currentColor);
            _notifyIcon = new NotifyIcon
            {
                Icon = Icon.FromHandle(_currentHIcon),
                Text = "BluePulse",
                ContextMenuStrip = _menu,
                Visible = true
            };

            // 悬停提示显示最新状态
            _notifyIcon.MouseMove += OnIconMouseMove;
        }

        /// <summary>
        /// 设置托盘状态：同时更新图标颜色与悬停提示
        /// </summary>
        /// <param name="text">状态文本</param>
        /// <param name="color">状态颜色（绿/灰/红）</param>
        public void SetStatus(string text, Color color)
        {
            if (_notifyIcon == null)
            {
                return;
            }

            if (color != _currentColor)
            {
                _currentColor = color;
                // 替换图标：先记录旧 HICON，换上新图标后再销毁旧句柄。
                // Icon.FromHandle 的包装器不拥有句柄，其 Dispose 不会销毁 HICON，
                // 因此必须在这里 P/Invoke DestroyIcon，否则每次切换都泄漏一个句柄。
                IntPtr oldHIcon = _currentHIcon;
                _currentHIcon = MakeIcon(color);
                _notifyIcon.Icon = Icon.FromHandle(_currentHIcon);
                DestroyOldIcon(oldHIcon);
            }

            // 托盘提示最多 63 个字符，超长截断
            _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;

            // 同步更新右键菜单里的状态文本项，保证菜单与图标/提示一致
            _statusItem.Text = text;
        }

        /// <summary>安全销毁旧的 HICON（忽略销毁失败）</summary>
        /// <param name="hIcon">要销毁的图标句柄</param>
        private static void DestroyOldIcon(IntPtr hIcon)
        {
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }
        }

        /// <summary>更新菜单里的状态文本项</summary>
        /// <param name="text">状态文本</param>
        public void SetStatusText(string text)
        {
            _statusItem.Text = text;
        }

        /// <summary>
        /// 用 System.Drawing 动态生成 16x16 圆点图标（不需要图标资源文件）
        /// </summary>
        /// <param name="color">圆点颜色</param>
        /// <returns>原始 HICON（GetHicon 产生，调用方负责用 DestroyIcon 销毁）</returns>
        private static IntPtr MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            {
                // 平滑抗锯齿，让圆点边缘干净
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color))
                {
                    // 留 1px 边距画 14x14 的实心圆
                    g.FillEllipse(brush, 1, 1, 14, 14);
                }
                // GetHicon 返回的 HICON 归调用方管，记录句柄以便销毁
                return bmp.GetHicon();
            }
        }

        /// <summary>悬停时刷新提示文本</summary>
        private void OnIconMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                string status = _getStatus();
                _notifyIcon.Text = status.Length > 63 ? status.Substring(0, 63) : status;
            }
            catch
            {
                // 状态获取失败不影响托盘
            }
        }

        /// <summary>"立即重连"菜单点击</summary>
        private void OnReconnectClick(object sender, EventArgs e)
        {
            var handler = _onReconnect;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>"退出"菜单点击</summary>
        private void OnExitClick(object sender, EventArgs e)
        {
            var handler = _onExit;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>释放托盘资源</summary>
        public void Dispose()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.MouseMove -= OnIconMouseMove;
                _notifyIcon.Icon = null;
                _notifyIcon.Dispose();
            }
            // 销毁最后一个图标的 HICON（Icon.FromHandle 的包装器不拥有它）
            DestroyOldIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
            if (_menu != null)
            {
                _menu.Dispose();
            }
        }
    }
}
