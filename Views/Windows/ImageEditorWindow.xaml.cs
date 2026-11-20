using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrustNTE.ViewModels;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using WPoint = System.Windows.Point;

namespace TrustNTE.Views
{
    public partial class ImageEditorWindow : Wpf.Ui.Controls.FluentWindow
    {
        private ScreenshotEditorViewModel? Vm => DataContext as ScreenshotEditorViewModel;

        private WPoint? _selStart;   // 框选起点(图像像素坐标)
        private WPoint? _panLast;    // 移动工具平移起点

        public ImageEditorWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => HookVm();
            Closed += (_, _) => UnhookVm();
        }

        // ============ VM 事件挂钩(选区重绘) ============
        private ScreenshotEditorViewModel? _hooked;

        private void HookVm()
        {
            var vm = Vm;
            if (vm == null || _hooked == vm) return;
            _hooked = vm;
            vm.SelectionVisualChanged += RedrawSel;
            vm.PropertyChanged += Vm_PropertyChanged;
            RedrawSel();
        }

        private void UnhookVm()
        {
            var vm = _hooked;
            _hooked = null;
            if (vm == null) return;
            vm.SelectionVisualChanged -= RedrawSel;
            vm.PropertyChanged -= Vm_PropertyChanged;
        }

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScreenshotEditorViewModel.Scale)) RedrawSel();
        }

        // ============ 选区常驻绘制(虚线框/圆 + 反选遮罩) ============
        private void RedrawSel()
        {
            var vm = _hooked ?? Vm;
            if (vm == null) return;
            HideInv();

            if (vm.SelRectPx is not { } r || vm.EditorImage == null)
            {
                if (_selStart == null)
                    SelRect.Visibility = SelEllipse.Visibility = Visibility.Collapsed;
                return;
            }

            double s = vm.Scale;
            double ix = vm.EditorImage.PixelWidth * s, iy = vm.EditorImage.PixelHeight * s;
            double x = r.X * s, y = r.Y * s, w = r.Width * s, h = r.Height * s;

            if (vm.SelInverted)
            {
                ShowInv(InvTop, 0, 0, ix, Math.Max(0, y));
                ShowInv(InvBottom, 0, y + h, ix, Math.Max(0, iy - (y + h)));
                ShowInv(InvLeft, 0, y, Math.Max(0, x), h);
                ShowInv(InvRight, x + w, y, Math.Max(0, ix - (x + w)), h);
            }

            if (vm.SelIsCircle)
            {
                SelRect.Visibility = Visibility.Collapsed;
                SelEllipse.Visibility = Visibility.Visible;
                SelEllipse.SetValue(Canvas.LeftProperty, x);
                SelEllipse.SetValue(Canvas.TopProperty, y);
                SelEllipse.Width = w; SelEllipse.Height = h;
            }
            else
            {
                SelEllipse.Visibility = Visibility.Collapsed;
                SelRect.Visibility = Visibility.Visible;
                SelRect.SetValue(Canvas.LeftProperty, x);
                SelRect.SetValue(Canvas.TopProperty, y);
                SelRect.Width = w; SelRect.Height = h;
            }
        }

        // 修复:参数写全名,避免被全局 using 认成 System.Drawing.Rectangle
        private void ShowInv(System.Windows.Shapes.Rectangle el, double x, double y, double w, double h)
        {
            if (w <= 0.5 || h <= 0.5) { el.Visibility = Visibility.Collapsed; return; }
            el.SetValue(Canvas.LeftProperty, x);
            el.SetValue(Canvas.TopProperty, y);
            el.Width = w; el.Height = h;
            el.Visibility = Visibility.Visible;
        }

        private void HideInv() =>
            InvTop.Visibility = InvBottom.Visibility = InvLeft.Visibility = InvRight.Visibility = Visibility.Collapsed;

        // ============ 坐标换算 ============
        private (int px, int py, bool ok) Hit(MouseEventArgs e)
        {
            var vm = Vm;
            if (vm?.EditorImage == null || vm.Scale <= 0) return (0, 0, false);
            var pos = e.GetPosition(EditorImageCtl);
            int px = (int)(pos.X / vm.Scale);
            int py = (int)(pos.Y / vm.Scale);
            return (px, py, px >= 0 && py >= 0 && px < vm.EditorImage.PixelWidth && py < vm.EditorImage.PixelHeight);
        }

        private bool IsCircleTool => Vm?.IsCircleTool == true;
        private bool IsMoveTool => Vm?.IsMoveTool == true;
        private bool IsPickTool => Vm?.IsPickTool == true;
        private bool IsFillTool => Vm?.IsFillTool == true;

        // ============ 滚轮缩放 ============
        private void EditorScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Vm == null || Vm.EditorImage == null) return;
            e.Handled = true;
            if (e.Delta > 0) Vm.ZoomInCommand.Execute(null);
            else Vm.ZoomOutCommand.Execute(null);
        }

        // ============ 鼠标按下 ============
        private void EditorGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var vm = Vm;
            if (vm == null || e.ClickCount != 1) return;

            if (IsMoveTool)
            {
                _panLast = e.GetPosition(EditorScroll);
                EditorEventGrid.CaptureMouse();
                return;
            }

            var (px, py, ok) = Hit(e);
            if (!ok) return;
            if (IsPickTool) { vm.PickAt(px, py); return; }
            if (IsFillTool) { vm.FillAt(px, py); return; }

            // 矩形/圆形:开始拖框
            _selStart = new WPoint(px, py);
            EditorEventGrid.CaptureMouse();
        }

        // ============ 鼠标移动 ============
        private void EditorGrid_MouseMove(object sender, MouseEventArgs e)
        {
            var vm = Vm;
            if (vm == null) return;

            // 移动工具:平移
            if (_panLast != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var cur = e.GetPosition(EditorScroll);
                EditorScroll.ScrollToHorizontalOffset(EditorScroll.HorizontalOffset - (cur.X - _panLast.Value.X));
                EditorScroll.ScrollToVerticalOffset(EditorScroll.VerticalOffset - (cur.Y - _panLast.Value.Y));
                _panLast = cur;
                return;
            }

            var (px, py, ok) = Hit(e);
            if (ok) vm.OnImageMouseMove(px, py); // 放大镜读数

            // 矩形/圆形拖框预览
            if (_selStart != null && e.LeftButton == MouseButtonState.Pressed && ok)
            {
                double x = Math.Min(_selStart.Value.X, px), y = Math.Min(_selStart.Value.Y, py);
                double w = Math.Abs(px - _selStart.Value.X), h = Math.Abs(py - _selStart.Value.Y);
                var el = IsCircleTool ? (FrameworkElement)SelEllipse : SelRect;
                var other = IsCircleTool ? (FrameworkElement)SelRect : SelEllipse;
                other.Visibility = Visibility.Collapsed;
                el.Visibility = Visibility.Visible;
                el.SetValue(Canvas.LeftProperty, x * vm.Scale);
                el.SetValue(Canvas.TopProperty, y * vm.Scale);
                el.Width = w * vm.Scale; el.Height = h * vm.Scale;
            }
        }

        // ============ 鼠标松开 ============
        private void EditorGrid_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var vm = Vm;
            if (vm == null) return;
            EditorEventGrid.ReleaseMouseCapture();

            if (_panLast != null) { _panLast = null; return; }

            if (_selStart != null)
            {
                var start = _selStart.Value;   // 先存起点
                _selStart = null;              // 再置空
                var (px, py, ok) = Hit(e);
                if (ok)
                {
                    int x = (int)Math.Min(start.X, px), y = (int)Math.Min(start.Y, py);
                    int w = (int)Math.Abs(px - start.X), h = (int)Math.Abs(py - start.Y);
                    if (w > 1 && h > 1) vm.CompleteRect(x, y, w, h, IsCircleTool);
                    else RedrawSel();          // 太小 → 恢复常驻选区显示
                }
                else RedrawSel();
            }
        }

        private void EditorGrid_MouseLeave(object sender, MouseEventArgs e) { }

        // ============ 快捷键 ============
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.T && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Vm != null)
            {
                Vm.AutoCropCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && Vm != null)
            {
                Vm.ClearSelCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
