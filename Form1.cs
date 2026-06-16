using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Drawing; // Added for Color and SolidBrush
using System.IO;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
// using System.Windows.Automation; // UI Automation - .NET 9에서 직접 사용 불가, P/Invoke로 대체
// using WinRT; // WinRT interop - .NET 9에서 사용 불가, 주석 처리

namespace KakaoTab
{
    public partial class Form1 : Form
    {
        // ===== UI Theme (WinForms) =====
        // KakaoTalk-ish light theme
        private static readonly Color UiBack = Color.FromArgb(246, 246, 248);        // app background
        private static readonly Color UiPanel = Color.FromArgb(254, 229, 0);         // kakao yellow
        private static readonly Color UiBorder = Color.FromArgb(224, 224, 228);      // subtle border
        private static readonly Color UiText = Color.FromArgb(24, 24, 28);           // near-black text
        private static readonly Color UiMuted = Color.FromArgb(110, 110, 120);       // secondary text
        private static readonly Color UiTabIdle = Color.FromArgb(240, 240, 240);     // light gray for inactive tabs
        private static readonly Color UiTabSelected = Color.FromArgb(255, 232, 18); // bright yellow for active tab (KakaoTalk brand color)
        private const bool UseBorderlessChrome = false;

        private System.Windows.Forms.Timer? scanTimer;
        private HashSet<IntPtr> knownWindows = new HashSet<IntPtr>();
        private readonly Dictionary<IntPtr, ChatWindowState> chatStates = new Dictionary<IntPtr, ChatWindowState>();
        private CheckBox chkAutoDetect;
        private Button btnAddWindow;
        private Button btnDiagnostics;
        private Label? lblTitle;
        private Button? btnMinimize;
        private Button? btnClose;
        private bool isAddWindowMode = false;
        private System.Windows.Forms.Timer focusTimer = new System.Windows.Forms.Timer();
        private NotifyIcon notifyIcon;
        private ToastWatcher? toastWatcher; // Toast 알림 감지
        private DateTime lastUnstableLog = DateTime.MinValue; // unstable tick skip 로그용
        private bool isSortingTabs = false; // 정렬 중 무한 루프 방지 플래그
        private int lastContextTabIndex = -1; // 우클릭한 탭 인덱스 저장
        private Dictionary<string, WebView2> webViews = new Dictionary<string, WebView2>(); // AI 탭 WebView2 매핑
        private Button? btnRefreshWebView; // 웹뷰 새로고침 버튼
        private MouseWheelForwardFilter? wheelFilter;
        // private UserNotificationListener? notificationListener; // WinRT - .NET 9에서 사용 불가, 주석 처리

        public Form1()
        {
            InitializeComponent();
            this.Icon = new Icon("KakaoTab.ico");
            this.Load += Form1_Load;
            this.Resize += Form1_Resize;

            tabControlMain.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControlMain.ItemSize = new Size(120, 30); // 탭 너비/높이 고정 (원하는 값으로 조정)
            tabControlMain.SizeMode = TabSizeMode.Fixed; // (혹시 빠져있다면 추가)

            tabControlMain.DrawItem += TabControlMain_DrawItem;
            tabControlMain.MouseDown += TabControlMain_MouseDown;
            tabControlMain.MouseClick += TabControlMain_MouseClick;
            
            // 탭 우클릭 메뉴 설정
            ContextMenuStrip tabContextMenu = new ContextMenuStrip();
            ToolStripMenuItem pinItem = new ToolStripMenuItem("고정");
            pinItem.Click += PinItem_Click;
            tabContextMenu.Items.Add(pinItem);
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("설정");
            settingsItem.Click += SettingsItem_Click;
            tabContextMenu.Items.Add(settingsItem);
            tabContextMenu.Opening += TabContextMenu_Opening; // 메뉴 열 때 현재 탭 상태 반영
            tabControlMain.ContextMenuStrip = tabContextMenu;

            // 트레이 아이콘 제거 (탭 배지로만 알림 표시)
            // notifyIcon = new NotifyIcon();
            // notifyIcon.Icon = this.Icon;
            // notifyIcon.Visible = true;
            // notifyIcon.Text = "KakaoTab";

            // 자동 감지 ON/OFF 체크박스 추가
            chkAutoDetect = new CheckBox();
            chkAutoDetect.Text = "대화창 자동 감지";
            chkAutoDetect.Checked = true;
            chkAutoDetect.Top = 6;
            chkAutoDetect.Left = 8;
            chkAutoDetect.AutoSize = true;
            chkAutoDetect.CheckedChanged += ChkAutoDetect_CheckedChanged;
            topPanel.Controls.Add(chkAutoDetect);

            // 진단 버튼: 현재 감지되는 카카오톡 창 목록 표시
            btnDiagnostics = new Button();
            btnDiagnostics.Text = "진단";
            btnDiagnostics.Top = 3;
            btnDiagnostics.Left = 100;
            btnDiagnostics.Height = 26;
            btnDiagnostics.Click += BtnDiagnostics_Click;
            //topPanel.Controls.Add(btnDiagnostics);

            // 웹뷰 새로고침 버튼
            btnRefreshWebView = new Button();
            btnRefreshWebView.Text = "↻ 새로고침";
            btnRefreshWebView.Top = 3;
            btnRefreshWebView.Left = 160;
            btnRefreshWebView.Height = 26;
            btnRefreshWebView.Width = 100;
            btnRefreshWebView.Click += BtnRefreshWebView_Click;
            btnRefreshWebView.Visible = false; // 기본적으로 숨김, 웹탭 선택 시 표시
            topPanel.Controls.Add(btnRefreshWebView);

            // + 대화방 추가 버튼 생성 및 이벤트 연결 주석 처리
            // btnAddWindow = new Button();
            // btnAddWindow.Text = "+ 대화방 추가";
            // btnAddWindow.Top = 4;
            // btnAddWindow.Left = 120;
            // btnAddWindow.Height = 24;
            // btnAddWindow.Visible = false;
            // btnAddWindow.Click += BtnAddWindow_Click;
            // topPanel.Controls.Add(btnAddWindow);
            // chkAutoDetect.BringToFront();

            focusTimer.Interval = 300; // 0.3초마다 체크
            focusTimer.Tick += FocusTimer_Tick;
            focusTimer.Start();

            this.Activated += (_, _) => BeginInvoke(FocusSelectedTabContent);

            wheelFilter = new MouseWheelForwardFilter(this);
            Application.AddMessageFilter(wheelFilter);

            // 마지막에 테마 적용 (컨트롤이 모두 생성된 후)
            ApplyModernTheme();
        }

        private sealed class MouseWheelForwardFilter : IMessageFilter
        {
            private readonly Form1 _form;

            public MouseWheelForwardFilter(Form1 form) => _form = form;

            public bool PreFilterMessage(ref Message m)
            {
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_MOUSEHWHEEL = 0x020E;
                if (m.Msg != WM_MOUSEWHEEL && m.Msg != WM_MOUSEHWHEEL)
                    return false;

                return _form.TryForwardMouseWheel(m.Msg, m.WParam, m.LParam);
            }
        }

        private bool TryForwardMouseWheel(int msg, IntPtr wParam, IntPtr lParam)
        {
            var selectedTab = tabControlMain.SelectedTab;
            if (selectedTab?.Tag is not TabTag tag || tag.IsWebTab || tag.Hwnd == IntPtr.Zero)
                return false;

            Point pt = tabControlMain.PointToClient(Control.MousePosition);
            if (!tabControlMain.DisplayRectangle.Contains(pt))
                return false;

            return WindowEmbedder.ForwardMouseWheel(tag.Hwnd, msg, wParam, lParam);
        }

        private void ApplyModernTheme()
        {
            // Base
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            this.BackColor = UiBack;
            this.ForeColor = UiText;

            // Top panel
            topPanel.Height = 30;
            topPanel.BackColor = UiPanel;

            // Tabs (owner draw already)
            tabControlMain.BackColor = UiBack;

            // Style existing controls
            chkAutoDetect.ForeColor = UiText;
            chkAutoDetect.BackColor = Color.Transparent;
            chkAutoDetect.AutoSize = true;
            StyleKakaoButton(btnDiagnostics);
            if (btnRefreshWebView != null)
            {
                StyleKakaoButton(btnRefreshWebView);
            }

            // Optional borderless chrome
            if (!UseBorderlessChrome) return;

            this.FormBorderStyle = FormBorderStyle.None;
            this.Padding = new Padding(1);

            lblTitle = new Label
            {
                Text = "KakaoTab",
                ForeColor = UiText,
                BackColor = Color.Transparent,
                AutoSize = true,
                Left = 12,
                Top = 11
            };
            topPanel.Controls.Add(lblTitle);
            lblTitle.BringToFront();

            // 정렬: [아이콘 & 제목] [자동 감지] [진단] [ ..공백.. ] [ - ] [ X ]
            RepositionTopControls();

            btnClose = CreateChromeButton("✕");
            btnClose.Click += (_, _) => this.Close();
            topPanel.Controls.Add(btnClose);

            btnMinimize = CreateChromeButton("—");
            btnMinimize.Click += (_, _) => this.WindowState = FormWindowState.Minimized;
            topPanel.Controls.Add(btnMinimize);

            topPanel.Resize += (_, _) =>
            {
                if (btnClose == null || btnMinimize == null) return;
                btnClose.Width = 44;
                btnMinimize.Width = 44;
                btnClose.Height = 32;
                btnMinimize.Height = 32;
                btnClose.Top = 4;
                btnMinimize.Top = 4;

                btnClose.Left = topPanel.Width - btnClose.Width - 6;
                btnMinimize.Left = btnClose.Left - btnMinimize.Width - 4;

                RepositionTopControls();
            };

            topPanel.MouseDown += TopPanel_MouseDown_DragMove;
            lblTitle.MouseDown += TopPanel_MouseDown_DragMove;
        }

        private void RepositionTopControls()
        {
            // 안전 체크
            if (lblTitle == null || btnDiagnostics == null || chkAutoDetect == null || btnRefreshWebView == null) return;

            // 제목은 왼쪽 고정
            lblTitle.Left = 12;
            lblTitle.Top = (topPanel.Height - lblTitle.Height) / 2;

            // 자동 감지 체크박스는 제목 오른쪽
            chkAutoDetect.Top = (topPanel.Height - chkAutoDetect.Height) / 2;
            chkAutoDetect.Left = lblTitle.Right + 16;

            // 진단 버튼은 체크박스 오른쪽
            btnDiagnostics.Height = 26;
            btnDiagnostics.Top = (topPanel.Height - btnDiagnostics.Height) / 2;
            btnDiagnostics.Left = chkAutoDetect.Right + 12;

            // 새로고침 버튼은 진단 버튼 오른쪽
            if (btnRefreshWebView != null)
            {
                btnRefreshWebView.Height = 26;
                btnRefreshWebView.Top = (topPanel.Height - btnRefreshWebView.Height) / 2;
                btnRefreshWebView.Left = btnDiagnostics.Right + 8;
            }
        }

        private Button CreateChromeButton(string text)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiPanel,
                ForeColor = UiText,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
                TabStop = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            // subtle hover on yellow bar
            b.MouseEnter += (_, _) => b.BackColor = Color.FromArgb(255, 239, 90);
            b.MouseLeave += (_, _) => b.BackColor = UiPanel;
            return b;
        }

