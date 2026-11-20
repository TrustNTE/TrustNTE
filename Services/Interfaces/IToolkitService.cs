using System.Collections.Generic;
using TrustNTE.Models.Toolkit;

namespace TrustNTE.Services.Interfaces
{
    /// <summary>工具箱:注册表 + 激活分发。</summary>
    public interface IToolkitService
    {
        IReadOnlyList<ToolkitToolDescriptor> GetTools();
        void Activate(ToolkitToolDescriptor tool);
    }
}
