using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace ClassIslandInjector;

/// <summary>
/// 流光跑马灯专用全屏覆盖窗口：覆盖整块屏幕（含任务栏区域），置顶、透明、
/// 不抢焦点、点击穿透。绕过宿主特效窗口（TopmostEffectWindow）默认只用工作区
/// （不含任务栏）渲染的限制，让流光能铺到屏幕真正的底边。
/// </summary>
internal sealed class MarqueeOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Panel _host = new();

    /// <summary>流光覆盖层的宿主面板（Children 为 IList，供注入器统一移除）。</summary>
    public Panel Host => _host;

    public MarqueeOverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Content = _host;
        // 窗口真正显示后（句柄就绪）再确保点击穿透等扩展样式生效，
        // 否则 Show 时句柄可能尚未创建，导致全屏窗口挡住鼠标操作。
        Opened += (_, _) => ApplyClickThrough();
    }

    /// <summary>
    /// 定位到整块屏幕（含任务栏区域）并置顶显示在任务栏之上；
    /// 同时设置点击穿透、不抢焦点、不显示在任务栏/Alt-Tab。
    /// </summary>
    public void ShowFullScreen(Screen? screen)
    {
        if (screen != null)
        {
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            Width = screen.Bounds.Width / scaling;
            Height = screen.Bounds.Height / scaling;
            Position = new PixelPoint(screen.Bounds.X, screen.Bounds.Y);
        }

        if (!IsVisible)
        {
            Show();
        }

        ApplyTopmost();
        ApplyClickThrough();
    }

    /// <summary>压过任务栏（置顶带顶部）。</summary>
    private void ApplyTopmost()
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is { } hwnd && hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary>设置点击穿透 + 不抢焦点 + 工具窗扩展样式；改样式后需带 SWP_FRAMECHANGED 再 SetWindowPos 才生效。</summary>
    private void ApplyClickThrough()
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is { } hwnd && hwnd != IntPtr.Zero)
        {
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, GwlExStyle,
                (IntPtr)(exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate));
            NativeMethods.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
        }
    }

    /// <summary>宿主面板没有剩余覆盖层时隐藏窗口。</summary>
    public void HideWhenEmpty()
    {
        if (_host.Children.Count == 0 && IsVisible)
        {
            Hide();
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
