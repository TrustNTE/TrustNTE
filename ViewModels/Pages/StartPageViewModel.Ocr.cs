using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using TrustNTE.Helpers; // KeyCode 定义在此
using TrustNTE.Views.Windows;

namespace TrustNTE.ViewModels.Pages
{
    /// <summary>
    /// <see cref="StartPageViewModel"/> 的 OCR 测试 partial 文件。
    /// </summary>
    public partial class StartPageViewModel
    {
        private static readonly string OcrModelDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "NTE-Ocr_Models");

        private static readonly Rect UidOcrRoi = new Rect(3, 1051, 193, 27);

        /// <summary>
        /// 驾驶提示 ROI（1139, 547, 133x80）。【临时测试用，测完删除】
        /// </summary>
        private static readonly Rect DriveHintRoi = new Rect(1139, 547, 133, 80);

        [ObservableProperty]
        private string _recModelPath = string.Empty;

        private void InitializeOcr()
        {
            string rec = Path.Combine(OcrModelDir, "rec.onnx");
            RecModelPath = File.Exists(rec) ? rec : string.Empty;
        }

        [RelayCommand]
        private async Task TestUidOcrAsync()
        {
            if (_automationService.CurrentHwnd == IntPtr.Zero)
            {
                await _dialogService.ShowInfoAsync("无法测试", "请先前往【启动】页面开启截图器并绑定游戏窗口！");
                return;
            }

            using (var checkFrame = _automationService.GetLatestFrame())
            {
                if (checkFrame == null || checkFrame.Empty())
                {
                    await _dialogService.ShowInfoAsync("无法测试", "未能获取到游戏画面，请确认截图器正在运行。");
                    return;
                }
                if (UidOcrRoi.Right > checkFrame.Width || UidOcrRoi.Bottom > checkFrame.Height)
                {
                    await _dialogService.ShowInfoAsync(
                        "无法测试",
                        $"当前画面尺寸 {checkFrame.Width}x{checkFrame.Height} 小于 UID 区域（3,1051，宽高 193x27），请确认游戏分辨率为 1920x1080。");
                    return;
                }
            }

            Mat? CaptureUidRegion()
            {
                using var frame = _automationService.GetLatestFrame();
                if (frame == null || frame.Empty() || UidOcrRoi.Right > frame.Width || UidOcrRoi.Bottom > frame.Height)
                    return null;

                using var roiMat = new Mat(frame, UidOcrRoi);
                return roiMat.Clone();
            }

            _logService.LogInfo($"开始 OCR 测试 UID 区域 193x27...");

            using (var debugMat = CaptureUidRegion())
            {
                if (debugMat != null)
                {
                    string testDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "OcrTest");
                    if (!Directory.Exists(testDir)) Directory.CreateDirectory(testDir);
                    string imgPath = Path.Combine(testDir, $"uid_test_{DateTime.Now:HHmmss}.png");
                    Cv2.ImWrite(imgPath, debugMat);
                    _shellService.OpenFolder(testDir);
                }
            }

            const int n = 20;
            var capTimes = new List<double>();
            var ocrTimes = new List<double>();
            string lastText = "";

            using (var warmMat = CaptureUidRegion())
            {
                if (warmMat != null)
                {
                    await _ocrService.RecognizeTextAsync(warmMat);
                }
                else
                {
                    _logService.LogWarning("[压测] 预热截图失败，#01 会偏慢，以 #02 之后为准");
                }
            }
            _logService.LogInfo("[压测] 预热完成，开始循环");

            for (int i = 1; i <= n; i++)
            {
                var swCap = System.Diagnostics.Stopwatch.StartNew();
                using var bmp = CaptureUidRegion();
                swCap.Stop();

                if (bmp == null)
                {
                    _logService.LogWarning($"[压测] #{i:00} 截图失败，跳过");
                    continue;
                }

                capTimes.Add(swCap.Elapsed.TotalMilliseconds);

                var swOcr = System.Diagnostics.Stopwatch.StartNew();
                var text = await _ocrService.RecognizeTextAsync(bmp);
                swOcr.Stop();
                ocrTimes.Add(swOcr.Elapsed.TotalMilliseconds);

                lastText = text;
                _logService.LogInfo($"[压测] #{i:00}  截图 {swCap.Elapsed.TotalMilliseconds,5:F0} ms | " +
                                    $"识别 {swOcr.Elapsed.TotalMilliseconds,5:F0} ms | '{text}'");
                await Task.Delay(100);
            }

