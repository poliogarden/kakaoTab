using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
// using System.Windows.Automation; // .NET 9에서 직접 사용 불가, P/Invoke로 대체 필요

namespace KakaoTab
{
    /// <summary>
    /// WinEventHook을 사용하여 Toast 알림을 감지하는 클래스
    /// </summary>
    public sealed class ToastWatcher : IDisposable
    {
        private IntPtr _hookHandle = IntPtr.Zero;
        private WinEventProcDelegate? _winEventProc;
        private bool _isDisposed = false;
        private readonly Dictionary<string, DateTime> _recentToasts = new Dictionary<string, DateTime>();
        private readonly object _recentToastsLock = new object();
        private System.Threading.Timer? _cleanupTimer;
        private bool _debugMode = true; // 디버그 모드 플래그

        // WinEventHook 관련 상수
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0x00000000;
        private const int CHILDID_SELF = 0;

        public event Action<ToastInfo>? OnToast;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventProcDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        // WinEventProc 델리게이트 (GC 방지를 위해 필드에 보관)
        public delegate void WinEventProcDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsTimeStamp);

        public ToastWatcher()
        {
            // 최근 토스트 정리 타이머 (5초마다)
            _cleanupTimer = new System.Threading.Timer(CleanupRecentToasts, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public void Start()
        {
            if (_hookHandle != IntPtr.Zero)
                return; // 이미 시작됨

            // WinEventProc 델리게이트를 필드에 보관 (GC 방지)
            _winEventProc = WinEventProc;

            // EVENT_OBJECT_SHOW 이벤트 구독
            _hookHandle = SetWinEventHook(
                EVENT_OBJECT_SHOW,
                EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventProc,
                0, // 모든 프로세스
                0, // 모든 스레드
                WINEVENT_OUTOFCONTEXT);

            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] SetWinEventHook failed: error={error}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[ToastWatcher] Hook installed");
            }
        }

