using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Implementations
{
    /// <summary>
    /// PP-OCR rec-only 识别服务（Python RapidOCR 方案的 C# 原生重建）。
    ///
    /// 核心：固定 ROI（UID 区）文字位置已知 → 跳过 det 模型，只跑 rec：
    ///   裁剪图 → 边缘补边 → 缩放到高 48 → 归一化 [-1,1] → rec.onnx → CTC 解码 → 文本
    ///
    /// 设备策略：优先 DirectML（N卡/A卡/Intel 卡通吃），失败回退 CPU。
    /// 线程安全：InferenceSession.Run 官方保证可并发，无需加锁。
    /// 字典自校验：输出类别数 = 字典行数 + blank(+空格) 对不上就拒绝启动并报出数字。
    /// </summary>
    public class RapidOcrService : IOcrService, IDisposable
    {
        private readonly ILogService _logService;

        private static readonly string OcrModelDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "NTE-Ocr_Models");

        private InferenceSession? _session;
        private string[] _dict = Array.Empty<string>(); // _dict[i] 对应输出索引 i+1（0 是 CTC blank）
        private bool _hasSpace;                          // 输出末位是否为空格类
        private int _inputH = 48;
        private int _fixedInputW = -1;                   // 模型若按固定宽度导出，此值 >0
        private volatile bool _ready;
        private bool _initFailed;
        private readonly object _initLock = new();

        public RapidOcrService(ILogService logService) => _logService = logService;

        // ══════════════════ 初始化 ══════════════════

        private void EnsureEngine()
        {
            if (_ready || _initFailed) return;
            lock (_initLock)
            {
                if (_ready || _initFailed) return;
                try
                {
                    string recPath = Path.Combine(OcrModelDir, "rec.onnx");
                    if (!File.Exists(recPath))
                    {
                        _logService.LogError("[OCR] rec.onnx 不存在，无法初始化识别引擎");
                        _initFailed = true;
                        return;
                    }

                    // 1) EP 选择：先试 DirectML（全显卡通用），失败回退 CPU
                    InferenceSession session; string ep;
                    try
                    {
                        var so = NewSessionOptions();
                        so.AppendExecutionProvider_DML(0);
                        session = new InferenceSession(recPath, so);
                        ep = "DirectML(GPU)";
                    }
                    catch (Exception ex)
                    {
                        _logService.LogInfo($"[OCR] DirectML 初始化失败（{FirstLine(ex.Message)}），回退 CPU");
                        session = new InferenceSession(recPath, NewSessionOptions());
                        ep = "CPU";
                    }
                    _session = session;

                    // 2) 读输入元数据（有的导出是固定尺寸）
                    var inDims = _session.InputMetadata.Values.First().Dimensions;
                    if (inDims.Length >= 4)
                    {
                        if (inDims[2] > 0) _inputH = inDims[2];
                        if (inDims[3] > 0) _fixedInputW = inDims[3];
                    }

                    // 3) 预热：跑一张全零图，既完成 EP 编译，也拿到真实输出类别数
                    int classes = WarmupAndGetClasses();
                    if (classes <= 0)
                    {
                        _logService.LogError("[OCR] 预热失败，无法确定输出类别数");
                        _initFailed = true;
                        return;
                    }

                    // 4) 字典获取：优先读 rec.onnx 内嵌字典（官方 ONNX 自带，Python 端就是这么成功的）
                    if (!TryLoadEmbeddedDict(classes))
                    {
                        // 5) 内嵌没有或对不上 → 退回文件字典匹配
                        if (!TryMatchDict(classes))
                        {
                            _initFailed = true;
                            return;
                        }
                    }

                    _ready = true;
                    _logService.LogInfo($"[OCR] ✅ rec-only 引擎就绪 | EP={ep} | 输入 {_inputH}x" +
                        $"{(_fixedInputW > 0 ? _fixedInputW.ToString() : "动态")} | 字典 {_dict.Length} 字" +
                        $"{(_hasSpace ? " + 空格" : "")}");
                }
                catch (Exception ex)
                {
                    _initFailed = true;
                    _logService.LogError($"[OCR] 引擎初始化失败: {ex.Message}");
                }
            }
        }

        private static SessionOptions NewSessionOptions()
        {
            var so = new SessionOptions();
            so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            return so;
        }

        private int WarmupAndGetClasses()
        {
            try
            {
                int w = _fixedInputW > 0 ? _fixedInputW : 320;
                var dummy = new DenseTensor<float>(new[] { 1, 3, _inputH, w });
                using var results = _session!.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), dummy)
                });
                var outDims = results.First().AsTensor<float>().Dimensions;
                return outDims[^1] > 0 ? outDims[^1] : -1;
            }
            catch (Exception ex)
            {
                _logService.LogError($"[OCR] 预热推理失败: {FirstLine(ex.Message)}");
                return -1;
            }
        }

        /// <summary>从 rec.onnx 元数据读内嵌字典（官方 PP-OCR ONNX 自带 dictionary 键）。
        /// 返回 true 表示内嵌字典已加载且类别数匹配。</summary>
        private bool TryLoadEmbeddedDict(int classes)
        {
            try
            {
                var map = _session!.ModelMetadata.CustomMetadataMap;
                if (!map.TryGetValue("dictionary", out var dictStr) || string.IsNullOrEmpty(dictStr))
                {
                    _logService.LogInfo("[OCR] rec.onnx 无内嵌字典，改用文件字典");
                    return false;
                }

                var lines = dictStr.Split('\n')
                                   .Select(l => l.TrimEnd('\r'))
                                   .Where(l => l.Length > 0)
                                   .ToArray();

                if (classes == lines.Length + 1 || classes == lines.Length + 2)
                {
                    _dict = lines;
                    _hasSpace = classes == lines.Length + 2;
                    _logService.LogInfo($"[OCR] 使用模型内嵌字典（{lines.Length} 字，输出 {classes} 类）✅");
                    return true;
                }

                _logService.LogWarning(
                    $"[OCR] 内嵌字典（{lines.Length} 行）与输出 {classes} 类不符，改用文件字典");
                return false;
            }
            catch (Exception ex)
            {
                _logService.LogInfo($"[OCR] 读模型元数据失败: {FirstLine(ex.Message)}，改用文件字典");
                return false;
            }
        }

        private bool TryMatchDict(int classes)
        {
            foreach (var name in new[] { "ppocrv6_dict.txt", "ppocrv6_tiny_dict.txt", "ppocr_keys_v1.txt" })
            {
                string path = Path.Combine(OcrModelDir, name);
                if (!File.Exists(path)) continue;

                var lines = File.ReadAllLines(path)
                                .Select(l => l.TrimEnd('\r', ' '))
                                .Where(l => l.Length > 0)
                                .ToArray();

                // CTC 输出维度 = blank(1) + 字典行数 (+1 若导出时含空格类)
                if (classes == lines.Length + 1 || classes == lines.Length + 2)
                {
                    _dict = lines;
                    _hasSpace = classes == lines.Length + 2;
                    _logService.LogInfo($"[OCR] 字典匹配成功: {name}（{lines.Length} 行，输出 {classes} 类）✅");
                    return true;
                }
                _logService.LogWarning($"[OCR] 字典 {name}（{lines.Length} 行）与输出 {classes} 类不符，跳过");
            }

            _logService.LogError(
                $"[OCR] 所有候选字典都与 rec.onnx 对不上（输出 {classes} 类）。" +
                $"请把与 rec.onnx 配套的字典放入 {OcrModelDir}");
            return false;
        }

        // ══════════════════ 识别 ══════════════════

        public async Task<string> RecognizeTextAsync(Mat image, CancellationToken token = default)
        {
            EnsureEngine();
            if (!_ready || image == null || image.Empty()) return string.Empty;
            token.ThrowIfCancellationRequested();

            return await Task.Run(() =>
            {
                try
                {
                    using var bgr = ToBgr(image);
                    using var bordered = new Mat();
                    // 四周复制边缘 8px：防止 ROI 恰好切到字（Python 端 det 会自动找字，rec-only 没这层保护）
                    Cv2.CopyMakeBorder(bgr, bordered, 8, 8, 8, 8, BorderTypes.Replicate);

                    var input = BuildInput(bordered, out int width);
                    using var results = _session!.Run(new[]
                    {
                        NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), input)
                    });
                    var output = results.First().AsTensor<float>();

                    string raw = CtcDecode(output);
                    _logService.LogInfo($"[OCR] 原始输出: '{raw}'");   // 排查关键：永远先记原始串再清理
                    return Clean(raw);
                }
                catch (Exception ex)
                {
                    _logService.LogError($"[OCR] 识别异常: {FirstLine(ex.Message)}");
                    return string.Empty;
                }
            }, token).ConfigureAwait(false);
        }

        private static Mat ToBgr(Mat image)
        {
            if (image.Channels() == 4) { var m = new Mat(); Cv2.CvtColor(image, m, ColorConversionCodes.BGRA2BGR); return m; }
            if (image.Channels() == 1) { var m = new Mat(); Cv2.CvtColor(image, m, ColorConversionCodes.GRAY2BGR); return m; }
            return image.Clone();
        }

        /// <summary>缩放到高 48、宽按比例（8 对齐），归一化 (x/255 - 0.5)/0.5，BGR 通道序，CHW 布局。</summary>
        private DenseTensor<float> BuildInput(Mat bgr, out int width)
        {
            int h = bgr.Height, w = bgr.Width;
            int targetW = _fixedInputW > 0
                ? _fixedInputW
                : Math.Min(1280, ((int)Math.Ceiling(_inputH * (float)w / h) + 7) / 8 * 8);
            width = targetW;

            using var resized = new Mat();
            Cv2.Resize(bgr, resized, new OpenCvSharp.Size(targetW, _inputH), 0, 0, InterpolationFlags.Linear);

            var buffer = new float[3 * _inputH * targetW];
            Mat[] chans = resized.Split(); // B, G, R —— 与 Paddle/RapidOCR 的 BGR 输入一致
            try
            {
                for (int c = 0; c < 3; c++)
                {
                    chans[c].GetArray(out byte[] plane);
                    int off = c * _inputH * targetW;
                    for (int i = 0; i < plane.Length; i++)
                        buffer[off + i] = plane[i] / 127.5f - 1f;
                }
            }
            finally { foreach (var m in chans) m.Dispose(); }

            return new DenseTensor<float>(new Memory<float>(buffer), new[] { 1, 3, _inputH, targetW });
        }

        /// <summary>CTC 解码：逐帧 argmax，折叠连续重复，跳过 blank(0)，按字典映射。
        /// 兼容 [1,T,C] / [T,1,C] / [T,C] 三种导出布局（batch 维恒为 1，flat 公式通用）。</summary>
        private string CtcDecode(Tensor<float> output)
        {
            var dims = output.Dimensions;
            long total = 1; foreach (var d in dims) total *= d;
            int classes = dims[^1];
            if (classes <= 0) return string.Empty;
            int tLen = (int)(total / classes);

            var sb = new StringBuilder(tLen);
            int prev = -1;
            for (int t = 0; t < tLen; t++)
            {
                int best = 0; float bestVal = float.NegativeInfinity;
                for (int c = 0; c < classes; c++)
                {
                    float v = dims.Length switch
                    {
                        3 => dims[0] == 1 ? output[0, t, c] : output[t, 0, c],
                        _ => output[t, c]
                    };
                    if (v > bestVal) { bestVal = v; best = c; }
                }
                if (best != prev && best != 0)
                {
                    string ch = CharAt(best);
                    if (ch.Length > 0) sb.Append(ch);
                }
                prev = best;
            }
            return sb.ToString();
        }

        private string CharAt(int idx)
        {
            if (idx <= 0) return string.Empty;                 // CTC blank
            if (idx <= _dict.Length) return _dict[idx - 1];
            if (_hasSpace && idx == _dict.Length + 1) return " ";
            return string.Empty;
        }

        private static string Clean(string raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? string.Empty
                : Regex.Replace(raw, @"[^\w:\u4e00-\u9fa5]", "");

        private static string FirstLine(string msg) =>
            (msg ?? string.Empty).Split('\n')[0];

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
            _ready = false;
        }
    }
}