using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrustNTE.ViewModels;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using WPoint = System.Windows.Point;

namespace TrustNTE.Views
{
    public partial class DevToolsWindow : Wpf.Ui.Controls.FluentWindow
    {
        private DevToolsWindowViewModel? Vm => DataContext as DevToolsWindowViewModel;
        private WPoint? _selStart;   // 框选起点(相对 FrameImageCtl)

        public DevToolsWindow()
        {
            InitializeComponent();
            this.Activated += (_, _) => { Vm?.OnWindowActivated(); FitImage(); };
            this.Closed += (_, _) => Vm?.OnWindowClosed();
            this.Loaded += (_, _) => FitImage();
            this.DataContextChanged += (_, _) => HookFrameImage();
        }

        // ============ 让 Image/Canvas 精确等于画面显示尺寸(黑边在控件外) ============
        private void HookFrameImage()
        {
            var vm = Vm;
            if (vm == null) return;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DevToolsWindowViewModel.FrameImage))
                    Dispatcher.BeginInvoke(new Action(FitImage),
                        System.Windows.Threading.DispatcherPriority.Loaded);
            };
            Dispatcher.BeginInvoke(new Action(FitImage),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void FrameHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitImage();

        private void FitImage()
        {
            var bs = Vm?.FrameImage;
            if (bs == null || FrameHost.ActualWidth < 1 || FrameHost.ActualHeight < 1) return;
            double scale = Math.Min(FrameHost.ActualWidth / bs.PixelWidth, FrameHost.ActualHeight / bs.PixelHeight);
            double w = Math.Floor(bs.PixelWidth * scale);
            double h = Math.Floor(bs.PixelHeight * scale);
            FrameImageCtl.Width = w; FrameImageCtl.Height = h;   // 控件=画面,Stretch=Fill 无变形
            CrossCanvas.Width = w; CrossCanvas.Height = h;     // 覆盖层与画面逐像素对齐
        }

        // ============ 坐标换算:全部相对 FrameImageCtl,一步除法,无黑边概念 ============
        private double ImgScale()
        {
            var bs = Vm?.FrameImage;
            if (bs == null || FrameImageCtl.ActualWidth < 1 || bs.PixelWidth < 1) return 0;
            return FrameImageCtl.ActualWidth / bs.PixelWidth;
        }

        private (int px, int py, bool ok) ToImage(WPoint p)
        {
            var bs = Vm?.FrameImage;
            double s = ImgScale();
            if (bs == null || s <= 0) return (0, 0, false);
            int x = (int)(p.X / s), y = (int)(p.Y / s);
            return (x, y, x >= 0 && y >= 0 && x < bs.PixelWidth && y < bs.PixelHeight);
        }

        // ============ 鼠标(按下冻结快照 → 松手裁快照,所见即所得) ============
        private void FrameHost_Click(object sender, MouseButtonEventArgs e)
        {
            if (Vm == null) return;
            var (px, py, ok) = ToImage(e.GetPosition(FrameImageCtl));
            if (!ok) return;

            if (Vm.IsColorPickerTab) { Vm.PickAt(px, py); return; }

            _selStart = e.GetPosition(FrameImageCtl);
            Vm.BeginRegionCapture();
            FrameHost.CaptureMouse();
        }

        private void FrameHost_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(FrameImageCtl);
            var (px, py, ok) = ToImage(p);
            if (ok) Vm?.OnFrameMouseMove(px, py);

            CrossV.Visibility = CrossH.Visibility = Visibility.Visible;
            CrossV.X1 = p.X; CrossV.X2 = p.X; CrossV.Y1 = 0; CrossV.Y2 = CrossCanvas.ActualHeight;
            CrossH.Y1 = p.Y; CrossH.Y2 = p.Y; CrossH.X1 = 0; CrossH.X2 = CrossCanvas.ActualWidth;

            if (_selStart != null)
            {
                SelRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(SelRect, Math.Min(_selStart.Value.X, p.X));
                Canvas.SetTop(SelRect, Math.Min(_selStart.Value.Y, p.Y));
                SelRect.Width = Math.Abs(p.X - _selStart.Value.X);
                SelRect.Height = Math.Abs(p.Y - _selStart.Value.Y);
            }
        }

        private void FrameHost_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (Vm == null || _selStart == null) return;
            FrameHost.ReleaseMouseCapture();
            var start = _selStart.Value;
            _selStart = null;
            SelRect.Visibility = Visibility.Collapsed;

            var (x1, y1, ok1) = ToImage(start);
            var (x2, y2, ok2) = ToImage(e.GetPosition(FrameImageCtl));
            if (!ok1 || !ok2) { Vm.CancelRegionCapture(); return; }

            int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
            int w = Math.Abs(x2 - x1), h = Math.Abs(y2 - y1);
            if (w < 8 || h < 8) { Vm.CancelRegionCapture(); return; }
            Vm.CaptureRegion(x, y, w, h);
        }

        private void FrameHost_MouseLeave(object sender, MouseEventArgs e)
        {
            CrossV.Visibility = CrossH.Visibility = Visibility.Collapsed;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && Vm is { IsFrozen: true })
            {
                Vm.ToggleFreezeCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