        private void StyleKakaoButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(255, 219, 0);
            b.ForeColor = UiText;
            b.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            b.Cursor = Cursors.Hand;
            b.Padding = new Padding(6, 0, 6, 0);
            b.MouseEnter += (_, _) => b.BackColor = Color.FromArgb(255, 210, 0);
            b.MouseLeave += (_, _) => b.BackColor = Color.FromArgb(255, 219, 0);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private void TopPanel_MouseDown_DragMove(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(this.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        private void BtnDiagnostics_Click(object? sender, EventArgs e)
        {
            var sb = new StringBuilder();
            
            // === 탭 상태 요약 ===
            sb.AppendLine("=== 탭 상태 요약 ===\r\n");
            sb.AppendLine($"총 탭 수: {tabControlMain.TabPages.Count}\r\n");
            
            if (tabControlMain.TabPages.Count > 0)
            {
                sb.AppendLine("탭명 | BaseTitle | HasNewActivity | UnreadCount | ActivityStreak | LastActivityDetectedTime | IsManuallyMarkedRead");
                sb.AppendLine("--------------------------------------------------------------------------------------------------------------------------------");
                
                foreach (TabPage tab in tabControlMain.TabPages)
                {
                    if (tab.Tag is TabTag tabTagInfo)
                    {
                        string tabText = tab.Text ?? "(null)";
                        string baseTitle = tabTagInfo.BaseTitle ?? "(null)";
                        string hasActivity = tabTagInfo.HasNewActivity ? "TRUE" : "false";
                        string unreadCount = tabTagInfo.UnreadCount.ToString();
                        string activityStreak = tabTagInfo.ActivityStreak.ToString();
                        string lastActivityTime = tabTagInfo.LastActivityDetectedTime == DateTime.MinValue 
                            ? "(never)" 
                            : tabTagInfo.LastActivityDetectedTime.ToString("HH:mm:ss");
                        string isManual = tabTagInfo.IsManuallyMarkedRead ? "YES" : "no";
                        
                        sb.AppendLine($"{tabText,-15} | {baseTitle,-20} | {hasActivity,-13} | {unreadCount,-11} | {activityStreak,-14} | {lastActivityTime,-22} | {isManual}");
                    }
                    else
                    {
                        sb.AppendLine($"{tab.Text,-15} | (Tag 없음)");
                    }
                }
                
                sb.AppendLine("\r\n=== ROI 해시 상태 ===");
                foreach (TabPage tab in tabControlMain.TabPages)
                {
                    if (tab.Tag is TabTag tabTagInfo)
                    {
                        sb.AppendLine($"\n탭: {tab.Text}");
                        sb.AppendLine($"  LastBottomPack: Hash=0x{tabTagInfo.LastBottomPack.Hash:X16}, Mean={tabTagInfo.LastBottomPack.Mean}");
                        sb.AppendLine($"  LastMidPack: Hash=0x{tabTagInfo.LastMidPack.Hash:X16}, Mean={tabTagInfo.LastMidPack.Mean}");
                        sb.AppendLine($"  LastAuxPack: Hash=0x{tabTagInfo.LastAuxPack.Hash:X16}, Mean={tabTagInfo.LastAuxPack.Mean}");
                        sb.AppendLine($"  LastRoiHash (하위호환): 0x{tabTagInfo.LastRoiHash:X16}");
                        sb.AppendLine($"  HashChangeStreak (하위호환): {tabTagInfo.HashChangeStreak}");
                        sb.AppendLine($"  LastHashUpdate: {tabTagInfo.LastHashUpdate:yyyy-MM-dd HH:mm:ss}");
                        sb.AppendLine($"  LastCheckedTime: {tabTagInfo.LastCheckedTime:yyyy-MM-dd HH:mm:ss}");
                    }
                }
            }
            else
            {
                sb.AppendLine("탭이 없습니다.");
            }
            
            sb.AppendLine("\r\n" + new string('=', 80) + "\r\n");
            
            // 현재 선택된 탭의 자식 윈도우 텍스트 확인
            var selectedTab = tabControlMain.SelectedTab;
            if (selectedTab != null && selectedTab.Tag is TabTag tag && tag.Hwnd != IntPtr.Zero)
            {
                sb.AppendLine("=== 현재 선택된 탭의 자식 윈도우 텍스트 ===\r\n");
                List<(string Text, string ClassName, WindowScanner.RECT Rect)> allItems = new List<(string, string, WindowScanner.RECT)>();
                WindowScanner.CollectAllChildTexts(tag.Hwnd, allItems);
                
                sb.AppendLine($"총 {allItems.Count}개의 컨트롤 발견\r\n");
                sb.AppendLine("텍스트 | 클래스명 | 위치");
                sb.AppendLine("----------------------------------------------");
                
                int count = 0;
                foreach (var item in allItems.OrderBy(x => x.Rect.Top))
                {
                    if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        sb.AppendLine($"\"{item.Text}\" | {item.ClassName} | Top:{item.Rect.Top}, Left:{item.Rect.Left}");
                        count++;
                        if (count > 100) // 최대 100개만 표시
                        {
                            sb.AppendLine("... (더 많은 항목이 있습니다)");
                            break;
                        }
                    }
                }
                
                sb.AppendLine("\r\n=== 숫자 패턴 매칭 결과 ===");
                int found = WindowScanner.FindUnreadCountInWindow(tag.Hwnd);
                sb.AppendLine($"감지된 읽지않음 숫자: {found}");
                
                sb.AppendLine("\r\n=== 대화 내용 추출 (최근 메시지) ===");
                var messages = WindowScanner.ExtractChatMessages(tag.Hwnd);
                if (messages.Count > 0)
                {
                    sb.AppendLine($"총 {messages.Count}개의 메시지 텍스트 발견:\r\n");
                    int msgCount = 0;
                    foreach (var msg in messages.Take(20)) // 최근 20개만 표시
                    {
                        sb.AppendLine($"[{msgCount + 1}] {msg}");
                        msgCount++;
                    }
                    if (messages.Count > 20)
                    {
                        sb.AppendLine($"\r\n... (총 {messages.Count}개 중 20개만 표시)");
                    }
                }
                else
                {
                    sb.AppendLine("대화 내용을 읽을 수 없습니다. 카카오톡이 커스텀 렌더링을 사용하여 Win32 API로는 접근이 제한적일 수 있습니다.");
                }
            }
            else
            {
                sb.AppendLine("선택된 탭이 없습니다.");
            }

            var text = sb.ToString();
            if (text.Length > 10000) text = text.Substring(0, 10000) + "\r\n... (생략)";
            MessageBox.Show(text, "KakaoTab 진단 - 자식 윈도우 텍스트", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ChkAutoDetect_CheckedChanged(object? sender, EventArgs e)
        {
            // btnAddWindow.Visible = !chkAutoDetect.Checked;
            if (chkAutoDetect.Checked)
            {
                StartScanTimer();
            }
            else
            {
                if (scanTimer != null)
                    scanTimer.Stop();
            }
        }

        // BtnAddWindow_Click 주석 처리
        // private void BtnAddWindow_Click(object? sender, EventArgs e)
        // {
        //     isAddWindowMode = true;
        //     MessageBox.Show("추가할 대화방 창을 클릭하세요! (이 창을 닫은 후 원하는 대화방 창을 클릭)");
        //     this.Cursor = Cursors.Cross;
        //     this.MouseDown += Form1_AddWindow_MouseDown;
        // }

        // Form1_AddWindow_MouseDown 주석 처리
        // private void Form1_AddWindow_MouseDown(object? sender, MouseEventArgs e)
        // {
        //     if (!isAddWindowMode) return;
        //     isAddWindowMode = false;
        //     this.Cursor = Cursors.Default;
        //     this.MouseDown -= Form1_AddWindow_MouseDown;
        //
        //     // 마우스 클릭 위치의 화면 좌표 구하기
        //     Point screenPoint = this.PointToScreen(e.Location);
        //     IntPtr hwnd = WindowScanner.WindowFromPoint(screenPoint);
        //     string className = WindowScanner.GetWindowClassName(hwnd);
        //     string title = WindowScanner.GetWindowTitle(hwnd);
        //     if (className == "EVA_Window_Dblclk" && !string.IsNullOrWhiteSpace(title) && title != "카카오톡")
        //     {
        //         if (!knownWindows.Contains(hwnd))
        //         {
        //             var tab = new TabPage(title);
        //             tab.Tag = new TabTag { Hwnd = hwnd };
        //             tabControlMain.TabPages.Add(tab);
        //             knownWindows.Add(hwnd);
        //             tabControlMain.SelectedTab = tab;
        //         }
        //     }
        //     else
        //     {
        //         MessageBox.Show("카카오톡 대화방 창이 아닙니다.");
        //     }
        // }

        private void TabControlMain_DrawItem(object sender, DrawItemEventArgs e)
        {
            var tab = tabControlMain.TabPages[e.Index];
            Rectangle tabRect = tabControlMain.GetTabRect(e.Index);
            bool isSelected = (e.Index == tabControlMain.SelectedIndex);
            Color backColor = isSelected ? UiTabSelected : UiTabIdle;
            using (SolidBrush brush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(brush, tabRect);
            }
            
            // TabTag 가져오기 또는 생성
            TabTag? tag = tab.Tag as TabTag;
            if (tag == null)
            {
                IntPtr hwnd = tab.Tag is IntPtr h ? h : IntPtr.Zero;
                tag = new TabTag { Hwnd = hwnd };
                tab.Tag = tag;
            }
            
            // 닫기 버튼 위치
            Rectangle closeRect = new Rectangle(tabRect.Right - 18, tabRect.Top + 6, 14, 14);
            tag.CloseRect = closeRect;
            
            // 웹탭은 닫기 버튼 숨김
            bool showCloseButton = !tag.IsWebTab;
            
            // 읽지않음 숫자 배지 표시
            int rightMargin = showCloseButton ? 28 : 8; // 웹탭은 닫기 버튼 없으므로 여백 축소
            
            // 읽지않음 숫자가 있으면 숫자 배지 표시
            if (tag.UnreadCount > 0 && !tag.IsManuallyMarkedRead)
            {
                string badgeText = tag.UnreadCount > 99 ? "99+" : tag.UnreadCount.ToString();
                Font badgeFont = new Font(Font.FontFamily, 8, FontStyle.Bold);
                SizeF badgeSize = e.Graphics.MeasureString(badgeText, badgeFont);
                
                // 배지 크기 계산 (최소 크기 보장)
                int badgeWidth = Math.Max((int)badgeSize.Width + 8, 18);
                int badgeHeight = Math.Max((int)badgeSize.Height + 2, 16);
                
                // 배지 위치 (닫기 버튼 왼쪽)
                Rectangle badgeRect = new Rectangle(
                    tabRect.Right - 18 - badgeWidth - 4,
                    tabRect.Top + (tabRect.Height - badgeHeight) / 2,
                    badgeWidth,
                    badgeHeight
                );
                
                // 배지 배경 (빨간색 원형)
                using (SolidBrush badgeBrush = new SolidBrush(Color.FromArgb(255, 59, 48)))
                {
                    e.Graphics.FillEllipse(badgeBrush, badgeRect);
                }
                
                // 배지 텍스트 (흰색)
                using (StringFormat badgeSf = new StringFormat())
                {
                    badgeSf.Alignment = StringAlignment.Center;
                    badgeSf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        e.Graphics.DrawString(badgeText, badgeFont, textBrush, badgeRect, badgeSf);
                    }
                }
                
                badgeFont.Dispose();
                rightMargin += badgeWidth + 4; // 배지 공간 추가
            }
            // 읽지않음 숫자는 없지만 최근 활동이 있으면 빨간 점 표시 (fallback)
            else if (tag.HasNewActivity && tag.UnreadCount == 0)
            {
                // 작은 빨간 점 (8x8 원)
                int dotSize = 8;
                Rectangle dotRect = new Rectangle(tabRect.Right - 18 - dotSize - 4, tabRect.Top + 11, dotSize, dotSize);
                
                // 빨간 점 (눈에 잘 띄는 색상)
                using (SolidBrush dotBrush = new SolidBrush(Color.FromArgb(255, 59, 48)))
                {
                    e.Graphics.FillEllipse(dotBrush, dotRect);
                }
                rightMargin += dotSize + 4; // 점 공간 추가
            }
            
            // 텍스트 영역: 좌측 여백 8, 우측 배지/닫기버튼 고려
            int textOffsetX = tabRect.X + 8;
            
            // 고정된 탭은 핀 아이콘을 왼쪽에 그림
            if (tag.IsPinned)
            {
                int pinSize = 14;
                int pinX = tabRect.X + 6;
                int pinY = tabRect.Y + (tabRect.Height - pinSize) / 2;
                
                // 핀 아이콘 그리기 (간단한 핀 모양)
                using (Pen pinPen = new Pen(UiText, 1.5f))
                {
                    // 핀 머리 (원)
                    e.Graphics.DrawEllipse(pinPen, pinX + 2, pinY + 1, 6, 6);
                    // 핀 바디 (직선)
                    e.Graphics.DrawLine(pinPen, pinX + 5, pinY + 7, pinX + 5, pinY + pinSize - 2);
                    // 핀 끝 (작은 삼각형/다이아몬드)
                    Point[] pinTip = new Point[]
                    {
                        new Point(pinX + 3, pinY + pinSize - 3),
                        new Point(pinX + 5, pinY + pinSize - 1),
                        new Point(pinX + 7, pinY + pinSize - 3)
                    };
                    e.Graphics.DrawLines(pinPen, pinTip);
                }
                
                // 텍스트 시작 위치를 핀 아이콘 오른쪽으로 이동
                textOffsetX += 20;
            }
            
            Rectangle textRect = new Rectangle(textOffsetX, tabRect.Y + 4, tabRect.Width - (textOffsetX - tabRect.X) - rightMargin, tabRect.Height - 8);
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using var textBrush = new SolidBrush(UiText);
                
                // tab.Text는 항상 방 이름만 유지 (상태 텍스트 제거)
                e.Graphics.DrawString(tab.Text, Font, textBrush, textRect, sf);
            }
            
            // 닫기 버튼 (웹탭은 표시하지 않음)
            if (showCloseButton)
            {
                using var closeBrush = new SolidBrush(UiMuted);
                e.Graphics.DrawString("✕", Font, closeBrush, closeRect);
            }
        }

        private void TabControlMain_MouseDown(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < tabControlMain.TabPages.Count; i++)
            {
                var tab = tabControlMain.TabPages[i];
                if (tab.Tag is TabTag tag && tag.CloseRect.Contains(e.Location))
                {
                    // 웹탭은 닫기 비활성 (고정 탭)
                    if (tag.IsWebTab)
                    {
                        break; // 웹탭은 닫을 수 없음
                    }
                    
                    // 창을 ESC로 닫기
                    WindowEmbedder.CloseWindowWithEsc(tag.Hwnd);
                    tabControlMain.TabPages.RemoveAt(i);
                    knownWindows.Remove(tag.Hwnd);
                    
                    // 탭 닫은 후 정렬
                    SortTabsKeepSelection();
                    break;
                }
            }
        }

        private class TabTag
        {
            public IntPtr Hwnd { get; set; }
            public Rectangle CloseRect { get; set; }
            public int UnreadCount { get; set; } = 0;
            public bool HasNewActivity { get; set; } = false; // 최근 활동 있음 (빨간 점 표시용)
            public DateTime LastCheckedTime { get; set; } = DateTime.Now; // 마지막 확인 시간 (타임스탬프 기반)
            public bool IsManuallyMarkedRead { get; set; } = false; // 수동으로 읽음 표시됨
            public string BaseTitle { get; set; } = string.Empty; // 전체 방 제목 (축약 전)
            
            // 탭 고정 및 정렬용 필드
            public bool IsPinned { get; set; } = false;
            public DateTime LastActivatedTime { get; set; } = DateTime.MinValue;
            
            // WebView2 탭 구분용 필드
            public bool IsWebTab { get; set; } = false;
            public string WebViewKey { get; set; } = string.Empty; // "ChatGPT" 또는 "Claude"
            
