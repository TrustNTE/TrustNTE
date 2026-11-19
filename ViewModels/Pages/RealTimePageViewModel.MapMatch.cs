using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.ViewModels;

namespace TrustNTE.ViewModels.Pages
{
    public partial class RealTimePageViewModel
    {
        // ========= 地图匹配模块属性 =========
        [ObservableProperty] private bool _isMapMatchingEnabled;
        [ObservableProperty] private string _mapMatchStatus = "等待启动";
        [ObservableProperty] private string _mapMatchCoordinates = "X: -, Y: -";

        private CancellationTokenSource? _mapMatchCts;
        private int _isMatching = 0;
        private MapMatchDebugWindowViewModel? _mapMatchDebugVm;

        partial void OnIsMapMatchingEnabledChanged(bool value)
        {
            if (value)
            {
                if (_automationService.CurrentHwnd == IntPtr.Zero)
                {
                    MapMatchStatus = "失败: 请先开启截图器";
                    IsMapMatchingEnabled = false;
                    return;
                }

                MapMatchStatus = "正在匹配...";
                _mapMatchCts = new CancellationTokenSource();
                _ = MapMatchLoopAsync(_mapMatchCts.Token);
            }
            else
            {
                _mapMatchCts?.Cancel();
                _mapMatchCts?.Dispose();
                _mapMatchCts = null;
                MapMatchStatus = "已停止";
                MapMatchCoordinates = "X: -, Y: -";
                _mapMatchDebugVm?.ClearMarker();
            }
        }

        [RelayCommand]
        private void OpenMapMatchDebug()
        {
            if (_mapMatchDebugVm == null)
            {
                _mapMatchDebugVm = new MapMatchDebugWindowViewModel();
            }
            _windowService.ShowMapMatchDebugWindow(_mapMatchDebugVm);
        }

        private async Task MapMatchLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _isMatching, 1) == 0)
                {
                    try
                    {
                        using var frame = _automationService.GetLatestFrame();
                        if (frame != null && !frame.Empty())
                        {
                            var result = _mapMatchService.MatchMiniMapToBigMap(frame, 157, 136, 90);

                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                if (result.HasValue)
                                {
                                    MapMatchStatus = "匹配成功";
                                    MapMatchCoordinates = $"X: {result.Value.X}, Y: {result.Value.Y}";
                                    _mapMatchDebugVm?.UpdateMarker(result.Value.X, result.Value.Y);
                                }
                                else
                                {
                                    MapMatchStatus = "特征不足/匹配失败";
                                    _mapMatchDebugVm?.ClearMarker();
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"地图匹配异常: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isMatching, 0);
                    }
                }

                try
                {
                    await Task.Delay(300, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}