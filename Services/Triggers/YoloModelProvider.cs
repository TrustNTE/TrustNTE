using System;
using System.Collections.Generic;
using OpenCvSharp;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    public sealed record CombatYoloBox(Rect Rect, float Confidence, string? ClassName = null);

    public interface ICombatYoloDetector
    {
        IReadOnlyList<CombatYoloBox>? Detect(Mat frame, float confidence);
        void Dispose();
    }

    public sealed class YoloModelProvider
    {
        public enum YoloLoadState { NotStarted, Loading, Ready, Failed }

        private readonly Func<bool, ICombatYoloDetector> _factory;
        private readonly ILogService _log;
        private readonly object _lock = new();
        private YoloLoadState _state = YoloLoadState.NotStarted;
        private ICombatYoloDetector? _detector;
        private bool? _loadedUseGpu;
        private string? _lastError;

        // ===== ROI 砍除 v2：相交判定 + 近身大怪豁免 =====
        private const double RoiTopCut = 0.15, RoiBottomCut = 0.86;
        private const float BigMonsterMinHeight = 200f;   // 1080P 下近身怪框高≥200，UI 小框≤150
        /// <summary>累计被 ROI 砍掉的框数（诊断：战斗期持续高涨 = 刀太狠）</summary>
        public long RoiKilledTotal { get; private set; }
        /// <summary>最近一个被 ROI 砍掉的框（取证：判断刀砍的是 UI 还是真怪）</summary>
        public (Rect Rect, float Conf, string? Class)? LastKilledSample { get; private set; }
        public YoloModelProvider(Func<bool, ICombatYoloDetector> detectorFactory, ILogService log)
        { _factory = detectorFactory; _log = log; }

        public YoloLoadState State { get { lock (_lock) return _state; } }
        public bool IsReady => State == YoloLoadState.Ready;

        public string StatusText
        {
            get
            {
                lock (_lock)
                {
                    return _state switch
                    {
                        YoloLoadState.NotStarted => "未启动(未进游戏)",
                        YoloLoadState.Loading => $"加载中({(_loadedUseGpu == true ? "GPU" : "CPU")})",
                        YoloLoadState.Ready => $"就绪({(_loadedUseGpu == true ? "GPU" : "CPU")})",
                        YoloLoadState.Failed => $"失败({_lastError})",
                        _ => "?"
                    };
                }
            }
        }

        public void RequestEngine(bool useGpu)
        {
            lock (_lock)
            {
                if (_state == YoloLoadState.Loading) return;
                if (_state == YoloLoadState.Ready && _loadedUseGpu == useGpu) return;
                _log.LogInfo($"[YOLO] 收到引擎切换请求 (UseGpu={useGpu})，准备重建检测器");
            }
            EnsureStartLoading(useGpu);
        }

        public void EnsureStartLoading(bool useGpu)
        {
            lock (_lock)
            {
                if (_state is YoloLoadState.Loading or YoloLoadState.Ready)
                {
                    if (_loadedUseGpu == useGpu) return;
                    _detector?.Dispose();
                    _detector = null;
                    _log.LogInfo($"[YOLO] 设备配置变更（{(_loadedUseGpu == true ? "GPU" : "CPU")}→{(useGpu ? "GPU" : "CPU")}），重建检测器");
                }
                _loadedUseGpu = useGpu;
                _state = YoloLoadState.Loading;
                _lastError = null;
            }
            _log.LogInfo($"[YOLO] 开始后台加载目标检测模型…（设备={(useGpu ? "GPU/DirectML" : "CPU")}）");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var det = _factory(useGpu);
                    if (det == null) throw new InvalidOperationException("模型工厂返回 null（检查模型文件路径）");
                    lock (_lock)
                    {
                        _detector?.Dispose();
                        _detector = det;
                        _state = YoloLoadState.Ready;
                    }
                    _log.LogInfo("[YOLO] ✅ 目标检测模型加载完成，YOLO 路上线");
                }
                catch (System.Exception ex)
                {
                    lock (_lock) { _state = YoloLoadState.Failed; _lastError = ex.Message; }
                    _log.LogError($"[YOLO] ❌ 模型加载失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// v2 判定：顶部 UI 小框照砍；近身大怪（框高≥200）只要与战斗带相交就放行。
        /// v1 只看框顶边——怪一冲脸框顶越线就被枪毙，"锁完就瞎"元凶。
        /// </summary>
        private IReadOnlyList<CombatYoloBox> ApplyRoiFilter(IReadOnlyList<CombatYoloBox> dets, Mat frame)
        {
            float top = (float)(frame.Height * RoiTopCut);
            float bottom = (float)(frame.Height * RoiBottomCut);
            var kept = new List<CombatYoloBox>(dets.Count);
            foreach (var d in dets)
            {
                bool topEdgeInBand = d.Rect.Y >= top && d.Rect.Y <= bottom;
                bool bigIntersects = d.Rect.Height >= BigMonsterMinHeight
                                     && d.Rect.Bottom >= top && d.Rect.Y <= bottom;
                if (topEdgeInBand || bigIntersects) { kept.Add(d); continue; }
                RoiKilledTotal++;
                LastKilledSample = (d.Rect, d.Confidence, d.ClassName);  
            }
            return kept;
        }

        public IReadOnlyList<CombatYoloBox>? Detect(Mat frame, float confidence)
        {
            ICombatYoloDetector? det;
            lock (_lock)
            {
                if (_state != YoloLoadState.Ready) return null;
                det = _detector;
            }
            try
            {
                var raw = det!.Detect(frame, confidence);
                return raw == null ? null : ApplyRoiFilter(raw, frame);
            }
            catch (System.Exception ex)
            {
                _log.LogError($"[YOLO] 检测异常: {ex.Message}");
                return null;
            }
        }
    }
}
