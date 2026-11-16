using System;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Models.Gacha;

namespace TrustNTE.Services.Interfaces
{
    /// <summary>
    /// 抽卡抓包引擎接口。实现类：Services\Gacha\GachaCaptureService。
    /// 红线6：StartAsync 必须全链响应 CancellationToken；事件均来自后台线程，
    /// 订阅方（VM）必须经 IDispatcherService 切回 UI 线程（红线2）。
    /// </summary>
    public interface IGachaCaptureService
    {
        bool IsRunning { get; }

        /// <summary>抓包引擎信息变化（后端选择/自适应模式等），启动后推送。</summary>
        event Action<string>? EngineInfoChanged;

        /// <summary>计数器更新（捕获包数, 解码包数）。</summary>
        event Action<int, int>? CountersUpdated;

        /// <summary>解析出一条抽卡记录。</summary>
        event Action<GachaRecord>? RecordDecoded;

        Task StartAsync(GachaCaptureConfig config, CancellationToken token);
        Task StopAsync();
    }
}
