using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.ViewModels
{
    public partial class LogWindowViewModel : ObservableObject, IDisposable
    {
        private const string DefaultCopyText = "一键复制日志";
        private const string EmptyLogText = "没有日志可复制";
        private const string CopySuccessText = "复制成功!";
        private const int ResetDelayMilliseconds = 1500;

        private readonly IClipboardService _clipboardService;
        private CancellationTokenSource? _resetCts;
        private bool _isDisposed;

        /// <summary>与 StartPageViewModel 共享的日志集合（cap 200，主界面用）。</summary>
        public ObservableCollection<string> Logs { get; }

        [ObservableProperty]
        private string _copyButtonText = DefaultCopyText;

        public LogWindowViewModel(IClipboardService clipboardService, ObservableCollection<string> logs)
        {
            _clipboardService = clipboardService;
            Logs = logs;
        }

        /// <summary>
        /// 复制反馈：由窗口在复制完成后调用。copiedLines ≤ 0 显示"没有日志可复制"。
        /// </summary>
        public async void ShowCopyFeedback(int copiedLines)
        {
            if (_isDisposed) return;

            _resetCts?.Cancel();
            _resetCts?.Dispose();
            _resetCts = new CancellationTokenSource();
            CancellationToken token = _resetCts.Token;

            CopyButtonText = copiedLines > 0 ? CopySuccessText : EmptyLogText;

            try
            {
                await Task.Delay(ResetDelayMilliseconds, token);
                CopyButtonText = DefaultCopyText;
            }
            catch (OperationCanceledException) { }
        }

        [RelayCommand]
        private void Clear()
        {
            if (_isDisposed) return;
            Logs.Clear();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _resetCts?.Cancel();
            _resetCts?.Dispose();
            _resetCts = null;
        }
    }
}
