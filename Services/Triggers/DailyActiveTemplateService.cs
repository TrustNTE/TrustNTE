using TrustNTE.Services.Interfaces;
using TrustNTE.Services;

namespace TrustNTE.Services.Triggers
{
    /// 每日活跃度图集（Assets\Nte_DailyActive）。
    public sealed class NteDailyActiveTemplateService : SpriteAtlasTemplateServiceBase
    {
        public NteDailyActiveTemplateService(ILogService logService)
            : base("Nte_DailyActive", "每日活跃度", logService) { }

       
    }
}