            // 픽셀 변화 감지용 필드 (개선된 버전)
            public RoiHashPack LastBottomPack { get; set; } = default;
            public RoiHashPack LastMidPack { get; set; } = default;
            public RoiHashPack LastAuxPack { get; set; } = default;
            public int ActivityStreak { get; set; } = 0; // 활동 연속 횟수
            public DateTime LastActivityDetectedTime { get; set; } = DateTime.MinValue; // 마지막 활동 감지 시간 (쿨다운용)
            
            // 하위 호환성 (기존 코드용)
            public ulong LastRoiHash { get; set; } = 0;
            public int HashChangeStreak { get; set; } = 0; // 연속 변화 횟수 (하위 호환성)
            public DateTime LastHashUpdate { get; set; } = DateTime.Now;
        }

        private class ChatWindowState
        {
            public string LastTitle { get; set; } = string.Empty;
            public int LastUnread { get; set; } = 0;
            public DateTime LastFocusedTime { get; set; } = DateTime.MinValue;
            public int LastClientHeight { get; set; } = 0;
            public bool HasNewMessage { get; set; } = false; // 새 메세지 감지 플래그
            public string LastMessageText { get; set; } = string.Empty; // 마지막 메시지 텍스트 (대화 내용 추적용)
            public bool WasMinimized { get; set; } = false; // 창이 최소화되었는지 추적
        }

        // 사용자 설정
        private static class Settings
        {
            public static int CheckIntervalSeconds { get; set; } = 2; // 주기적 체크 간격 (기본 2초)
            public static int InactiveTimeThresholdSeconds { get; set; } = 300; // 비활성 시간 임계값 (기본 5분)
            public static bool UseTimestampBasedDetection { get; set; } = true; // 타임스탬프 기반 감지 사용
            public static bool UseWindowStateTracking { get; set; } = true; // 창 상태 추적 사용
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            // 폼 크기를 400x600으로 고정
            this.Width = 400;
            this.Height = 600;

            // Windows 알림 리스너 초기화 (WinRT interop 필요 - 현재 주석 처리)
            // InitializeNotificationListener();

            // Toast 알림 감지 초기화
            InitializeToastWatcher();

            // AI 탭 생성 (비동기)
            _ = CreateWebTabsAsync();

            TabPage? lastTab = null;
            // 요구사항: 활성화된(포커스된) 대화창만 탭으로 추가
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                string className = WindowScanner.GetWindowClassName(fg);
                string title = WindowScanner.GetWindowTitle(fg);
                if (className == "EVA_Window_Dblclk" && !string.IsNullOrWhiteSpace(title) && title != "카카오톡")
                {
                    var normalized = NormalizeChatTitle(title);
                    var tab = new TabPage(GetShortTitle(normalized.BaseTitle));
                    tab.Tag = new TabTag { Hwnd = fg, HasNewActivity = false, LastCheckedTime = DateTime.Now, BaseTitle = normalized.BaseTitle };
                    RegisterChatTabPage(tab);
                    tabControlMain.TabPages.Add(tab);
                    knownWindows.Add(fg);
                    lastTab = tab;
                    chatStates[fg] = new ChatWindowState { LastTitle = title, LastUnread = normalized.Unread };
                }
            }
            tabControlMain.SelectedIndexChanged += TabControlMain_SelectedIndexChanged;
            if (chkAutoDetect.Checked)
                StartScanTimer();
            
            // 폼 종료 시 알림 리스너 정리 (WinRT - .NET 9에서 사용 불가, 주석 처리)
            // this.FormClosing += (s, e) => 
            // {
            //     if (notificationListener != null)
            //     {
            //         notificationListener.NotificationChanged -= NotificationListener_NotificationChanged;
            //     }
            // };
            
            // 최초 탭 자동 선택 및 직접 임베드
            if (lastTab != null)
            {
                tabControlMain.SelectedTab = lastTab;
                var tag = lastTab.Tag as TabTag;
                if (tag != null && tag.Hwnd != IntPtr.Zero)
                {
                    var bounds = lastTab.ClientRectangle;
                    WindowEmbedder.EmbedWindow(tag.Hwnd, lastTab.Handle, bounds);
                    
                    // 초기 ROI 해시 설정 (3개 ROI 모두)
                    if (PixelActivityDetector.TryGetRoiHashPack(tag.Hwnd, RoiKind.Bottom, out var initialBottomPack))
                    {
                        tag.LastBottomPack = initialBottomPack;
                        tag.LastRoiHash = initialBottomPack.Hash; // 하위 호환성
                    }
                    if (PixelActivityDetector.TryGetRoiHashPack(tag.Hwnd, RoiKind.Mid, out var initialMidPack))
                    {
                        tag.LastMidPack = initialMidPack;
                    }
                    if (PixelActivityDetector.TryGetRoiHashPack(tag.Hwnd, RoiKind.Aux, out var initialAuxPack))
                    {
                        tag.LastAuxPack = initialAuxPack;
                    }
                    tag.LastHashUpdate = DateTime.Now;
                }
                BeginInvoke(FocusSelectedTabContent);
            }

            // 드래그&드롭 이벤트 핸들러 등록 (기본 구조)
            // DragEnter, DragDrop 이벤트 핸들러 주석 처리
            // this.AllowDrop = true;
            // this.DragEnter += Form1_DragEnter;
            // this.DragDrop += Form1_DragDrop;
            
            // 폼 종료 시 알림 리스너 정리 (WinRT - .NET 9에서 사용 불가, 주석 처리)
            // this.FormClosing += (s, e) => 
            // {
            //     if (notificationListener != null)
            //     {
            //         notificationListener.NotificationChanged -= NotificationListener_NotificationChanged;
            //     }
            // };
            
