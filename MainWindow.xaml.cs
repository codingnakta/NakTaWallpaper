using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace NakTaWallpaper;

public partial class MainWindow : Window
{
    private const int HotkeyQuit = 9000;
    private const int HotkeyToggle = 9001;
    private const uint ModCtrl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkQ = 0x51;
    private const uint VkD = 0x44;
    private const uint SmtoNormal = 0x0000;
    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoredirectionbitmap = 0x00200000;
    private const uint LwaAlpha = 0x02;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const uint GaRoot = 2;
    private static readonly IntPtr HwndBottom = (IntPtr)1;

    private const int WmMousemove = 0x0200;
    private const int WmLbuttondown = 0x0201;
    private const int WmLbuttonup = 0x0202;
    private const int WmRbuttondown = 0x0204;
    private const int WmRbuttonup = 0x0205;
    private const int WmMousewheel = 0x020A;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkEscape = 0x1B;
    private const int SmCxdoubleclk = 36;
    private const int SmCydoubleclk = 37;

    private const uint LvmHittest = 0x1012;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private static readonly IntPtr MkLbutton = (IntPtr)0x0001;
    private static readonly IntPtr MkRbutton = (IntPtr)0x0002;
    private static readonly IntPtr MkMove = (IntPtr)0x0020;

    private bool _isInteractive;
    private bool _isRaisedDesktop;
    private IntPtr _progmanHandle;
    private IntPtr _workerW;
    private IntPtr _shellDefView;
    private IntPtr _webViewInputHandle;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private LowLevelHookProc? _mouseProc;
    private LowLevelHookProc? _keyboardProc;
    private System.Drawing.Rectangle _workingArea;

    private uint _lastClickTick;
    private POINT _lastClickPt;
    private uint _dblClickTime;
    private int _dblClickDist;

    public MainWindow()
    {
        InitializeComponent();
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        _workingArea = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
        Loaded += MainWindow_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        IntPtr hwnd = helper.Handle;

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        RegisterHotKey(hwnd, HotkeyQuit, ModCtrl | ModShift, VkQ);
        RegisterHotKey(hwnd, HotkeyToggle, ModCtrl | ModShift, VkD);

        _dblClickTime = GetDoubleClickTime();
        _dblClickDist = Math.Max(GetSystemMetrics(SmCxdoubleclk), GetSystemMetrics(SmCydoubleclk));

        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule!;
        IntPtr hModule = GetModuleHandle(mod.ModuleName);

        _mouseProc = MouseHookCallback;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, hModule, 0);