            _logService.LogInfo($"[压测] ===== 汇总 {capTimes.Count}/{n} 次 =====");
            if (capTimes.Count > 0 && ocrTimes.Count > 0)
            {
                _logService.LogInfo($"[压测] 截图: 平均 {capTimes.Average():F1} ms | " +
                                    $"识别: 平均 {ocrTimes.Average():F1} ms | " +
                                    $"最快 {ocrTimes.Min():F1} | 最慢 {ocrTimes.Max():F1} ms");
            }
            _logService.LogInfo($"[压测] 最终: '{lastText}'");

            if (string.IsNullOrWhiteSpace(lastText))
            {
                _logService.LogWarning("OCR 未识别到文字。");
                await _dialogService.ShowInfoAsync(
                    "OCR 测试结果",
                    "未识别到文字。\n请查看刚打开的 OcrTest 文件夹中的截图，确认是否截到了清晰的 UID。");
            }
            else
            {
                _logService.LogInfo($"UID 识别结果: {lastText}");
                await _dialogService.ShowInfoAsync("OCR 测试结果", $"识别结果（最后一次）：\n{lastText}");
            }
        }

        /// <summary>
        /// 【临时】识别到"驾驶"提示后发送后台 F 键。测完整个方法删除。
        /// </summary>
        [RelayCommand]
        private async Task TestDriveFAsync()
        {
            if (_automationService.CurrentHwnd == IntPtr.Zero)
            {
                await _dialogService.ShowInfoAsync("无法测试", "请先绑定游戏窗口！");
                return;
            }

            Mat? CaptureDriveHint()
            {
                using var frame = _automationService.GetLatestFrame();
                if (frame == null || frame.Empty() ||
                    DriveHintRoi.Right > frame.Width || DriveHintRoi.Bottom > frame.Height)
                    return null;
                using var roi = new Mat(frame, DriveHintRoi);
                return roi.Clone();
            }

            _logService.LogInfo("[驾驶F] 开始检测（ROI 1139,547 133x80）...");

            using (var dbg = CaptureDriveHint())
            {
                if (dbg != null)
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "OcrTest");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    Cv2.ImWrite(Path.Combine(dir, $"drive_{DateTime.Now:HHmmss}.png"), dbg);
                }
            }

            using var mat = CaptureDriveHint();
            if (mat == null)
            {
                _logService.LogWarning("[驾驶F] 截图失败（画面未就绪或 ROI 越界）");
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = await _ocrService.RecognizeTextAsync(mat);
            sw.Stop();
            _logService.LogInfo($"[驾驶F] 识别 {sw.Elapsed.TotalMilliseconds:F0} ms | 原始输出: '{text}'");

            if (text.Contains("驾"))
            {
                _logService.LogInfo("[驾驶F] ✅ 命中'驾驶'，发送后台按键...");

                // 与 KeyTest 页 TestSendF 完全同源的封装调用（后台按键）
                _inputService.SendKeyPress(_automationService.CurrentHwnd, KeyCode.F);

                _logService.LogInfo($"[驾驶F] 已发送后台按键: F 键 (句柄:{_automationService.CurrentHwnd})");
            }
            else
            {
                _logService.LogInfo("[驾驶F] 未命中'驾驶'，本次不按键");
            }
        }

        [RelayCommand]
        private void OpenModelFolder(string? path)
        {
            string targetDir = OcrModelDir;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    targetDir = dir;
                }
            }

            if (!Directory.Exists(targetDir))
            {
                _logService.LogWarning($"OCR 模型目录不存在: {targetDir}");
                return;
            }

            _shellService.OpenFolder(targetDir);
        }

        [RelayCommand]
        private void ShowOcrDocs()
        {
            DocViewerWindow.ShowDoc("OCR 使用说明", "ocr.md");
        }
    }
}