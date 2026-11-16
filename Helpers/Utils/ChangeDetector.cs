using System.Collections.Generic;

namespace TrustNTE.Helpers
{
    /// <summary>
    /// 通用变化检测器：用于防日志刷屏。
    /// 状态机：每次调用 IsChanged 都会无条件更新内部状态。
    /// </summary>
    public sealed class ChangeDetector<T>
    {
        private T _last = default!;
        private bool _hasValue;

        public bool IsChanged(T current)
        {
            if (!_hasValue || !EqualityComparer<T>.Default.Equals(current, _last))
            {
                _hasValue = true;
                _last = current;
                return true;
            }
            return false;
        }
    }
}