        _keyboardProc = KeyboardHookCallback;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, hModule, 0);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            AttachToDesktop(hwnd);

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NakTaWallpaper", "WebView2Data");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await webView.EnsureCoreWebView2Async(env);

        // 페이지가 새 창을 요청하면 (target="_blank", window.open 등)
        // WebView2 기본 동작 막고 크롬으로 열기 → 없으면 기본 브라우저로 fallback
        webView.CoreWebView2.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };

        webView.CoreWebView2.Navigate("http://localhost:5173/");

        // Chrome 입력 핸들 찾기 (WebView2 안의 자식 윈도우 트리에서)
        await Task.Delay(600);
        _webViewInputHandle = FindChromeInputHwnd(hwnd);

        // 백그라운드에서 업데이트 확인 (실패해도 조용히 무시)
        _ = UpdaterService.CheckForUpdateAsync();
    }

    // === Lively 방식 desktop 부착 ===
    // 1. Progman에 WS_EX_NOREDIRECTIONBITMAP 있으면 Win11 raised desktop
    // 2. 0x052C로 WorkerW 생성 요청
    // 3. raised desktop이면 우리 윈도우를 Progman의 자식 + WS_EX_LAYERED + SHELLDLL_DefView 바로 뒤
    // 4. 일반이면 WorkerW의 자식
    // 5. SHELLDLL_DefView는 절대 안 건드림
    // 외부 URL을 크롬으로 열고, 크롬이 없으면 시스템 기본 브라우저로 fallback
    private static void OpenExternal(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // 1) 크롬 표준 설치 경로 후보
        string[] chromePaths =
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        };

        foreach (var path in chromePaths)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "\"" + url + "\"",
                        UseShellExecute = false
                    });
                    return;
                }
                catch
                {
                    // 다음 후보 시도
                }
            }
        }

        // 2) 크롬 없으면 시스템 기본 브라우저 (Edge 일 수도, 다른 거 일 수도)
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // 둘 다 실패면 조용히 무시
        }
    }

    private void AttachToDesktop(IntPtr hwnd)
    {
        _progmanHandle = FindWindow("Progman", null);

        // Win11 raised desktop 감지
        uint progmanEx = (uint)GetWindowLong(_progmanHandle, GwlExstyle);
        _isRaisedDesktop = (progmanEx & WsExNoredirectionbitmap) != 0;

        SendMessageTimeout(_progmanHandle, 0x052C, new IntPtr(0xD), new IntPtr(0x1),
            SmtoNormal, 1000, out _);

        // SHELLDLL_DefView와 WorkerW 위치 파악
        IntPtr foundDefView = IntPtr.Zero;
        IntPtr foundWorkerW = IntPtr.Zero;

        EnumWindows((topHandle, _) =>
        {
            IntPtr dv = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero)
            {
                foundDefView = dv;
                // SHELLDLL_DefView 가진 윈도우 다음 형제 WorkerW
                foundWorkerW = FindWindowEx(IntPtr.Zero, topHandle, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        // raised desktop에서는 SHELLDLL_DefView가 Progman의 자식이고
        // WorkerW는 Progman의 자식으로 새로 생성됨
        if (_isRaisedDesktop)
        {
            foundWorkerW = FindWindowEx(_progmanHandle, IntPtr.Zero, "WorkerW", null);
        }

        _shellDefView = foundDefView;
        _workerW = foundWorkerW;

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;

        if (_isRaisedDesktop)
        {
            // === Win11 raised desktop 경로 ===
            // 우리 윈도우: WS_CHILD + WS_EX_LAYERED(alpha 255)
            // → 시각적으론 완전 불투명, 자연 hit-test로는 클릭 못 받음
            uint style = (uint)GetWindowLong(hwnd, GwlStyle);
            SetWindowLong(hwnd, GwlStyle, unchecked((int)((style & ~WsPopup) | WsChild)));

            uint exStyle = (uint)GetWindowLong(hwnd, GwlExstyle);
            if ((exStyle & WsExLayered) == 0)
                SetWindowLong(hwnd, GwlExstyle, unchecked((int)(exStyle | WsExLayered)));
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);

            SetParent(hwnd, _progmanHandle);
            MoveWindow(hwnd, 0, 0, bounds.Width, bounds.Height, true);

            // SHELLDLL_DefView 바로 뒤에 (아이콘이 위에, 우리가 아래)
            if (_shellDefView != IntPtr.Zero)
                SetWindowPos(hwnd, _shellDefView, 0, 0, 0, 0,
                    SwpNosize | SwpNomove | SwpNoactivate);

            EnsureWorkerWZOrder();
        }
        else
        {
            // === 클래식 경로 ===
            uint style = (uint)GetWindowLong(hwnd, GwlStyle);
            SetWindowLong(hwnd, GwlStyle, unchecked((int)((style & ~WsPopup) | WsChild)));

            IntPtr parent = _workerW != IntPtr.Zero ? _workerW : _progmanHandle;
            SetParent(hwnd, parent);
            MoveWindow(hwnd, 0, 0, bounds.Width, bounds.Height, true);
        }
    }

    // raised desktop에서 WorkerW가 Progman 자식 중 맨 뒤(Z-order bottom)에 있는지 보장
    private void EnsureWorkerWZOrder()
    {
        if (!_isRaisedDesktop || _workerW == IntPtr.Zero) return;

        IntPtr lastChild = IntPtr.Zero;
        EnumChildWindows(_progmanHandle, (h, _) =>
        {
            lastChild = h;
            return true;
        }, IntPtr.Zero);

        if (lastChild != _workerW)
        {
            SetWindowPos(_workerW, HwndBottom, 0, 0, 0, 0,
                SwpNosize | SwpNomove | SwpNoactivate);
        }
    }

    // WebView2 내부의 Chrome 입력 윈도우 찾기
    // Lively는 별도 프로세스라 Chrome_WidgetWin_1, 우리는 임베디드라 구조가 다를 수 있어 여러 후보 시도
    private IntPtr FindChromeInputHwnd(IntPtr root)
    {
        // 우선순위: Chrome_WidgetWin_1 → Chrome_RenderWidgetHostHWND → Chrome_WidgetWin_0
        string[] preferred = { "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", "Chrome_WidgetWin_0" };
        foreach (var target in preferred)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(root, (h, _) =>
            {
                var cn = new char[64];
                GetClassName(h, cn, cn.Length);
                string cls = new string(cn).TrimEnd('\0');
                if (cls == target)
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();

        // === 양쪽 모드 공통: 더블클릭 감지 ===
        if (msg == WmLbuttondown)
        {
            var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            bool dblClick = _lastClickTick != 0
                && (hs.time - _lastClickTick) <= _dblClickTime
                && Math.Abs(hs.pt.x - _lastClickPt.x) <= _dblClickDist
                && Math.Abs(hs.pt.y - _lastClickPt.y) <= _dblClickDist;

            if (dblClick && IsDesktopArea(hs.pt))
            {
                if (_isInteractive)
                {
                    // 인터랙티브 → 즉시 일반 모드 복귀, 두 번째 클릭 먹기
                    _lastClickTick = 0;
                    Dispatcher.BeginInvoke(ToggleInteractiveMode);
                    return new IntPtr(1);
                }
                else
                {
                    // 일반 모드 → 아이콘이면 통과, 빈 영역이면 인터랙티브 진입
                    var pt = hs.pt;
                    _lastClickTick = 0;
                    Task.Run(() =>
                    {
                        if (!IsClickOnDesktopIcon(pt))
                            Dispatcher.BeginInvoke(ToggleInteractiveMode);
                    });
                    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                }
            }
            else
            {
                _lastClickTick = hs.time;
                _lastClickPt = hs.pt;
            }
        }

        // 일반 모드는 더블클릭 외엔 그대로 통과
        if (!_isInteractive)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        // === 인터랙티브 모드: WebView2로 입력 포워딩 ===

        if (msg == WmMousemove)
        {
            var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (_workingArea.Contains(hs.pt.x, hs.pt.y))
                ForwardMouse(WmMousemove, hs.pt, MkMove);
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // 휠은 별도 처리: Chromium의 비동기 입력 파이프라인이 PostMessage 휠을
        // 무시하는 케이스가 많아 JavaScript로 직접 scrollBy 호출
        if (msg == WmMousewheel)
        {
            var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (IsDesktopArea(hs.pt))
            {
                short delta = unchecked((short)(hs.mouseData >> 16));
                int scrollY = -delta; // 휠 forward(+)면 위로 스크롤(-)

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        webView?.CoreWebView2?.ExecuteScriptAsync(
                            $"window.scrollBy({{top:{scrollY},left:0,behavior:'auto'}})");
                    }
                    catch { }
                });
                return new IntPtr(1);
            }
        }

        if (msg is WmLbuttondown or WmLbuttonup or WmRbuttondown or WmRbuttonup)
        {
            var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (IsDesktopArea(hs.pt))
            {
                IntPtr wparam = msg switch
                {
                    WmLbuttondown or WmLbuttonup => MkLbutton,
                    WmRbuttondown or WmRbuttonup => MkRbutton,
                    _ => IntPtr.Zero
                };

                ForwardMouse(msg, hs.pt, wparam);
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isInteractive &&
            (wParam.ToInt32() == WmKeydown || wParam.ToInt32() == WmSyskeydown))
        {
            int vk = Marshal.ReadInt32(lParam);
            if (vk == VkEscape)
            {
                Dispatcher.BeginInvoke(ToggleInteractiveMode);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void ForwardMouse(int msg, POINT screenPt, IntPtr wParam)
    {
        if (_webViewInputHandle == IntPtr.Zero) return;

        POINT pt = screenPt;
        ScreenToClient(_webViewInputHandle, ref pt);

        IntPtr lParam = (IntPtr)((pt.y << 16) | (pt.x & 0xFFFF));
        PostMessage(_webViewInputHandle, (uint)msg, wParam, lParam);
    }

    private bool IsDesktopArea(POINT pt)
    {
        IntPtr win = WindowFromPoint(pt);
        if (win == IntPtr.Zero) return false;

        IntPtr root = GetAncestor(win, GaRoot);
        if (root == IntPtr.Zero) root = win;

        if (root == _progmanHandle || root == _workerW) return true;

        var cn = new char[16];
        GetClassName(root, cn, cn.Length);
        string cls = new string(cn).TrimEnd('\0');
        return cls is "WorkerW" or "Progman";
    }

    private bool IsClickOnDesktopIcon(POINT screenPt)
    {
        if (_shellDefView == IntPtr.Zero) return false;

        IntPtr listView = FindWindowEx(_shellDefView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero) return false;

        POINT clientPt = screenPt;
        if (!ScreenToClient(listView, ref clientPt)) return false;

        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
            false, pid);
        if (hProc == IntPtr.Zero) return false;

        uint size = (uint)Marshal.SizeOf<LVHITTESTINFO>();
        IntPtr remoteBuf = VirtualAllocEx(hProc, IntPtr.Zero, size, MEM_COMMIT, PAGE_READWRITE);
        if (remoteBuf == IntPtr.Zero)
        {
            CloseHandle(hProc);
            return false;
        }

        bool onIcon = false;
        IntPtr localBuf = Marshal.AllocHGlobal((int)size);
        try
        {
            var info = new LVHITTESTINFO { pt = clientPt, iItem = -1 };
            Marshal.StructureToPtr(info, localBuf, false);
            if (WriteProcessMemory(hProc, remoteBuf, localBuf, size, out _))
            {
                SendMessageTimeout(listView, LvmHittest, IntPtr.Zero, remoteBuf,
                    SmtoNormal, 200, out IntPtr result);
                onIcon = result.ToInt32() >= 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(localBuf);
            VirtualFreeEx(hProc, remoteBuf, 0, MEM_RELEASE);
            CloseHandle(hProc);
        }

        return onIcon;
    }

    private void ToggleInteractiveMode()
    {
        _isInteractive = !_isInteractive;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmHotkey = 0x0312;
        if (msg == wmHotkey)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyQuit)
            {
                System.Windows.Application.Current.Shutdown();
                handled = true;
            }
            else if (id == HotkeyToggle)
            {
                ToggleInteractiveMode();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);

        var handle = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(handle, HotkeyQuit);
        UnregisterHotKey(handle, HotkeyToggle);
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
        uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress,
        uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        IntPtr lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);
}