        public void Stop()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
                System.Diagnostics.Debug.WriteLine("[ToastWatcher] Stopped");
            }
        }

        private void WinEventProc(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsTimeStamp)
        {
            try
            {
                // idObject==OBJID_WINDOW && idChild==0 인 이벤트만 처리
                if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
                    return;

                // hwnd가 0이면 무시
                if (hwnd == IntPtr.Zero)
                    return;

                // 창이 유효한지 확인
                if (!IsWindow(hwnd))
                    return;

                // 프로세스명 확인
                GetWindowThreadProcessId(hwnd, out uint processId);
                string? processName = GetProcessName(processId);

                // 프로세스명이 ShellExperienceHost/StartMenuExperienceHost/explorer가 아니면 무시
                if (string.IsNullOrEmpty(processName) ||
                    (!processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) &&
                     !processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) &&
                     !processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)))
                {
                    if (_debugMode)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ToastWatcher] Ignored: hwnd={hwnd}, proc={processName}");
                    }
                    return;
                }

                if (_debugMode)
                {
                    System.Diagnostics.Debug.WriteLine($"[ToastWatcher] Event: hwnd={hwnd}, proc={processName}");
                }

                // UI Automation으로 텍스트 추출
                ToastInfo? toastInfo = ExtractToastTextWithUIA(hwnd);
                if (toastInfo == null)
                    return;

                // 중복 토스트 방지 (최근 2초 내 동일 텍스트 해시)
                string textHash = ComputeTextHash(toastInfo.RawText);
                lock (_recentToastsLock)
                {
                    if (_recentToasts.TryGetValue(textHash, out DateTime lastTime))
                    {
                        if ((DateTime.Now - lastTime).TotalSeconds < 2)
                        {
                            if (_debugMode)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] Duplicate toast ignored: {toastInfo.RawText.Substring(0, Math.Min(50, toastInfo.RawText.Length))}");
                            }
                            return;
                        }
                    }
                    _recentToasts[textHash] = DateTime.Now;
                }

                // 디버그: RawText 상위 5줄 출력
                if (_debugMode && !string.IsNullOrEmpty(toastInfo.RawText))
                {
                    var lines = toastInfo.RawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int logCount = Math.Min(5, lines.Length);
                    System.Diagnostics.Debug.WriteLine($"[ToastWatcher] RawText (top {logCount} lines):");
                    for (int i = 0; i < logCount; i++)
                    {
                        System.Diagnostics.Debug.WriteLine($"  [{i + 1}] {lines[i]}");
                    }
                }

                // 이벤트 발생
                OnToast?.Invoke(toastInfo);
            }
            catch (Exception ex)
            {
                // 예외는 조용히 무시
                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] Error in WinEventProc: {ex.Message}");
            }
        }

        private string? GetProcessName(uint processId)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// UI Automation을 사용하여 Toast 텍스트 추출
        /// 주의: .NET 9에서는 System.Windows.Automation을 직접 사용할 수 없으므로,
        /// 현재는 Win32 API로 대체합니다. 향후 P/Invoke로 COM 인터페이스를 직접 호출하도록 개선 필요.
        /// </summary>
        private ToastInfo? ExtractToastTextWithUIA(IntPtr hwnd)
        {
            try
            {
                // .NET 9에서는 System.Windows.Automation을 직접 사용할 수 없으므로
                // 기본적인 Win32 API로 텍스트 추출 시도
                // TODO: P/Invoke를 통해 UI Automation COM 인터페이스를 직접 호출하도록 개선 필요

                List<string> lines = new List<string>();
                string rawText = string.Empty;

                // 현재 hwnd에서 텍스트 수집 시도
                CollectWindowTexts(hwnd, lines, 0, 3);

                // 텍스트가 비었으면 부모로 6단계 올라가며 재시도
                if (lines.Count == 0)
                {
                    IntPtr currentHwnd = hwnd;
                    for (int i = 0; i < 6; i++)
                    {
                        IntPtr parentHwnd = GetParent(currentHwnd);
                        if (parentHwnd == IntPtr.Zero || !IsWindow(parentHwnd))
                            break;

                        CollectWindowTexts(parentHwnd, lines, 0, 3);
                        if (lines.Count > 0)
                            break;

                        currentHwnd = parentHwnd;
                    }
                }

                if (lines.Count == 0)
                    return null;

                rawText = string.Join(" ", lines);
                string appHint = GetWindowTitle(hwnd);

                return new ToastInfo
                {
                    AppHint = appHint,
                    Lines = lines,
                    RawText = rawText
                };
            }
            catch (Exception ex)
            {
                // 예외는 조용히 무시
                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] ExtractToastTextWithUIA failed: {ex.Message}");
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private string GetWindowTitle(IntPtr hWnd)
        {
            try
            {
                int length = GetWindowTextLength(hWnd);
                if (length == 0) return string.Empty;
                StringBuilder sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private void CollectWindowTexts(IntPtr hWnd, List<string> texts, int depth, int maxDepth)
        {
            if (depth >= maxDepth)
                return;

            try
            {
                // 현재 창의 텍스트
                string text = GetWindowTitle(hWnd);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 1)
                {
                    texts.Add(text);
                }

                // 자식 창 열거
                EnumChildWindows(hWnd, (childHwnd, lParam) =>
                {
                    CollectWindowTexts(childHwnd, texts, depth + 1, maxDepth);
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // 무시
            }
        }

        private string ComputeTextHash(string text)
        {
            // 간단한 해시 (실제로는 더 정교한 해시 함수 사용 가능)
            return text.GetHashCode().ToString();
        }

        private void CleanupRecentToasts(object? state)
        {
            lock (_recentToastsLock)
            {
                var now = DateTime.Now;
                var keysToRemove = _recentToasts
                    .Where(kvp => (now - kvp.Value).TotalSeconds > 10)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _recentToasts.Remove(key);
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            Stop();
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;
            _isDisposed = true;
        }
    }
}
