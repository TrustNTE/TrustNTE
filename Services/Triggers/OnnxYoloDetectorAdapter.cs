using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;
using TrustNTE.Services.Implementations;

namespace TrustNTE.Services.Triggers
{
    /// <summary>
    /// 把 OnnxYoloService 包装成 ICombatYoloDetector（DirectML/CPU 双模）
    /// </summary>
    public class OnnxYoloDetectorAdapter : ICombatYoloDetector
    {
        private readonly OnnxYoloService _svc;

        public OnnxYoloDetectorAdapter(string modelPath, bool useGpu, Action<string>? log = null)
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"找不到 YOLO 模型文件: {modelPath}");

            _svc = new OnnxYoloService(modelPath, useGpu, log);
        }

        public IReadOnlyList<CombatYoloBox>? Detect(Mat frame, float conf)
        {
            var results = _svc.Detect(frame, conf);
            if (results == null) return null;

            return results.Select(r => new CombatYoloBox(r.Rect, r.Confidence, "target")).ToList();
        }

        public void Dispose() => _svc.Dispose();
    }
}
