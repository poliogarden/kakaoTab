using System;
using System.Collections.Generic;

namespace KakaoTab
{
    /// <summary>
    /// Toast 알림 정보
    /// </summary>
    public class ToastInfo
    {
        public string AppHint { get; set; } = string.Empty;
        public List<string> Lines { get; set; } = new List<string>();
        public string RawText { get; set; } = string.Empty;
    }
}
