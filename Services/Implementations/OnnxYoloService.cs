using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace TrustNTE.Services.Implementations
{
    public class YoloResult
    {
        public Rect Rect { get; set; }
        public float Confidence { get; set; }
    }

    public class OnnxYoloService : IDisposable
    {
        private InferenceSession? _session;
        private readonly string _modelPath;
        private readonly Action<string>? _log;
        private readonly object _sessionLock = new();

        private string _inputName = "";
        private int _inputH = 640, _inputW = 640;
        private bool _currentUseGpu;

        //    用于接收线程安全的引擎切换请求
        private int _pendingEngine = -1; // -1=无请求；0=CPU；1=GPU

        public OnnxYoloService(string modelPath, bool useGpu, Action<string>? log = null)
        {
            if (!File.Exists(modelPath)) throw new FileNotFoundException($"YOLO 模型不存在: {modelPath}");
            _modelPath = modelPath;
            _log = log;
            _currentUseGpu = useGpu;
        }

        /// <summary>UI 线程调用：登记引擎切换请求，下一次推理时在锁内生效。</summary>
        public void RequestEngine(bool useGpu) =>
            Interlocked.Exchange(ref _pendingEngine, useGpu ? 1 : 0);

        private void EnsureSession()
        {
            lock (_sessionLock)
            {
                // 检查是否有切换请求
                int req = Interlocked.Exchange(ref _pendingEngine, -1);
                if (req is 0 or 1)
                {
                    _session?.Dispose();
                    _session = null;
                    _currentUseGpu = req == 1;
                    _log?.Invoke($"[YOLO] 引擎切换 → {(_currentUseGpu ? "DirectML" : "CPU")}（session 已重建）");
                }

                if (_session != null) return;

                var so = new SessionOptions();
                string ep = "CPU";
                if (_currentUseGpu)
                {
                    try
                    {
                        so.AppendExecutionProvider_DML(0);
                        ep = "DirectML";
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"[YOLO] ⚠️ DirectML 初始化失败({ex.Message})，回落 CPU");
                        ep = "CPU";
                    }
                }

                _session = new InferenceSession(_modelPath, so);
                _inputName = _session.InputMetadata.Keys.First();
                var dims = _session.InputMetadata[_inputName].Dimensions;
                _inputH = dims[2] > 0 ? dims[2] : 640;
                _inputW = dims[3] > 0 ? dims[3] : 640;
                _log?.Invoke($"[YOLO] 模型加载完成 | EP={ep} ✅ | 输入 {_inputW}x{_inputH}");
            }
        }

        public List<YoloResult> Detect(Mat frame, double confidenceThreshold = 0.65)
        {
            EnsureSession(); // 确保 session 存在且引擎正确

            // ════════════════════════════════════════════════════════════════
            // ① 前处理：Letterbox（对齐 ultralytics：等比缩放 + 灰边114填充）
            // ════════════════════════════════════════════════════════════════
            double r = Math.Min((double)_inputW / frame.Width, (double)_inputH / frame.Height);
            int newW = (int)Math.Round(frame.Width * r);
            int newH = (int)Math.Round(frame.Height * r);
            int padX = (_inputW - newW) / 2;
            int padY = (_inputH - newH) / 2;

            using var resized = new Mat();
            Cv2.Resize(frame, resized, new OpenCvSharp.Size(newW, newH));

            using var letterboxed = new Mat(_inputH, _inputW, frame.Type(), new Scalar(114, 114, 114));
            resized.CopyTo(letterboxed[new OpenCvSharp.Rect(padX, padY, newW, newH)]);

            using Mat blob = CvDnn.BlobFromImage(letterboxed, 1.0 / 255.0,
                new OpenCvSharp.Size(_inputW, _inputH), new Scalar(0, 0, 0), true, false);

            float[] blobData = new float[blob.Total()];
            Marshal.Copy(blob.Data, blobData, 0, blobData.Length);

            var input = new DenseTensor<float>(blobData, new[] { 1, 3, _inputH, _inputW });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
            var results = new List<YoloResult>();

            lock (_sessionLock)
            {
                if (_session == null) return results;

                using (var outputs = _session.Run(inputs))
                {
                    var output = outputs.First().AsTensor<float>();
                    var shape = output.Dimensions;
                    var data = output.ToArray();

                    bool isTransposed = shape[1] < shape[2];
                    int numAnchors = isTransposed ? (int)shape[2] : (int)shape[1];
                    int numClasses = isTransposed ? (int)shape[1] - 4 : (int)shape[2] - 4;

                    // ════════════════════════════════════════════════════════════════
                    // ② 坐标反映射：单尺度反 padding
                    // ════════════════════════════════════════════════════════════════
                    float invR = (float)(1.0 / r);

                    for (int i = 0; i < numAnchors; i++)
                    {
                        float maxScore = 0; int maxClass = -1;
                        for (int c = 0; c < numClasses; c++)
                        {
                            float score = isTransposed ? data[(c + 4) * numAnchors + i] : data[i * (numClasses + 4) + c + 4];
                            if (score > maxScore) { maxScore = score; maxClass = c; }
                        }

                        if (maxScore >= confidenceThreshold)
                        {
                            float cx = isTransposed ? data[0 * numAnchors + i] : data[i * (numClasses + 4) + 0];
                            float cy = isTransposed ? data[1 * numAnchors + i] : data[i * (numClasses + 4) + 1];
                            float w = isTransposed ? data[2 * numAnchors + i] : data[i * (numClasses + 4) + 2];
                            float h = isTransposed ? data[3 * numAnchors + i] : data[i * (numClasses + 4) + 3];

                            // 反向映射回原图：先减去灰边，再除以缩放比
                            int x = Math.Max(0, (int)(((cx - w / 2) - padX) * invR));
                            int y = Math.Max(0, (int)(((cy - h / 2) - padY) * invR));
                            int width = Math.Min(frame.Width - x, (int)(w * invR));
                            int height = Math.Min(frame.Height - y, (int)(h * invR));

                            // ③ 尺寸过滤：放宽到 8x6，防止小怪或远处的 Lv 标签被误杀
                            if (width > 8 && height > 6)
                                results.Add(new YoloResult { Rect = new Rect(x, y, width, height), Confidence = maxScore });
                        }
                    }
                }
            }
            return NMS(results, 0.45);
        }

        private List<YoloResult> NMS(List<YoloResult> boxes, double iouThreshold)
        {
            var result = new List<YoloResult>();
            var sorted = boxes.OrderByDescending(b => b.Confidence).ToList();
            while (sorted.Count > 0)
            {
                var current = sorted[0];
                result.Add(current);
                sorted.RemoveAt(0);
                sorted.RemoveAll(b => Iou(current.Rect, b.Rect) > iouThreshold);
            }
            return result;
        }

        private static double Iou(Rect a, Rect b)
        {
            int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            int w = Math.Max(0, x2 - x1), h = Math.Max(0, y2 - y1);
            int inter = w * h;
            int union = a.Width * a.Height + b.Width * b.Height - inter;
            return union == 0 ? 0 : (double)inter / union;
        }

        public void Dispose()
        {
            lock (_sessionLock)
            {
                _session?.Dispose();
                _session = null;
            }
        }
    }
}