            // 폼 종료 시 ToastWatcher 정리
            this.FormClosing += (s, e) =>
            {
                if (wheelFilter != null)
                    Application.RemoveMessageFilter(wheelFilter);
                toastWatcher?.Dispose();
            };
        }

        /// <summary>
        /// Toast 알림 감지 초기화
        /// </summary>
        private void InitializeToastWatcher()
        {
            try
            {
                toastWatcher = new ToastWatcher();
                toastWatcher.OnToast += ToastWatcher_OnToast;
                toastWatcher.Start();
                System.Diagnostics.Debug.WriteLine("[Form1] ToastWatcher initialized");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Form1] ToastWatcher initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Toast 알림 이벤트 핸들러
        /// </summary>
        private void ToastWatcher_OnToast(ToastInfo toastInfo)
        {
            try
            {
                // 카카오톡 알림 판별
                if (!IsKakaoTalkToast(toastInfo))
                    return;

                // 방 이름 추출
                string? chatRoomName = ExtractChatRoomName(toastInfo);
                if (string.IsNullOrWhiteSpace(chatRoomName))
                    return;

                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] KakaoTalk toast detected: ChatRoom='{chatRoomName}'");

                // UI 스레드에서 탭 업데이트
                if (this.InvokeRequired)
                {
                    this.Invoke(new Action(() => UpdateTabFromToast(chatRoomName)));
                }
                else
                {
                    UpdateTabFromToast(chatRoomName);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] Error processing toast: {ex.Message}");
            }
        }

        /// <summary>
        /// 카카오톡 Toast인지 판별
        /// </summary>
        private bool IsKakaoTalkToast(ToastInfo toastInfo)
        {
            string rawText = toastInfo.RawText ?? string.Empty;
            string appHint = toastInfo.AppHint ?? string.Empty;

            // "KakaoTalk" 또는 "카카오톡"이 포함되어 있으면 카카오톡 알림
            return rawText.Contains("KakaoTalk", StringComparison.OrdinalIgnoreCase) ||
                   rawText.Contains("카카오톡", StringComparison.OrdinalIgnoreCase) ||
                   appHint.Contains("KakaoTalk", StringComparison.OrdinalIgnoreCase) ||
                   appHint.Contains("카카오톡", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Toast에서 대화방 이름 추출
        /// </summary>
        private string? ExtractChatRoomName(ToastInfo toastInfo)
        {
            try
            {
                // Lines에서 첫 번째로 "시간(오전/오후/HH:MM)" 패턴이 아닌 문구 찾기
                foreach (string line in toastInfo.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    string trimmed = line.Trim();

                    // 너무 짧거나(1글자), 숫자만, 시간만은 제외
                    if (trimmed.Length <= 1)
                        continue;

                    // 숫자만 있는 경우 제외
                    if (Regex.IsMatch(trimmed, @"^\d+$"))
                        continue;

                    // 시간 패턴 제외 (오전/오후/HH:MM)
                    if (Regex.IsMatch(trimmed, @"^(오전|오후)\s*\d{1,2}:\d{2}$") ||
                        Regex.IsMatch(trimmed, @"^\d{1,2}:\d{2}$"))
                        continue;

                    // "방이름: ..." 형태면 콜론 앞부분 추출
                    int colonIndex = trimmed.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string beforeColon = trimmed.Substring(0, colonIndex).Trim();
                        if (beforeColon.Length > 1)
                            return beforeColon;
                    }

                    // 일반적인 경우 첫 번째 적합한 라인 반환
                    return trimmed;
                }

                // Lines에서 찾지 못하면 RawText에서 시도
                if (!string.IsNullOrWhiteSpace(toastInfo.RawText))
                {
                    string rawText = toastInfo.RawText.Trim();
                    
                    // "방이름: ..." 형태면 콜론 앞부분 추출
                    int colonIndex = rawText.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string beforeColon = rawText.Substring(0, colonIndex).Trim();
                        if (beforeColon.Length > 1 && !Regex.IsMatch(beforeColon, @"^\d+$"))
                            return beforeColon;
                    }

                    // 첫 번째 공백 전까지 추출
                    int spaceIndex = rawText.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        string firstPart = rawText.Substring(0, spaceIndex).Trim();
                        if (firstPart.Length > 1 && !Regex.IsMatch(firstPart, @"^\d+$"))
                            return firstPart;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] ExtractChatRoomName failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Toast 알림으로부터 탭 업데이트
        /// </summary>
        private void UpdateTabFromToast(string chatRoomName)
        {
            try
            {
                // 모든 탭에서 매칭되는 탭 찾기
                foreach (TabPage tab in tabControlMain.TabPages)
                {
                    if (tab.Tag is TabTag tag && !string.IsNullOrEmpty(tag.BaseTitle))
                    {
                        // BaseTitle과 chatRoomName 매칭
                        if (IsStringMatch(tag.BaseTitle, chatRoomName))
                        {
                            // HasNewActivity만 설정 (UnreadCount는 0 유지)
                            // tab.Text는 변경하지 않음 (점은 DrawItem에서 이미 그림)
                            tag.HasNewActivity = true;

                            // 탭 다시 그리기
                            tabControlMain.Invalidate();
                            
                            System.Diagnostics.Debug.WriteLine($"[ToastWatcher] Tab updated: {tag.BaseTitle} -> HasNewActivity=true");
                            return; // 첫 번째 매칭만 처리
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] No matching tab found for: {chatRoomName}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToastWatcher] UpdateTabFromToast failed: {ex.Message}");
            }
        }

        private void StartScanTimer()
        {
            scanTimer = new System.Windows.Forms.Timer();
            scanTimer.Interval = Settings.CheckIntervalSeconds * 1000; // 사용자 설정 간격
            scanTimer.Tick += ScanTimer_Tick;
            scanTimer.Start();
        }

        private void TabControlMain_MouseClick(object? sender, MouseEventArgs e)
        {
            // 우클릭 시 컨텍스트 메뉴 표시는 자동으로 처리됨
            
            // 우클릭한 탭 인덱스 저장
            if (e.Button == MouseButtons.Right)
            {
                // 마우스 위치를 기준으로 탭 인덱스 찾기
                for (int i = 0; i < tabControlMain.TabPages.Count; i++)
                {
                    Rectangle tabRect = tabControlMain.GetTabRect(i);
                    // 탭 영역 내에 마우스가 있는지 확인
                    if (tabRect.Contains(e.Location))
                    {
                        lastContextTabIndex = i;
                        System.Diagnostics.Debug.WriteLine($"[ContextMenu] Right-clicked tab index: {i}, tab: {tabControlMain.TabPages[i].Text}");
                        break;
                    }
                }
            }
        }
        
        private void TabContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 메뉴 열 때 마우스 위치를 기준으로 탭 인덱스 다시 확인 (더 정확함)
            Point mousePos = tabControlMain.PointToClient(Control.MousePosition);
            for (int i = 0; i < tabControlMain.TabPages.Count; i++)
            {
                Rectangle tabRect = tabControlMain.GetTabRect(i);
                if (tabRect.Contains(mousePos))
                {
                    lastContextTabIndex = i;
                    break;
                }
            }
            
            // 우클릭한 탭의 고정 상태에 따라 메뉴 텍스트 업데이트
            if (lastContextTabIndex >= 0 && lastContextTabIndex < tabControlMain.TabPages.Count)
            {
                var tab = tabControlMain.TabPages[lastContextTabIndex];
                if (tab.Tag is TabTag tag && tabControlMain.ContextMenuStrip != null)
                {
                    // "고정" 항목 찾기 (인덱스 0)
                    if (tabControlMain.ContextMenuStrip.Items.Count > 0 && 
                        tabControlMain.ContextMenuStrip.Items[0] is ToolStripMenuItem pinMenuItem)
                    {
                        pinMenuItem.Text = tag.IsPinned ? "고정 해제" : "고정";
                    }
                }
            }
        }
        
        private void PinItem_Click(object? sender, EventArgs e)
        {
            // 우클릭한 탭의 고정 상태 토글
            if (lastContextTabIndex >= 0 && lastContextTabIndex < tabControlMain.TabPages.Count)
            {
                var tab = tabControlMain.TabPages[lastContextTabIndex];
                if (tab.Tag is TabTag tag)
                {
                    tag.IsPinned = !tag.IsPinned;
                    tabControlMain.Invalidate(); // DrawItem에서 고정 표시 업데이트
                    
                    System.Diagnostics.Debug.WriteLine($"[ContextMenu] Pin toggle: tab={tab.Text}, index={lastContextTabIndex}, IsPinned={tag.IsPinned}");
                    
                    // 정렬 (무한 루프 방지 플래그 사용)
                    SortTabsKeepSelection();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ContextMenu] PinItem_Click: Tab has no Tag");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ContextMenu] PinItem_Click: Invalid tab index {lastContextTabIndex}");
            }
        }


        private void SettingsItem_Click(object? sender, EventArgs e)
        {
            // 설정 창 표시
            using (var settingsForm = new Form())
            {
                settingsForm.Text = "KakaoTab 설정";
                settingsForm.Size = new Size(400, 300);
                settingsForm.StartPosition = FormStartPosition.CenterParent;

                var lblInterval = new Label { Text = "체크 간격 (초):", Top = 20, Left = 20, AutoSize = true };
                var numInterval = new NumericUpDown { Minimum = 1, Maximum = 10, Value = Settings.CheckIntervalSeconds, Top = 20, Left = 150, Width = 100 };
                var lblInactive = new Label { Text = "비활성 시간 임계값 (초):", Top = 60, Left = 20, AutoSize = true };
                var numInactive = new NumericUpDown { Minimum = 60, Maximum = 3600, Value = Settings.InactiveTimeThresholdSeconds, Top = 60, Left = 150, Width = 100 };
                var chkTimestamp = new CheckBox { Text = "타임스탬프 기반 감지 사용", Checked = Settings.UseTimestampBasedDetection, Top = 100, Left = 20, AutoSize = true };
                var chkWindowState = new CheckBox { Text = "창 상태 추적 사용", Checked = Settings.UseWindowStateTracking, Top = 130, Left = 20, AutoSize = true };
                var btnOK = new Button { Text = "확인", Top = 200, Left = 200, Width = 80, DialogResult = DialogResult.OK };
                var btnCancel = new Button { Text = "취소", Top = 200, Left = 290, Width = 80, DialogResult = DialogResult.Cancel };

                settingsForm.Controls.AddRange(new Control[] { lblInterval, numInterval, lblInactive, numInactive, chkTimestamp, chkWindowState, btnOK, btnCancel });
                settingsForm.AcceptButton = btnOK;
                settingsForm.CancelButton = btnCancel;

                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    Settings.CheckIntervalSeconds = (int)numInterval.Value;
                    Settings.InactiveTimeThresholdSeconds = (int)numInactive.Value;
                    Settings.UseTimestampBasedDetection = chkTimestamp.Checked;
                    Settings.UseWindowStateTracking = chkWindowState.Checked;

                    // 타이머 재시작 (간격 변경 반영)
                    if (scanTimer != null)
                    {
                        scanTimer.Stop();
                        StartScanTimer();
                    }
                }
            }
        }

        private void ScanTimer_Tick(object? sender, EventArgs e)
        {
            // 카카오톡 메인 창(대화 목록)에서 빨간 배지 감지
            DetectUnreadBadgesFromMainWindow();
            
            // 요구사항: 활성화된(포커스된) 대화창만 탭으로 추가
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero)
            {
string className = WindowScanner.GetWindowClassName(fgHwnd);
                string title = WindowScanner.GetWindowTitle(fgHwnd);
                if (className == "EVA_Window_Dblclk" && !string.IsNullOrWhiteSpace(title) && title != "카카오톡")
                {
                    if (!knownWindows.Contains(fgHwnd))
                    {
                        var normalized = NormalizeChatTitle(title);
                        var tab = new TabPage(GetShortTitle(normalized.BaseTitle));
                        var newTag = new TabTag 
                        { 
                            Hwnd = fgHwnd, 
                            HasNewActivity = false, 
                            LastCheckedTime = DateTime.Now, 
                            BaseTitle = normalized.BaseTitle,
                            LastActivatedTime = DateTime.Now // 새 탭 추가 시 현재 시간으로 초기화
                        };
                        tab.Tag = newTag;
                        RegisterChatTabPage(tab);
                        tabControlMain.TabPages.Add(tab);
                        knownWindows.Add(fgHwnd);
                        chatStates[fgHwnd] = new ChatWindowState { LastTitle = title, LastUnread = normalized.Unread };
                        tabControlMain.SelectedTab = tab;
                        // 새 탭 추가 시 즉시 임베드 (이벤트만 기다리지 말고 직접 호출)
                        var bounds = tab.ClientRectangle;
                        WindowEmbedder.EmbedWindow(fgHwnd, tab.Handle, bounds);
                        BeginInvoke(FocusSelectedTabContent);
                        
                        // 초기 ROI 해시 설정 (3개 ROI 모두)
                        if (PixelActivityDetector.TryGetRoiHashPack(fgHwnd, RoiKind.Bottom, out var initialBottomPack))
                        {
                            newTag.LastBottomPack = initialBottomPack;
                            newTag.LastRoiHash = initialBottomPack.Hash; // 하위 호환성
                        }
                        if (PixelActivityDetector.TryGetRoiHashPack(fgHwnd, RoiKind.Mid, out var initialMidPack))
                        {
                            newTag.LastMidPack = initialMidPack;
                        }
                        if (PixelActivityDetector.TryGetRoiHashPack(fgHwnd, RoiKind.Aux, out var initialAuxPack))
                        {
                            newTag.LastAuxPack = initialAuxPack;
                        }
                        newTag.LastHashUpdate = DateTime.Now;
                        
                        // 새 탭 추가 후 정렬
                        SortTabsKeepSelection();
                    }
                }
            }

            // 기존 탭: 제목/읽지않음 숫자 변화 감지
            foreach (var tab in tabControlMain.TabPages)
            {
                if (tab is not TabPage tp) continue;
                if (tp.Tag is not TabTag tag) continue;
                var hwnd = tag.Hwnd;
                if (hwnd == IntPtr.Zero) continue;
                if (!WindowEmbedder.IsWindow(hwnd)) continue;

                string currentTitle = WindowScanner.GetWindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(currentTitle)) continue;
                if (currentTitle == "카카오톡") continue;

                var normalized = NormalizeChatTitle(currentTitle);

                if (!chatStates.TryGetValue(hwnd, out var state))
                {
                    state = new ChatWindowState();
                    chatStates[hwnd] = state;
                }

                // BaseTitle 업데이트 (변경된 경우)
                if (tag.BaseTitle != normalized.BaseTitle)
                {
                    tag.BaseTitle = normalized.BaseTitle;
                }
                
                // 탭 제목은 항상 방 이름만 유지 (점 표시는 DrawItem에서 처리)
                string desiredTabText = GetShortTitle(normalized.BaseTitle);
                if (tp.Text != desiredTabText)
                {
                    // "● " 접두사가 있으면 제거
                    string cleanText = tp.Text.StartsWith("● ") ? tp.Text.Substring(2) : tp.Text;
                    if (cleanText != desiredTabText)
                    {
                        tp.Text = desiredTabText;
                    }
                }

                // 현재 보고 있는 탭인지 확인
                IntPtr fg = GetForegroundWindow();
                bool isChatBeingViewed =
                    (fg == hwnd) ||
                    (fg == this.Handle && tabControlMain.SelectedTab == tp);

                // 읽지않음 숫자 찾기 (여러 방법 시도)
                int detectedUnread = 0;
                
                // 방법 1: 제목에서 파싱
                if (normalized.Unread > 0)
                {
                    detectedUnread = normalized.Unread;
                }
                
                // 방법 2: 개별 창에서 직접 찾기 (제목 파싱이 실패한 경우)
                if (detectedUnread == 0 && !isChatBeingViewed)
                {
                    int windowUnread = WindowScanner.FindUnreadCountInWindow(hwnd);
                    if (windowUnread > 0)
                    {
                        detectedUnread = windowUnread;
                    }
                }
                
                // 방법 3: 메인 창의 배지 정보 활용 (DetectUnreadBadgesFromMainWindow에서 이미 처리됨)
                // tag.UnreadCount는 DetectUnreadBadgesFromMainWindow에서 설정될 수 있음
                
                // 최종 읽지않음 숫자 업데이트
                if (detectedUnread > 0)
                {
                    tag.UnreadCount = detectedUnread;
                }
                else if (isChatBeingViewed)
                {
                    // 현재 보고 있는 탭이면 읽지않음 숫자 리셋
                    tag.UnreadCount = 0;
                }

                // 최근 활동 감지: 제목 변화 + 픽셀 변화 감지
                bool hasActivity = false;
                
                // 제목 변화 감지: 읽지않음 숫자가 증가했는지 확인
                if (!isChatBeingViewed)
                {
                    // 읽지않음 숫자가 증가했으면 새 메시지로 간주
                    if (normalized.Unread > state.LastUnread)
                    {
                        hasActivity = true;
                    }
                    // 제목 자체가 바뀌었을 수도 있음 (예: "(1) 방이름" -> "(2) 방이름")
                    else if (state.LastTitle != currentTitle && normalized.Unread > 0)
                    {
                        // 제목이 바뀌었고 읽지않음 숫자가 있으면 새 활동 가능성
                        hasActivity = true;
                    }
                }
                else
                {
                    // 현재 보고 있는 탭이면 확인 시간 업데이트 및 읽지않음 숫자 리셋
                    if (tag != null)
                    {
                        tag.LastCheckedTime = DateTime.Now;
                        tag.IsManuallyMarkedRead = false; // 자동으로 확인했으므로 수동 표시 해제
                        tag.UnreadCount = 0; // 현재 보고 있으면 읽지않음 숫자 리셋
                    }
                }

                // 픽셀 변화 감지 (개선된 버전: 3 ROI 투표 + 평균 밝기 + 쿨다운)
                if (!isChatBeingViewed && tag != null && !tag.IsManuallyMarkedRead)
                {
                    // 쿨다운 체크: 최근 2초 내 활동 감지가 있었으면 스킵
                    if ((DateTime.Now - tag.LastActivityDetectedTime).TotalSeconds < 2.0)
                    {
                        // 쿨다운 중이므로 감지 스킵
                        continue;
                    }

                    const int OnThreshold = 10;      // 감지 시작 임계값
                    const int OffThreshold = 6;       // 안정 임계값
                    const int MeanThreshold = 2;      // 평균 밝기 변화 임계값
                    const int TinyDist = 3;           // 렌더링 중 판별용 작은 거리
                    const int STREAK_THRESHOLD = 2;   // 활동 확정을 위한 streak 임계값
                    const bool DEBUG_PIXEL = true;    // 디버그 모드

                    // 샘플링 안정화: 같은 tick에서 2회 캡처 (60ms 간격)
                    RoiHashPack? pack1 = null;
                    RoiHashPack? pack2 = null;

                    if (PixelActivityDetector.TryGetRoiHashPack(hwnd, RoiKind.Bottom, out var tempPack1))
                    {
                        pack1 = tempPack1;
                        System.Threading.Thread.Sleep(60); // 60ms 대기
                        if (PixelActivityDetector.TryGetRoiHashPack(hwnd, RoiKind.Bottom, out var tempPack2))
                        {
                            pack2 = tempPack2;
                            
                            // 두 해시가 서로 많이 다르면 "렌더링 중"으로 판단하고 스킵
                            int distBetween = PixelActivityDetector.HammingDistance(pack1.Value.Hash, pack2.Value.Hash);
                            if (distBetween > TinyDist)
                            {
                                // unstable tick skip (5초에 1번만 로그)
                                if (DEBUG_PIXEL && (DateTime.Now - lastUnstableLog).TotalSeconds >= 5.0)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[Pixel] Unstable rendering detected, skipping tick: tab={tp.Text}, dist={distBetween}");
                                    lastUnstableLog = DateTime.Now;
                                }
                                continue; // 이 tick 스킵
                            }
                        }
                    }

                    // 안정적인 캡처가 없으면 스킵
                    if (!pack1.HasValue)
                        continue;

                    // 3 ROI 투표: Bottom, Mid, Aux
                    int changedCount = 0;
                    int distB = 0, distM = 0, distA = 0;
                    int meanB = 0, meanM = 0, meanA = 0;

                    // Bottom ROI
                    if (PixelActivityDetector.TryGetRoiHashPack(hwnd, RoiKind.Bottom, out var currentBottomPack))
                    {
                        if (tag.LastBottomPack.Hash != 0) // 초기값이 아닐 때만 비교
                        {
                            distB = PixelActivityDetector.HammingDistance(tag.LastBottomPack.Hash, currentBottomPack.Hash);
                            meanB = Math.Abs(currentBottomPack.Mean - tag.LastBottomPack.Mean);
                            
                            if (distB >= OnThreshold && meanB >= MeanThreshold)
                            {
                                changedCount++;
                            }
                        }
                    }

                    // Mid ROI
                    if (PixelActivityDetector.TryGetRoiHashPack(hwnd, RoiKind.Mid, out var currentMidPack))
                    {
                        if (tag.LastMidPack.Hash != 0)
                        {
                            distM = PixelActivityDetector.HammingDistance(tag.LastMidPack.Hash, currentMidPack.Hash);
                            meanM = Math.Abs(currentMidPack.Mean - tag.LastMidPack.Mean);
                            
                            if (distM >= OnThreshold && meanM >= MeanThreshold)
                            {
                                changedCount++;
                            }
                        }
                    }

                    // Aux ROI
                    if (PixelActivityDetector.TryGetRoiHashPack(hwnd, RoiKind.Aux, out var currentAuxPack))
                    {
                        if (tag.LastAuxPack.Hash != 0)
                        {
                            distA = PixelActivityDetector.HammingDistance(tag.LastAuxPack.Hash, currentAuxPack.Hash);
                            meanA = Math.Abs(currentAuxPack.Mean - tag.LastAuxPack.Mean);
                            
                            if (distA >= OnThreshold && meanA >= MeanThreshold)
                            {
                                changedCount++;
                            }
                        }
                    }

                    // 투표 기반 결정: 3개 중 2개 이상이 변화했을 때만 활동으로 판정
                    if (changedCount >= 2)
                    {
                        // 활동 감지: streak 증가
                        tag.ActivityStreak++;
                        
                        // 연속 2회 이상 변화 시 활동으로 확정
                        if (tag.ActivityStreak >= STREAK_THRESHOLD)
                        {
                            if (DEBUG_PIXEL)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Pixel] Activity: tab={tp.Text}, distB={distB} meanB={meanB} distM={distM} meanM={meanM} distA={distA} meanA={meanA}");
                            }
                            
                            hasActivity = true;
                            tag.HasNewActivity = true;
                            tag.LastActivityDetectedTime = DateTime.Now;
                            tag.ActivityStreak = 0; // streak 리셋
                        }
                    }
                    else
                    {
                        // 변화 없음: streak 빠르게 리셋
                        tag.ActivityStreak = 0;
                    }

                    // 기준값 업데이트 (항상)
                    if (currentBottomPack.Hash != 0)
                    {
                        tag.LastBottomPack = currentBottomPack;
                    }
                    if (currentMidPack.Hash != 0)
                    {
                        tag.LastMidPack = currentMidPack;
                    }
                    if (currentAuxPack.Hash != 0)
                    {
                        tag.LastAuxPack = currentAuxPack;
                    }
                    
                    // 하위 호환성: LastRoiHash도 업데이트
                    tag.LastRoiHash = currentBottomPack.Hash;
                    tag.LastHashUpdate = DateTime.Now;
                }

                // 아이디어 3: 창 상태 추적 (최소화/복원)
                if (Settings.UseWindowStateTracking)
                {
                    bool isCurrentlyMinimized = IsIconic(hwnd);
                    if (state.WasMinimized && !isCurrentlyMinimized)
                    {
                        // 창이 최소화에서 복원되었으면 새 메시지 가능성
                        if (!isChatBeingViewed)
                        {
                            hasActivity = true;
                        }
                    }
                    state.WasMinimized = isCurrentlyMinimized;
                }

                // 아이디어 1: 타임스탬프 기반 감지 (설정에서 켜져있을 때만)
                if (Settings.UseTimestampBasedDetection && tag != null && !tag.IsManuallyMarkedRead)
                {
                    TimeSpan timeSinceLastCheck = DateTime.Now - tag.LastCheckedTime;
                    if (timeSinceLastCheck.TotalSeconds > Settings.InactiveTimeThresholdSeconds)
                    {
                        // 일정 시간 이상 확인하지 않았고, 읽지않음 숫자가 있으면 새 메시지 가능성
                        if (normalized.Unread > 0)
                        {
                            hasActivity = true;
                        }
                    }
                }

                // TabTag에 최근 활동 플래그 설정 (더 간단하고 확실하게)
                if (tp.Tag is TabTag tabTag)
                {
                    // 수동으로 읽음 표시된 경우는 활동 플래그 설정하지 않음
                    if (tabTag.IsManuallyMarkedRead)
                    {
                        // 수동 표시된 경우는 무시
                    }
                    else if (isChatBeingViewed)
                    {
                        // 현재 보고 있는 탭이면 활동 플래그 및 읽지않음 숫자 리셋
                        tabTag.HasNewActivity = false;
                        tabTag.UnreadCount = 0;
                    }
                    else if (hasActivity)
                    {
                        // 활동이 감지되면 무조건 표시
                        tabTag.HasNewActivity = true;
                    }
                    // hasActivity가 false여도 기존 HasNewActivity는 유지 (한번 표시되면 계속)
                }
                else
                {
                    tp.Tag = new TabTag 
                    { 
                        Hwnd = hwnd, 
                        HasNewActivity = hasActivity && !isChatBeingViewed,
                        LastCheckedTime = DateTime.Now
                    };
                }

                state.LastTitle = currentTitle;
                state.LastUnread = normalized.Unread;
                
                // 탭 다시 그리기 (활동 표시 업데이트) - 항상 호출
                tabControlMain.Invalidate();
            }

            // 창이 닫힌 탭 자동 제거
            for (int i = tabControlMain.TabPages.Count - 1; i >= 0; i--)
            {
                var tab = tabControlMain.TabPages[i];
                IntPtr hwnd = IntPtr.Zero;
                if (tab.Tag is TabTag tag)
                    hwnd = tag.Hwnd;
                else if (tab.Tag is IntPtr h)
                    hwnd = h;
                if (hwnd != IntPtr.Zero && !WindowEmbedder.IsWindow(hwnd))
                {
                    tabControlMain.TabPages.RemoveAt(i);
                    knownWindows.Remove(hwnd);
                    chatStates.Remove(hwnd);
                }
            }
        }

        /// <summary>
        /// 문자열 정규화 (공백 제거, 다중 공백 축약)
        /// </summary>
        private static string NormalizeString(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;
            
            // Trim 및 다중 공백을 단일 공백으로 변환
            return Regex.Replace(input.Trim(), @"\s+", " ");
        }

        /// <summary>
        /// 두 문자열이 매칭되는지 확인 (완전일치 우선 → 포함매칭)
        /// </summary>
        private static bool IsStringMatch(string baseTitle, string chatName)
        {
            if (string.IsNullOrWhiteSpace(baseTitle) || string.IsNullOrWhiteSpace(chatName))
                return false;

            string normalizedBase = NormalizeString(baseTitle);
            string normalizedChat = NormalizeString(chatName);

            // 완전일치
            if (normalizedBase.Equals(normalizedChat, StringComparison.OrdinalIgnoreCase))
                return true;

            // 포함매칭
            if (normalizedBase.Contains(normalizedChat, StringComparison.OrdinalIgnoreCase) ||
                normalizedChat.Contains(normalizedBase, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// 카카오톡 메인 창(대화 목록)에서 빨간 배지 감지하여 탭에 알림 표시
        /// </summary>
        private void DetectUnreadBadgesFromMainWindow()
        {
            // 카카오톡 메인 창 찾기 (제목이 "카카오톡"인 창)
            List<IntPtr> kakaoWindows = WindowScanner.FindKakaoTalkMainWindows();
            
            // 모든 탭의 배지 상태 초기화 (배지가 사라진 경우를 위해)
            HashSet<string> badgesFoundForTabs = new HashSet<string>();
            
            foreach (var mainHwnd in kakaoWindows)
            {
                List<WindowScanner.ChatBadgeInfo> chatBadges = new List<WindowScanner.ChatBadgeInfo>();
                
                // UI Automation 우선 시도 (.NET 9에서는 현재 사용 불가)
                // TODO: .NET 9에서 UI Automation을 사용하려면 P/Invoke를 통해 COM 인터페이스를 직접 호출해야 합니다.
                try
                {
                    var uiaBadges = WindowScanner.FindUnreadBadgesInMainWindow_UIA(mainHwnd);
                    if (uiaBadges != null && uiaBadges.Count > 0)
                    {
                        chatBadges = uiaBadges;
                        System.Diagnostics.Debug.WriteLine($"[UIA] Successfully found {uiaBadges.Count} badges using UI Automation");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[UIA] UI Automation returned 0 badges, falling back to Win32");
                        // Fallback to Win32
                        chatBadges = WindowScanner.FindUnreadBadgesInMainWindow(mainHwnd);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UIA] UI Automation failed: {ex.Message}, falling back to Win32");
                    // Fallback to Win32
                    chatBadges = WindowScanner.FindUnreadBadgesInMainWindow(mainHwnd);
                }
                
                foreach (var badge in chatBadges)
                {
                    // 대화방 이름으로 탭 찾기
                    string chatName = badge.ChatName;
                    int unreadCount = badge.UnreadCount;
                    
                    // 탭에서 해당 대화방 찾기 (BaseTitle로 매칭, 정규화된 비교)
                    foreach (TabPage tab in tabControlMain.TabPages)
                    {
                        if (tab.Tag is TabTag tag && !string.IsNullOrEmpty(tag.BaseTitle))
                        {
                            // 정규화된 문자열로 매칭 (완전일치 우선 → 포함매칭)
                            if (IsStringMatch(tag.BaseTitle, chatName))
                            {
                                badgesFoundForTabs.Add(tag.BaseTitle);
                                
                                // 읽지않음 숫자 업데이트 (메인 창의 배지 정보가 더 정확할 수 있음)
                                // HasNewActivity는 절대 건드리지 않음 (다른 감지 방식에서 설정됨)
                                if (unreadCount > 0 && !tag.IsManuallyMarkedRead)
                                {
                                    tag.UnreadCount = unreadCount;
                                    // HasNewActivity는 여기서 설정하지 않음 (픽셀 감지/Toast 감지에서만 설정)
                                }
                            }
                        }
                    }
                }
            }
            
            // 배지가 사라진 탭에서 UnreadCount만 업데이트
            // HasNewActivity는 절대 건드리지 않음 (사용자가 탭을 선택하기 전까지 유지)
            foreach (TabPage tab in tabControlMain.TabPages)
            {
                if (tab.Tag is TabTag tag && !string.IsNullOrEmpty(tag.BaseTitle))
                {
                    // 배지가 없으면 UnreadCount만 0으로 설정
                    if (!badgesFoundForTabs.Contains(tag.BaseTitle))
                    {
                        tag.UnreadCount = 0;
                        // HasNewActivity는 리셋하지 않음
                        // tab.Text도 변경하지 않음 (DrawItem에서 처리)
                    }
                }
            }
        }

        private static readonly Regex UnreadPrefixRegex = new Regex(@"^\s*[\(\[]\s*(\d+)\s*[\)\]]\s*(.*)$", RegexOptions.Compiled);

        private (string BaseTitle, int Unread) NormalizeChatTitle(string rawTitle)
        {
            // 카카오톡 대화방 제목이 "(3) 방이름" 또는 "[3] 방이름" 형태로 변하는 경우가 많아서 이를 파싱
            var m = UnreadPrefixRegex.Match(rawTitle ?? string.Empty);
            if (m.Success)
            {
                int unread = 0;
                int.TryParse(m.Groups[1].Value, out unread);
                var baseTitle = m.Groups[2].Value?.Trim();
                if (string.IsNullOrWhiteSpace(baseTitle))
                    baseTitle = rawTitle.Trim();
                return (baseTitle, unread);
            }
            return (rawTitle.Trim(), 0);
        }

        /// <summary>
        /// Windows 알림 리스너 초기화
        /// </summary>
        /*
        /// <summary>
        /// Windows 알림 리스너 초기화 (WinRT - .NET 9에서 사용 불가, 주석 처리)
        /// </summary>
        private async void InitializeNotificationListener()
        {
            try
            {
                notificationListener = UserNotificationListener.Current;
                
                // 알림 접근 권한 요청
                var accessStatus = await notificationListener.RequestAccessAsync();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    // 권한이 없으면 알림 감지 불가
                    System.Diagnostics.Debug.WriteLine($"알림 접근 권한 없음: {accessStatus}");
                    return;
                }

                // 알림 변경 이벤트 구독
                notificationListener.NotificationChanged += NotificationListener_NotificationChanged;
                
                System.Diagnostics.Debug.WriteLine("Windows 알림 리스너 초기화 완료");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"알림 리스너 초기화 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// Windows 알림 변경 이벤트 핸들러 (WinRT - .NET 9에서 사용 불가, 주석 처리)
        /// </summary>
        private async void NotificationListener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            try
            {
                // 새 알림이 추가된 경우만 처리
                if (args.ChangeKind != UserNotificationChangedKind.Added)
                    return;

                // 알림 정보 가져오기
                var notification = await sender.GetNotificationAsync(args.UserNotificationId);
                if (notification == null) return;

                // 카카오톡 알림인지 확인
                string appId = notification.AppInfo.AppId;
                if (!appId.Contains("KakaoTalk", StringComparison.OrdinalIgnoreCase) &&
                    !appId.Contains("카카오톡", StringComparison.OrdinalIgnoreCase))
                {
                    return; // 카카오톡 알림이 아님
                }

                // 알림 내용 파싱
                string notificationText = ExtractNotificationText(notification);
                string? chatRoomName = ExtractChatRoomName(notificationText);

                if (!string.IsNullOrEmpty(chatRoomName))
                {
                    // UI 스레드에서 실행
                    this.Invoke((MethodInvoker)delegate
                    {
                        // 해당 대화방 탭 찾기
                        foreach (TabPage tab in tabControlMain.TabPages)
                        {
                            if (tab.Tag is TabTag tag)
                            {
                                // 탭 제목과 대화방 이름 비교
                                if (tab.Text.Contains(chatRoomName) || chatRoomName.Contains(tab.Text))
                                {
                                    // 빨간 점 표시
                                    if (!tag.IsManuallyMarkedRead)
                                    {
                                        tag.HasNewActivity = true;
                                        tabControlMain.Invalidate();
                                    }
                                    break;
                                }
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"알림 처리 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 알림에서 텍스트 추출 (WinRT - .NET 9에서 사용 불가, 주석 처리)
        /// </summary>
        private string ExtractNotificationText(UserNotification notification)
        {
            var sb = new StringBuilder();
            
            try
            {
                var binding = notification.Notification.Visual?.Binding;
                if (binding != null)
                {
                    // Toast 텍스트 추출
                    if (binding is ToastBinding toastBinding)
                    {
                        if (toastBinding.GetTextElements() != null)
                        {
                            foreach (var element in toastBinding.GetTextElements())
                            {
                                if (!string.IsNullOrEmpty(element.Text))
                                {
                                    sb.AppendLine(element.Text);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"알림 텍스트 추출 오류: {ex.Message}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 알림 텍스트에서 대화방 이름 추출 (WinRT - .NET 9에서 사용 불가, 주석 처리)
        /// </summary>
        private string? ExtractChatRoomName(string notificationText)
        {
            if (string.IsNullOrWhiteSpace(notificationText))
                return null;

            // 알림 형식 예시:
            // "방이름\n메시지 내용"
            // "방이름: 메시지 내용"
            // "방이름\n오후 8:36"
            
            var lines = notificationText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                string firstLine = lines[0].Trim();
                
                // 시간 패턴 제거 (예: "오전 8:36", "PM 8:36")
                if (Regex.IsMatch(firstLine, @"오전|오후|AM|PM|\d{1,2}:\d{2}"))
                {
                    // 첫 줄이 시간이면 두 번째 줄 사용
                    if (lines.Length > 1)
                        return lines[1].Trim();
                }
                else
                {
                    // 콜론 뒤 제거 (예: "방이름: 메시지")
                    int colonIndex = firstLine.IndexOf(':');
                    if (colonIndex > 0)
                        return firstLine.Substring(0, colonIndex).Trim();
                    
                    return firstLine;
                }
            }

            return null;
        }
        */

        // 트레이 알림 기능 제거: 탭 배지로만 알림 표시
        // private void ShowMessageNotification(string baseTitle, int unread)
        // {
        //     if (notifyIcon == null) return;
        //     notifyIcon.BalloonTipTitle = "새 카카오톡 메세지";
        //     notifyIcon.BalloonTipText = unread > 0
        //         ? $"{baseTitle} (읽지않음 {unread})"
        //         : $"{baseTitle} 에서 새 메세지가 감지되었습니다.";
        //     notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        //     notifyIcon.ShowBalloonTip(3000);
        // }

        private void Form1_Resize(object? sender, EventArgs e)
        {
            var tabPage = tabControlMain.SelectedTab;
            if (tabPage != null)
            {
                var tag = tabPage.Tag as TabTag;
                // 웹탭은 리사이즈 로직에서 제외 (WebView2가 자동으로 처리)
                if (tag != null && !tag.IsWebTab && tag.Hwnd != IntPtr.Zero)
                {
                    var bounds = tabPage.ClientRectangle;
                    WindowEmbedder.ResizeWindow(tag.Hwnd, bounds);
                }
            }
        }

        private void TabControlMain_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // 무한 루프 방지: 정렬 중이면 정렬 트리거하지 않음
            if (isSortingTabs)
                return;
            
            // 선택된 탭 확인
            var selectedTab = tabControlMain.SelectedTab;
            bool isWebTab = false;
            
            if (selectedTab != null && selectedTab.Tag is TabTag selectedTag)
            {
                isWebTab = selectedTag.IsWebTab;
                // 웹뷰 새로고침 버튼 표시/숨김
                if (btnRefreshWebView != null)
                {
                    btnRefreshWebView.Visible = isWebTab;
                }
            }
            else
            {
                if (btnRefreshWebView != null)
                {
                    btnRefreshWebView.Visible = false;
                }
            }
            
            for (int i = 0; i < tabControlMain.TabPages.Count; i++)
            {
                var tab = tabControlMain.TabPages[i];
                var tag = tab.Tag as TabTag;
                // 웹탭은 카카오톡 임베딩 로직에서 제외
                if (tag == null || tag.IsWebTab || tag.Hwnd == IntPtr.Zero) continue;
                var bounds = tab.ClientRectangle;
                if (i == tabControlMain.SelectedIndex)
                {
                    WindowEmbedder.EmbedWindow(tag.Hwnd, tab.Handle, bounds);
                    // 선택된 탭의 활동 플래그 리셋 및 확인 시간 업데이트
                    tag.HasNewActivity = false;
                    tag.UnreadCount = 0; // 읽지않음 숫자 리셋
                    tag.LastCheckedTime = DateTime.Now;
                    tag.IsManuallyMarkedRead = false; // 자동으로 확인했으므로 수동 표시 해제
                    tag.ActivityStreak = 0; // streak 리셋
                    tag.HashChangeStreak = 0; // 하위 호환성
                    tag.LastActivityDetectedTime = DateTime.MinValue; // 쿨다운 리셋
                    
                    // LastActivatedTime 갱신
                    tag.LastActivatedTime = DateTime.Now;
                    
                    // ROI 기준값 재설정 (3개 ROI 모두)
                    if (PixelActivityDetector.TryGetRoiHashPack(tag.Hwnd, RoiKind.Bottom, out var bottomPack))
                    {
                        tag.LastBottomPack = bottomPack;
                        tag.LastRoiHash = bottomPack.Hash; // 하위 호환성
                    }
                    if (PixelActivityDetector.TryGetRoiHashPack(tag.Hwnd, RoiKind.Mid, out var midPack))
                    {
                        tag.LastMidPack = midPack;
                    }
                    if (PixelActivityDetector.TryGetRoiHashPack(tag.Hwnd, RoiKind.Aux, out var auxPack))
                    {
                        tag.LastAuxPack = auxPack;
                    }
                    
                    tag.LastHashUpdate = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"[Pixel] ROI reset for selected tab: {tab.Text}");
                    
                    tabControlMain.Invalidate();
                }
                else
                {
                    WindowEmbedder.HideWindow(tag.Hwnd);
                }
            }
            
            // 탭 클릭 시 자동 정렬 제거 (사용자 요청: 탭 위치 유지)
            // SortTabsKeepSelection();

            BeginInvoke(FocusSelectedTabContent);
        }

        private void RegisterChatTabPage(TabPage tab)
        {
            tab.MouseDown += ChatTabPage_MouseDown;
        }

        private void ChatTabPage_MouseDown(object? sender, MouseEventArgs e)
        {
            if (sender is not TabPage tab || tab.Tag is not TabTag tag) return;
            if (tag.IsWebTab || tag.Hwnd == IntPtr.Zero) return;
            WindowEmbedder.FocusWindow(tag.Hwnd);
        }

        private void FocusSelectedTabContent()
        {
            var selectedTab = tabControlMain.SelectedTab;
            if (selectedTab?.Tag is not TabTag tag) return;

            if (tag.IsWebTab)
            {
                if (!string.IsNullOrEmpty(tag.WebViewKey) &&
                    webViews.TryGetValue(tag.WebViewKey, out var webView))
                {
                    webView.Focus();
                }
                return;
            }

            if (tag.Hwnd != IntPtr.Zero)
            {
                WindowEmbedder.FocusWindow(tag.Hwnd);
            }
        }
        
        /// <summary>
        /// 탭을 정렬하되 현재 선택된 탭은 유지
        /// </summary>
        private void SortTabsKeepSelection()
        {
            if (isSortingTabs)
                return; // 이미 정렬 중이면 무시
            
            isSortingTabs = true;
            try
            {
                // 현재 선택된 탭의 참조 보관
                TabPage? selectedTab = tabControlMain.SelectedTab;
                IntPtr selectedHwnd = IntPtr.Zero;
                if (selectedTab != null && selectedTab.Tag is TabTag selectedTag)
                {
                    selectedHwnd = selectedTag.Hwnd;
                }
                
                // TabPages를 List로 복사
                List<TabPage> tabs = new List<TabPage>();
                foreach (TabPage tab in tabControlMain.TabPages)
                {
                    tabs.Add(tab);
                }
                
                // 정렬 규칙:
                // 1) IsPinned 내림차순 (true 먼저)
                // 2) LastActivatedTime 내림차순
                // 3) BaseTitle(없으면 tab.Text) 오름차순
                var sortedTabs = tabs.OrderByDescending(tab =>
                {
                    if (tab.Tag is TabTag tag)
                        return tag.IsPinned;
                    return false;
                }).ThenByDescending(tab =>
                {
                    if (tab.Tag is TabTag tag)
                        return tag.LastActivatedTime;
                    return DateTime.MinValue;
                }).ThenBy(tab =>
                {
                    if (tab.Tag is TabTag tag && !string.IsNullOrEmpty(tag.BaseTitle))
                        return tag.BaseTitle;
                    return tab.Text ?? string.Empty;
                }, StringComparer.CurrentCultureIgnoreCase).ToList();
                
                // TabPages.Clear 후 AddRange
                tabControlMain.TabPages.Clear();
                tabControlMain.TabPages.AddRange(sortedTabs.ToArray());
                
                // 기존 선택 탭을 다시 SelectedTab으로 복원
                if (selectedHwnd != IntPtr.Zero)
                {
                    foreach (TabPage tab in tabControlMain.TabPages)
                    {
                        if (tab.Tag is TabTag tag && tag.Hwnd == selectedHwnd)
                        {
                            tabControlMain.SelectedTab = tab;
                            break;
                        }
                    }
                }
            }
            finally
            {
                isSortingTabs = false;
            }
        }

        // Form1_DragEnter 주석 처리
        // private void Form1_DragEnter(object? sender, DragEventArgs e)
        // {
        //     // 자동 감지 OFF일 때만 동작
        //     if (!chkAutoDetect.Checked)
        //     {
        //         e.Effect = DragDropEffects.Copy;
        //     }
        // }

        // Form1_DragDrop 주석 처리
        // private void Form1_DragDrop(object? sender, DragEventArgs e)
        // {
        //     // 자동 감지 OFF일 때만 동작
        //     if (!chkAutoDetect.Checked)
        //     {
        //         // 마우스 위치의 윈도우 핸들 감지
        //         Point screenPoint = Cursor.Position;
        //         IntPtr hwnd = WindowScanner.WindowFromPoint(screenPoint);
        //         // 클래스명 확인
        //         string className = WindowScanner.GetWindowClassName(hwnd);
        //         string title = WindowScanner.GetWindowTitle(hwnd);
        //         if (className == "EVA_Window_Dblclk" && !string.IsNullOrWhiteSpace(title) && title != "카카오톡")
        //         {
        //             if (!knownWindows.Contains(hwnd))
        //             {
        //                 var tab = new TabPage(title);
        //                 tab.Tag = new TabTag { Hwnd = hwnd };
        //                 tabControlMain.TabPages.Add(tab);
        //                 knownWindows.Add(hwnd);
        //                 tabControlMain.SelectedTab = tab;
        //             }
        //         }
        //     }
        // }

        private string GetShortTitle(string title, int maxLen = 11)
        {
            if (title.Length > maxLen)
                return title.Substring(0, maxLen) + "...";
            return title;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd); // 창이 최소화되었는지 확인

        private void FocusTimer_Tick(object? sender, EventArgs e)
        {
            IntPtr fgHwnd = GetForegroundWindow();
            // KakaoTab이 관리하는 대화방 중에 포커스된 창이 있는지 확인
            for (int i = 0; i < tabControlMain.TabPages.Count; i++)
            {
                var tab = tabControlMain.TabPages[i];
                if (tab.Tag is TabTag tag && tag.Hwnd == fgHwnd)
                {
                    if (tabControlMain.SelectedTab != tab)
                        tabControlMain.SelectedTab = tab;
                    break;
                }
            }
        }

        /// <summary>
        /// AI 탭 생성 (비동기)
        /// </summary>
        private async System.Threading.Tasks.Task CreateWebTabsAsync()
        {
            try
            {
                await CreateWebTab("ChatGPT", "https://chatgpt.com/", "ChatGPT");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebView] CreateWebTabsAsync failed: {ex.Message}");
                MessageBox.Show(
                    "AI 탭을 생성하는 중 오류가 발생했습니다.\n\n" +
                    "WebView2 런타임이 설치되어 있는지 확인하세요.\n" +
                    "설치: https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    "KakaoTab - WebView2 오류",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// WebView2 탭 생성
        /// </summary>
        private async System.Threading.Tasks.Task CreateWebTab(string tabTitle, string url, string webViewKey)
        {
            try
            {
                // TabPage 생성
                var tab = new TabPage(tabTitle);
                var tag = new TabTag
                {
                    IsWebTab = true,
                    WebViewKey = webViewKey,
                    IsPinned = true, // AI 탭은 기본 고정
                    Hwnd = IntPtr.Zero // 웹탭은 Hwnd 없음
                };
                tab.Tag = tag;

                // WebView2 생성
                var webView = new WebView2();
                webView.Dock = DockStyle.Fill;
                tab.Controls.Add(webView);

                // UserDataFolder 경로 설정 (세션 유지용)
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userDataFolder = Path.Combine(localAppData, "KakaoTab", "WebView2", webViewKey);

                // CoreWebView2Environment 생성
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                
                // CoreWebView2 초기화
                await webView.EnsureCoreWebView2Async(env);

                // URL 로드
                webView.CoreWebView2?.Navigate(url);

                // WebView2를 딕셔너리에 저장
                webViews[webViewKey] = webView;

                // 탭을 맨 앞에 추가 (고정 탭이므로)
                tabControlMain.TabPages.Insert(0, tab);

                System.Diagnostics.Debug.WriteLine($"[WebView] Created tab: {tabTitle}, URL: {url}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebView] CreateWebTab failed for {webViewKey}: {ex.Message}");
                throw; // 상위로 전달하여 사용자에게 알림
            }
        }

        /// <summary>
        /// 웹뷰 새로고침 버튼 클릭
        /// </summary>
        private void BtnRefreshWebView_Click(object? sender, EventArgs e)
        {
            var selectedTab = tabControlMain.SelectedTab;
            if (selectedTab != null && selectedTab.Tag is TabTag tag && tag.IsWebTab)
            {
                if (webViews.TryGetValue(tag.WebViewKey, out var webView))
                {
                    webView.CoreWebView2?.Reload();
                    System.Diagnostics.Debug.WriteLine($"[WebView] Reloaded: {tag.WebViewKey}");
                }
            }
        }
    }

    public static class WindowScanner
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(System.Drawing.Point pt);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static string GetProcessName(IntPtr hWnd)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return string.Empty;
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName ?? string.Empty; // 보통 "KakaoTalk"
            }
            catch
            {
                return string.Empty;
            }
        }

        public static List<(IntPtr, string, string)> FindAllWindowsWithClass()
        {
            List<(IntPtr, string, string)> result = new List<(IntPtr, string, string)>();
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                int length = GetWindowTextLength(hWnd);
                StringBuilder titleBuilder = new StringBuilder(length + 1);
                GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                string title = titleBuilder.ToString();

                StringBuilder classBuilder = new StringBuilder(256);
                GetClassName(hWnd, classBuilder, classBuilder.Capacity);
                string className = classBuilder.ToString();

                if (!string.IsNullOrWhiteSpace(title))
                {
                    result.Add((hWnd, title, className));
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static List<IntPtr> FindKakaoTalkChatWindows()
        {
            List<IntPtr> result = new List<IntPtr>();
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;
                StringBuilder builder = new StringBuilder(length + 1);
                GetWindowText(hWnd, builder, builder.Capacity);
                string title = builder.ToString();
                // "카카오톡"이 포함된 모든 창을 감지
                if (!string.IsNullOrWhiteSpace(title) && title.Contains("카카오톡"))
                {
                    result.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// 카카오톡 메인 창(대화 목록) 찾기
        /// </summary>
        public static List<IntPtr> FindKakaoTalkMainWindows()
        {
            List<IntPtr> result = new List<IntPtr>();
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                string className = GetWindowClassName(hWnd);
                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;
                StringBuilder builder = new StringBuilder(length + 1);
                GetWindowText(hWnd, builder, builder.Capacity);
                string title = builder.ToString();
                
                // 메인 창: 제목이 "카카오톡"이고 클래스명이 "EVA_Window_Dblclk"인 경우
                if (title == "카카오톡" && className == "EVA_Window_Dblclk")
                {
                    result.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// 대화방 이름과 읽지않음 숫자
        /// </summary>
        public class ChatBadgeInfo
        {
            public string ChatName { get; set; } = string.Empty;
            public int UnreadCount { get; set; } = 0;
        }

        /// <summary>
        /// 메인 창에서 빨간 배지(읽지않음 숫자)와 대화방 이름 찾기
        /// </summary>
        public static List<ChatBadgeInfo> FindUnreadBadgesInMainWindow(IntPtr mainHwnd)
        {
            List<ChatBadgeInfo> badges = new List<ChatBadgeInfo>();
            List<(string Text, string ClassName, WindowScanner.RECT Rect)> allItems = new List<(string, string, WindowScanner.RECT)>();
            
            // 모든 자식 윈도우 텍스트 수집 (메인 창 기준 상대 좌표로 변환)
            CollectAllChildTexts(mainHwnd, allItems, 8, 0, mainHwnd);
            
            // 클라이언트 영역 크기 (상대 좌표 기준이므로 0,0부터 시작)
            WindowScanner.RECT clientRect = new WindowScanner.RECT();
            GetClientRect(mainHwnd, out clientRect);
            int clientHeight = clientRect.Height;
            int clientWidth = clientRect.Width;
            
            // 대화 목록 영역: 상단 10% 제외, 하단 10% 제외, 클라이언트 영역 내부만
            var chatListItems = allItems.Where(item => 
                item.Rect.Top >= 0 && item.Rect.Top < clientHeight && // 클라이언트 영역 내부
                item.Rect.Left >= 0 && item.Rect.Left < clientWidth &&
                item.Rect.Top > clientHeight * 0.1 && 
                item.Rect.Top < clientHeight * 0.9 &&
                !string.IsNullOrWhiteSpace(item.Text)
            ).OrderBy(item => item.Rect.Top).ToList();
            
            // 배지 패턴 찾기: 숫자만 있는 작은 텍스트 (1-99)
            var badgeItems = chatListItems.Where(item => 
                System.Text.RegularExpressions.Regex.IsMatch(item.Text, @"^\d{1,2}$") &&
                int.TryParse(item.Text, out int num) && num > 0 && num <= 99
            ).ToList();
            
            // 각 배지 근처의 대화방 이름 찾기
            foreach (var badgeItem in badgeItems)
            {
                // 배지 위쪽이나 왼쪽에 있는 텍스트를 대화방 이름으로 간주
                var nearbyNames = chatListItems.Where(item =>
                    item.Rect.Top >= badgeItem.Rect.Top - 30 && // 배지 위 30픽셀 이내
                    item.Rect.Top <= badgeItem.Rect.Top + 10 && // 배지 아래 10픽셀 이내
                    item.Rect.Left < badgeItem.Rect.Left && // 배지 왼쪽
                    item.Text.Length > 1 && // 너무 짧지 않음
                    !System.Text.RegularExpressions.Regex.IsMatch(item.Text, @"^\d+$") && // 숫자만 있는 것은 제외
                    !item.Text.Contains("오전") && !item.Text.Contains("오후") // 시간 제외
                ).OrderByDescending(item => item.Rect.Left).Take(1).ToList(); // 가장 오른쪽(가까운) 것
                
                if (nearbyNames.Count > 0)
                {
                    string chatName = nearbyNames[0].Text.Trim();
                    int unreadCount = int.Parse(badgeItem.Text);
                    
                    // 중복 제거
                    if (!badges.Any(b => b.ChatName == chatName))
                    {
                        badges.Add(new ChatBadgeInfo { ChatName = chatName, UnreadCount = unreadCount });
                    }
                }
            }
            
            return badges;
        }

        /// <summary>
        /// UI Automation을 사용하여 메인 창에서 빨간 배지(읽지않음 숫자)와 대화방 이름 찾기
        /// 주의: .NET 9에서는 System.Windows.Automation을 직접 사용할 수 없으므로,
        /// 이 함수는 현재 사용되지 않습니다. Win32 방식으로 fallback합니다.
        /// </summary>
        public static List<ChatBadgeInfo> FindUnreadBadgesInMainWindow_UIA(IntPtr mainHwnd)
        {
            List<ChatBadgeInfo> badges = new List<ChatBadgeInfo>();
            
            // .NET 9에서는 System.Windows.Automation을 직접 사용할 수 없으므로
            // 이 함수는 현재 비활성화되어 있습니다.
            System.Diagnostics.Debug.WriteLine("[UIA] UI Automation is not available in .NET 9, using Win32 fallback");
            return badges;
            
            /* 주석 처리: .NET 9에서 System.Windows.Automation 사용 불가
            try
            {
                // AutomationElement 루트 생성
                AutomationElement root = AutomationElement.FromHandle(mainHwnd);
                if (root == null)
                {
                    System.Diagnostics.Debug.WriteLine("[UIA] AutomationElement.FromHandle failed for hwnd=" + mainHwnd);
                    return badges;
                }

                // 대화목록 컨테이너 찾기 (List, ListBox, 또는 ScrollViewer 등)
                // 카카오톡의 경우 대화목록이 여러 형태일 수 있으므로 다양한 패턴 시도
                AutomationElement? listContainer = null;
                
                // 방법 1: List 컨트롤 타입 찾기
                listContainer = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)
                );
                
                // 방법 2: ListBox 찾기
                if (listContainer == null)
                {
                    listContainer = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)
                    )?.GetParent();
                }
                
                // 방법 3: ScrollViewer 내부의 항목들 찾기
                if (listContainer == null)
                {
                    var scrollViewer = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)
                    );
                    if (scrollViewer != null)
                    {
                        listContainer = scrollViewer;
                    }
                }
                
                // 방법 4: 루트에서 직접 ListItem들을 찾기
                AutomationElementCollection? listItems = null;
                if (listContainer != null)
                {
                    listItems = listContainer.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)
                    );
                }
                else
                {
                    // 루트에서 직접 ListItem 찾기
                    listItems = root.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)
                    );
                }
                
                // 방법 5: ListItem이 없으면 Pane이나 Custom 컨트롤도 시도
                if (listItems == null || listItems.Count == 0)
                {
                    listItems = root.FindAll(
                        TreeScope.Descendants,
                        new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)
                        )
                    );
                }

                if (listItems == null || listItems.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[UIA] No list items found in main window");
                    return badges;
                }

                System.Diagnostics.Debug.WriteLine($"[UIA] Found {listItems.Count} potential chat list items");

                // 각 항목에서 읽지않음 숫자와 대화방 이름 추출
                int processedCount = 0;
                foreach (AutomationElement item in listItems)
                {
                    try
                    {
                        // 항목의 Name 속성 (대화방 이름일 가능성)
                        string itemName = item.Current.Name ?? string.Empty;
                        
                        // 항목 내부의 모든 Text 요소 찾기
                        var textElements = item.FindAll(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)
                        );

                        string chatName = string.Empty;
                        int unreadCount = 0;
                        System.Drawing.Rectangle? badgeRect = null;

                        // 읽지않음 숫자 찾기 (정규식 ^\d{1,3}$)
                        foreach (AutomationElement textElem in textElements)
                        {
                            string text = textElem.Current.Name ?? string.Empty;
                            
                            // 숫자만 있는 텍스트 (1-999)
                            if (Regex.IsMatch(text, @"^\d{1,3}$"))
                            {
                                if (int.TryParse(text, out int count) && count > 0 && count <= 999)
                                {
                                    unreadCount = count;
                                    try
                                    {
                                        var rect = textElem.Current.BoundingRectangle;
                                        badgeRect = new System.Drawing.Rectangle(
                                            (int)rect.Left, (int)rect.Top,
                                            (int)rect.Width, (int)rect.Height
                                        );
                                    }
                                    catch { }
                                    break; // 첫 번째 숫자만 사용
                                }
                            }
                        }

                        // 대화방 이름 찾기
                        if (!string.IsNullOrWhiteSpace(itemName))
                        {
                            // Name이 숫자만 있거나 시간 패턴이 아니면 대화방 이름으로 사용
                            if (!Regex.IsMatch(itemName, @"^\d+$") &&
                                !itemName.Contains("오전") && !itemName.Contains("오후") &&
                                !itemName.Contains(":") && itemName.Length > 1)
                            {
                                chatName = itemName.Trim();
                            }
                        }

                        // Name이 적합하지 않으면 Text 요소에서 찾기
                        if (string.IsNullOrWhiteSpace(chatName))
                        {
                            foreach (AutomationElement textElem in textElements)
                            {
                                string text = textElem.Current.Name ?? string.Empty;
                                
                                // 숫자만 있거나 시간 패턴이 아니고, 길이가 적절한 텍스트
                                if (!string.IsNullOrWhiteSpace(text) &&
                                    !Regex.IsMatch(text, @"^\d+$") &&
                                    !text.Contains("오전") && !text.Contains("오후") &&
                                    !text.Contains(":") && text.Length > 1 && text.Length < 100)
                                {
                                    // 배지 숫자보다 왼쪽에 있으면 대화방 이름일 가능성 높음
                                    try
                                    {
                                        var textRect = textElem.Current.BoundingRectangle;
                                        if (badgeRect.HasValue)
                                        {
                                            if (textRect.Left < badgeRect.Value.Left)
                                            {
                                                chatName = text.Trim();
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            // 배지가 없어도 첫 번째 적합한 텍스트를 사용
                                            chatName = text.Trim();
                                            break;
                                        }
                                    }
                                    catch
                                    {
                                        chatName = text.Trim();
                                        break;
                                    }
                                }
                            }
                        }

                        // 읽지않음 숫자가 있으면 추가 (대화방 이름이 없어도 추가)
                        if (unreadCount > 0)
                        {
                            // 대화방 이름이 없으면 Name을 사용
                            if (string.IsNullOrWhiteSpace(chatName) && !string.IsNullOrWhiteSpace(itemName))
                            {
                                chatName = itemName.Trim();
                            }

                            // 중복 제거
                            if (!badges.Any(b => b.ChatName == chatName && b.UnreadCount == unreadCount))
                            {
                                badges.Add(new ChatBadgeInfo 
                                { 
                                    ChatName = chatName, 
                                    UnreadCount = unreadCount 
                                });
                                processedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 개별 항목 처리 실패는 무시하고 계속
                        System.Diagnostics.Debug.WriteLine($"[UIA] Error processing list item: {ex.Message}");
                    }
                }

                // 디버그 로그: 발견한 항목 수와 상위 20개 출력
                System.Diagnostics.Debug.WriteLine($"[UIA] Processed {processedCount} items with unread badges, total badges: {badges.Count}");
                int logCount = Math.Min(20, badges.Count);
                for (int i = 0; i < logCount; i++)
                {
                    var badge = badges[i];
                    System.Diagnostics.Debug.WriteLine($"[UIA] Badge {i + 1}: ChatName='{badge.ChatName}', UnreadCount={badge.UnreadCount}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIA] FindUnreadBadgesInMainWindow_UIA failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[UIA] Stack trace: {ex.StackTrace}");
            }

            if (badges.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[UIA] UIA not available or no badges found");
            }

            return badges;
            */ // 주석 처리 종료
        }

        public static string GetWindowTitle(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length == 0) return string.Empty;
            StringBuilder builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        public static string GetWindowClassName(IntPtr hWnd)
        {
            StringBuilder classBuilder = new StringBuilder(256);
            GetClassName(hWnd, classBuilder, classBuilder.Capacity);
            return classBuilder.ToString();
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        /// <summary>
        /// 창의 모든 자식 윈도우를 재귀적으로 탐색하여 텍스트 읽기
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);

        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;

        public static void CollectAllChildTexts(IntPtr hWnd, List<(string Text, string ClassName, WindowScanner.RECT Rect)> results, int maxDepth = 8, int currentDepth = 0, IntPtr? parentMainHwnd = null)
        {
            if (currentDepth >= maxDepth) return;

            // 메인 창의 화면 좌표 구하기 (상대 좌표 변환용)
            WindowScanner.RECT? mainWindowRect = null;
            if (parentMainHwnd.HasValue && parentMainHwnd.Value != IntPtr.Zero)
            {
                GetWindowRect(parentMainHwnd.Value, out WindowScanner.RECT mainRect);
                mainWindowRect = mainRect;
            }

            EnumChildWindows(hWnd, (childHwnd, lParam) =>
            {
                // 방법 1: GetWindowText 시도
                int length = GetWindowTextLength(childHwnd);
                string text = string.Empty;
                if (length > 0 && length < 200)
                {
                    StringBuilder builder = new StringBuilder(length + 1);
                    GetWindowText(childHwnd, builder, builder.Capacity);
                    text = builder.ToString().Trim();
                }

                // 방법 2: GetWindowText가 실패하면 SendMessage로 시도
                if (string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        // WM_GETTEXTLENGTH: 마지막 파라미터는 무시됨
                        IntPtr lenPtr = SendMessage(childHwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                        int msgLength = lenPtr.ToInt32();
                        if (msgLength > 0 && msgLength < 200)
                        {
                            StringBuilder msgBuilder = new StringBuilder(msgLength + 1);
                            // WM_GETTEXT: StringBuilder 오버로드 사용
                            SendMessage(childHwnd, WM_GETTEXT, (IntPtr)(msgLength + 1), msgBuilder);
                            text = msgBuilder.ToString().Trim();
                        }
                    }
                    catch
                    {
                        // SendMessage 실패 시 무시
                    }
                }

                // 클래스명 읽기
                StringBuilder classBuilder = new StringBuilder(256);
                GetClassName(childHwnd, classBuilder, classBuilder.Capacity);
                string className = classBuilder.ToString();

                // 위치 정보 (화면 좌표)
                WindowScanner.RECT rect = new WindowScanner.RECT();
                GetWindowRect(childHwnd, out rect);

                // 메인 창 기준 상대 좌표로 변환
                if (mainWindowRect.HasValue)
                {
                    rect.Left -= mainWindowRect.Value.Left;
                    rect.Top -= mainWindowRect.Value.Top;
                    rect.Right -= mainWindowRect.Value.Left;
                    rect.Bottom -= mainWindowRect.Value.Top;
                }

                if (!string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(className))
                {
                    results.Add((text, className, rect));
                }

                // 재귀적으로 자식의 자식도 탐색 (parentMainHwnd 전달)
                CollectAllChildTexts(childHwnd, results, maxDepth, currentDepth + 1, parentMainHwnd);

                return true;
            }, IntPtr.Zero);
        }

        /// <summary>
        /// 창의 모든 자식 윈도우에서 텍스트를 읽어 읽지않음 숫자 찾기 (개선된 버전)
        /// </summary>
        public static int FindUnreadCountInWindow(IntPtr hWnd)
        {
            List<(string Text, string ClassName, WindowScanner.RECT Rect)> allItems = new List<(string, string, WindowScanner.RECT)>();
            
            // 재귀적으로 모든 자식 윈도우 탐색
            CollectAllChildTexts(hWnd, allItems);

            // 클라이언트 영역 크기 구하기
            WindowScanner.RECT clientRect = new WindowScanner.RECT();
            GetClientRect(hWnd, out clientRect);
            int clientHeight = clientRect.Height;
            int clientWidth = clientRect.Width;

            // 메시지 영역 우선순위: 하단 50% > 중앙-하단 > 전체
            // 메시지 옆의 배지는 보통 메시지 영역 중앙-하단에 위치
            var messageAreaItems = allItems.Where(item => 
                item.Rect.Top > clientHeight * 0.3 && item.Rect.Top < clientHeight * 0.9 // 중앙-하단 60% 영역
            ).OrderBy(item => item.Rect.Top).ToList(); // 하단부터 정렬

            // 메시지 영역에서 작은 숫자(1-99)만 있는 텍스트 우선 찾기 (배지일 가능성 높음)
            foreach (var item in messageAreaItems)
            {
                // 텍스트가 단순 숫자만 있는 경우 (배지일 가능성 높음)
                if (!string.IsNullOrWhiteSpace(item.Text) && 
                    System.Text.RegularExpressions.Regex.IsMatch(item.Text, @"^\d{1,2}$"))
                {
                    if (int.TryParse(item.Text, out int num) && num > 0 && num <= 99)
                    {
                        return num; // 배지로 판단하고 즉시 반환
                    }
                }
            }

            // 메시지 영역에서 일반 숫자 패턴 찾기
            foreach (var item in messageAreaItems)
            {
                int num = ExtractNumberFromText(item.Text);
                if (num > 0 && num <= 99) return num; // 99 이하만 (배지 숫자는 보통 작음)
            }

            // 하단 영역에서 찾기
            var bottomItems = allItems.Where(item => 
                item.Rect.Top > clientHeight * 0.7 // 하단 30% 영역
            ).ToList();
            foreach (var item in bottomItems)
            {
                int num = ExtractNumberFromText(item.Text);
                if (num > 0 && num <= 99) return num;
            }

            // 전체에서 찾기 (마지막 수단)
            foreach (var item in allItems)
            {
                int num = ExtractNumberFromText(item.Text);
                if (num > 0 && num <= 99) return num;
            }

            return 0; // 찾지 못함
        }

        /// <summary>
        /// 창에서 대화 내용 추출 (자식 윈도우 텍스트 중 대화 메시지로 보이는 것만 필터링)
        /// </summary>
        public static List<string> ExtractChatMessages(IntPtr hWnd)
        {
            List<string> messages = new List<string>();
            List<(string Text, string ClassName, WindowScanner.RECT Rect)> allItems = new List<(string, string, WindowScanner.RECT)>();
            
            CollectAllChildTexts(hWnd, allItems);
            
            // 클라이언트 영역 크기 구하기
            WindowScanner.RECT clientRect = new WindowScanner.RECT();
            GetClientRect(hWnd, out clientRect);
            int clientHeight = clientRect.Height;
            
            // 대화 영역: 상단 20% 제외 (제목/버튼 영역), 하단 10% 제외 (입력 영역)
            var chatAreaItems = allItems.Where(item => 
                item.Rect.Top > clientHeight * 0.2 && 
                item.Rect.Top < clientHeight * 0.9 &&
                !string.IsNullOrWhiteSpace(item.Text)
            ).OrderBy(item => item.Rect.Top).ToList(); // 위에서 아래 순서
            
            foreach (var item in chatAreaItems)
            {
                string text = item.Text.Trim();
                
                // 필터링: 너무 짧거나 숫자만 있는 것은 제외
                if (text.Length < 2) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+$")) continue; // 숫자만 있는 것은 배지일 가능성
                if (text.Length > 500) continue; // 너무 긴 텍스트는 제외
                
                // UI 요소로 보이는 텍스트 제외
                if (text.Contains("보내기") || text.Contains("입력") || text.Contains("검색")) continue;
                if (item.ClassName.Contains("Button") || item.ClassName.Contains("Edit")) continue;
                
                // 중복 제거 (같은 텍스트가 여러 컨트롤에 있을 수 있음)
                if (!messages.Contains(text))
                {
                    messages.Add(text);
                }
            }
            
            return messages;
        }

        /// <summary>
        /// 텍스트에서 읽지않음 숫자 추출 (1-999까지 지원)
        /// </summary>
        private static int ExtractNumberFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            // 괄호 안의 숫자: (1), [1], {1}, (99+), [99+]
            var match = System.Text.RegularExpressions.Regex.Match(text, @"[\(\[\{]\s*(\d+)\+?\s*[\)\]\}]");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int num) && num > 0 && num <= 999)
                {
                    return num;
                }
            }

            // "99+" 같은 패턴
            match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\+");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int num) && num > 0 && num <= 999)
                {
                    return num;
                }
            }

            // 단순 숫자만 있는 경우 (1-999 사이)
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{1,3}$"))
            {
                if (int.TryParse(text, out int num) && num > 0 && num <= 999)
                {
                    return num;
                }
            }

            // 텍스트 중간에 숫자가 있는 경우 (예: "읽지않음 5", "New 3" 등)
            match = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,3})");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int num) && num > 0 && num <= 999)
                {
                    // 너무 큰 숫자나 연도 같은 건 제외 (예: 2024, 2025 등)
                    if (num < 100 || (num >= 100 && num <= 999 && !text.Contains("20")))
                    {
                        return num;
                    }
                }
            }

            return 0;
        }
    }

    public static class WindowEmbedder
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const int SW_SHOW = 5;
        public const int SW_HIDE = 0;
        public const int GWL_STYLE = -16;
        public const uint WS_CHILD = 0x40000000;
        public const uint WS_POPUP = 0x80000000;

        public const uint WM_KEYDOWN = 0x0100;
        public const int VK_ESCAPE = 0x1B;
        private const int WM_ACTIVATE = 0x0006;
        private const int WA_CLICKACTIVE = 1;

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        public static bool ForwardMouseWheel(IntPtr chatHwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (chatHwnd == IntPtr.Zero || !IsWindow(chatHwnd))
                return false;

            if (msg != WM_MOUSEWHEEL && msg != WM_MOUSEHWHEEL)
                return false;

            long param = lParam.ToInt64();
            int x = (short)(param & 0xFFFF);
            int y = (short)((param >> 16) & 0xFFFF);
            IntPtr hwndAtPoint = WindowScanner.WindowFromPoint(new System.Drawing.Point(x, y));

            IntPtr target = chatHwnd;
            if (hwndAtPoint != IntPtr.Zero && IsDescendantOf(chatHwnd, hwndAtPoint))
                target = hwndAtPoint;

            SendMessage(target, msg, wParam, lParam);
            return true;
        }

        private static bool IsDescendantOf(IntPtr ancestor, IntPtr hwnd)
        {
            IntPtr current = hwnd;
            while (current != IntPtr.Zero)
            {
                if (current == ancestor)
                    return true;
                current = GetParent(current);
            }
            return false;
        }

        public static void FocusWindow(IntPtr chatHwnd)
        {
            if (chatHwnd == IntPtr.Zero || !IsWindow(chatHwnd)) return;

            IntPtr inputHwnd = FindInputHwnd(chatHwnd);
            IntPtr focusTarget = inputHwnd != IntPtr.Zero ? inputHwnd : chatHwnd;

            uint targetThread = GetWindowThreadProcessId(focusTarget, out uint targetPid);
            uint currentThread = GetCurrentThreadId();
            bool attached = false;

            if (targetThread != 0 && targetThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, targetThread, true);
            }

            try
            {
                AllowSetForegroundWindow(targetPid);
                SetForegroundWindow(chatHwnd);
                PostMessage(chatHwnd, WM_ACTIVATE, (IntPtr)WA_CLICKACTIVE, IntPtr.Zero);
                SetFocus(focusTarget);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, targetThread, false);
                }
            }
        }

        private static IntPtr FindInputHwnd(IntPtr parent)
        {
            var state = new InputSearchState();
            CollectInputHwnd(parent, state);
            return state.Best;
        }

        private sealed class InputSearchState
        {
            public IntPtr Best { get; set; } = IntPtr.Zero;
            public int BestBottom { get; set; } = int.MinValue;
        }

        private static void CollectInputHwnd(IntPtr parent, InputSearchState state)
        {
            EnumChildWindows(parent, (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                    return true;

                string className = WindowScanner.GetWindowClassName(hwnd);
                if (IsInputClassName(className))
                {
                    WindowScanner.GetWindowRect(hwnd, out WindowScanner.RECT rect);
                    if (rect.Bottom > state.BestBottom)
                    {
                        state.BestBottom = rect.Bottom;
                        state.Best = hwnd;
                    }
                }

                CollectInputHwnd(hwnd, state);
                return true;
            }, IntPtr.Zero);
        }

        private static bool IsInputClassName(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            if (className.Equals("Edit", StringComparison.OrdinalIgnoreCase)) return true;
            if (className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)) return true;
            if (className.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void EmbedWindow(IntPtr childHwnd, IntPtr parentHwnd, System.Drawing.Rectangle bounds)
        {
            var setParentRes = SetParent(childHwnd, parentHwnd);
            if (setParentRes == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[EmbedWindow] SetParent failed hwnd={childHwnd}, parent={parentHwnd}, err={err}");
            }
            int style = GetWindowLong(childHwnd, GWL_STYLE);
            style = (int)(((uint)style | WS_CHILD) & ~WS_POPUP);
            int prevStyle = SetWindowLong(childHwnd, GWL_STYLE, style);
            if (prevStyle == 0)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[EmbedWindow] SetWindowLong failed hwnd={childHwnd}, err={err}");
            }
            MoveWindow(childHwnd, 0, 0, bounds.Width, bounds.Height, true);
            ShowWindow(childHwnd, SW_SHOW);
        }

        public static void ResizeWindow(IntPtr childHwnd, System.Drawing.Rectangle bounds)
        {
            MoveWindow(childHwnd, 0, 0, bounds.Width, bounds.Height, true);
        }

        public static void HideWindow(IntPtr childHwnd)
        {
            ShowWindow(childHwnd, SW_HIDE);
        }

        public static void RestoreWindow(IntPtr childHwnd)
        {
            SetParent(childHwnd, IntPtr.Zero);
            ShowWindow(childHwnd, SW_SHOW);
        }

        public static void CloseWindowWithEsc(IntPtr hWnd)
        {
            PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
        }
    }
}

