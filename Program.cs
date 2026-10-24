// PropriaClick - Ctrl + right click opens the properties of a shell item.
// Copyright (C) 2026 42 <42_ans@proton.me>
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace PropriaClick
{
    internal static class Program
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_APP_CLICK = 0x8001;
        private const int WM_APP_SHOWTRAY = 0x8002;
        private const int VK_CONTROL = 0x11;
        private const uint GA_ROOT = 2;
        private const int SWC_DESKTOP = 8;
        private const int SWFO_NEEDDISPATCH = 1;
        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
        private const uint SEE_MASK_NOASYNC = 0x00000100;
        private const int SW_SHOWNORMAL = 1;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;
        private const uint SVGIO_ALLVIEW = 0x00000002;
        private const int SIGDN_NORMALDISPLAY = 0;
        private const int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
        private const int STALE_CLICK_MS = 3000;
        private const int MAX_LOGGED_SIZE = 1024 * 1024;

        // Shell folders that appear as navigation pane nodes. They are matched by their
        // localized display name, because the navigation tree exposes no item id lists
        // to an outside process.
        private static readonly Guid[] NavFolderIds = new Guid[]
        {
            new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), // FOLDERID_Desktop
            new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173"), // FOLDERID_Profile
            new Guid("374DE290-123F-4565-9164-39C4925E467B"), // FOLDERID_Downloads
            new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), // FOLDERID_Documents
            new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"), // FOLDERID_Pictures
            new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"), // FOLDERID_Music
            new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), // FOLDERID_Videos
            new Guid("031E4825-7B94-4DC3-B131-E946B44C8DD5"), // FOLDERID_Libraries
            new Guid("0AC0837C-BBF8-452A-850D-79D08E667CA7"), // FOLDERID_ComputerFolder
            new Guid("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53"), // FOLDERID_NetworkFolder
            new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC"), // FOLDERID_RecycleBinFolder
            new Guid("82A74AEB-AEB4-465C-A014-D097EE346D63"), // FOLDERID_ControlPanelFolder
            new Guid("F3CE0F7C-4901-4ACC-8648-D5D44B04EF8F"), // FOLDERID_UsersFiles
        };

        private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        private const string AboutResource = "About.md";
        private const string AboutFile = "PropriaClick-About.md";
        private const string LicenseResource = "LICENSE";
        private const string LicenseFile = "PropriaClick-License.txt";

        private static IntPtr _hook;
        private static LowLevelMouseProc _hookProc;
        private static MessageWindow _msgWindow;
        private static NotifyIcon _tray;
        private static System.Drawing.Icon _trayIcon;
        private static ContextMenuStrip _trayMenu;
        private static EventWaitHandle _showTrayEvent;
        private static uint _taskbarCreatedMsg;

        private static readonly object _logLock = new object();
        private static string _logPath;

        // right button press seen by the hook, waiting for its button-up
        private static volatile bool _pending;
        private static POINT _pendingPt;
        private static IntPtr _pendingHwnd;
        private static int _pendingTick;

        // click worker: keeps UIA / COM / ShellExecuteEx off the hook thread
        private static readonly object _clickSync = new object();
        private static MessageWindow _clickWindow;
        private static readonly ManualResetEvent _workerReady = new ManualResetEvent(false);
        private static POINT _clickPt;
        private static IntPtr _clickHwnd;
        private static bool _clickQueued;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string lpParameters;
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [DllImport("shell32.dll")]
        private static extern uint ILGetSize(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

        [DllImport("shell32.dll")]
        private static extern int SHGetIDListFromObject(IntPtr punk, out IntPtr ppidl);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
            [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
            [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
            [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
            [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
            [PreserveSig] int EnableModelessSB(bool fEnable);
            [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
            [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
            [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
            [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
            [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
            [PreserveSig] int QueryActiveShellView(out IShellView ppshv);
            [PreserveSig] int OnViewWindowActive(IShellView ppshv);
            [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
            [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
            [PreserveSig] int EnableModeless(bool fEnable);
            [PreserveSig] int UIActivate(uint uState);
            [PreserveSig] int Refresh();
            [PreserveSig] int CreateViewWindow(IShellView psvPrevious, IntPtr pfs, IShellBrowser psb, IntPtr prcView, out IntPtr phWnd);
            [PreserveSig] int DestroyViewWindow();
            [PreserveSig] int GetCurrentInfo(IntPtr pfs);
            [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
            [PreserveSig] int SaveViewState();
            [PreserveSig] int SelectItem(IntPtr pidl, uint uFlags);
            [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView
        {
            [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
            [PreserveSig] int SetCurrentViewMode(uint ViewMode);
            [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
            [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
            [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
            [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetSelectionMarkedItem(out int piItem);
            [PreserveSig] int GetFocusedItem(out int piItem);
            [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
            [PreserveSig] int GetSpacing(out POINT ppt);
            [PreserveSig] int GetDefaultSpacing(out POINT ppt);
            [PreserveSig] int GetAutoArrange();
            [PreserveSig] int ItemAt(POINT pt, out int piItem);
            [PreserveSig] int SelectItem(int iItem, uint dwFlags);
            [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributes(uint dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int GetCount(out uint pdwNumItems);
            [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
            [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            int Count { get; }

            [PreserveSig]
            int Item([In, MarshalAs(UnmanagedType.Struct)] object index, out IntPtr ppid);

            object _NewEnum();

            void Register([In, MarshalAs(UnmanagedType.Interface)] object pid, [In] int hWnd, [In] int swClass, out int plCookie);
            void RegisterPending([In] int lThreadId, [In] IntPtr pvarloc, [In] IntPtr pvarlocRoot, [In] int swClass, out int plCookie);
            void Revoke([In] int lCookie);
            void OnNavigate([In] int lCookie, [In] IntPtr pvarloc);
            void OnActivated([In] int lCookie, [In] bool fActive);

            [PreserveSig]
            int FindWindowSW([In] IntPtr pvarloc, [In] IntPtr pvarlocRoot, [In] int swClass, out int phWnd, [In] int swfwOptions, out IntPtr ppdispOut);

            void OnCreated([In] int lCookie);
            void ProcessAttachDetach([In] bool fAttach);
        }

        [STAThread]
        private static void Main()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { }

            InitLogPath();

            Mutex mutex = new Mutex(false, "PropriaClick.SingleInstance");
            bool owned;
            try
            {
                owned = mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                // already running: ask the other instance to show its tray icon again
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting("PropriaClick.ShowTray"))
                        {
                            ev.Set();
                            return;
                        }
                    }
                    catch
                    {
                        Thread.Sleep(100);
                    }
                }
                return;
            }

            try
            {
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Log("fatal " + e.ExceptionObject);
                };
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    Log("ui exception " + e.Exception);
                };

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
                _msgWindow = new MessageWindow(false);
                InitTray();
                InitShowTrayEvent();
                StartClickWorker();

                _hookProc = HookProc;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                {
                    Log("hook install failed err=" + Marshal.GetLastWin32Error());
                    MessageBox.Show("无法安装全局鼠标钩子，软件无法运行。", "PropriaClick",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Shutdown();
                    return;
                }

                Log("started");
                Application.Run(new ApplicationContext());
            }
            finally
            {
                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                try { mutex.ReleaseMutex(); }
                catch { }
            }
        }

        private static void InitTray()
        {
            _trayIcon = LoadAppIcon();
            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = "PropriaClick";

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add(MenuItem("隐藏", delegate
            {
                if (_tray != null) _tray.Visible = false;
            }));
            _trayMenu.Items.Add(MenuItem("关闭", delegate { Shutdown(); }));
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(MenuItem("说明", delegate { ShowDocument(AboutResource, AboutFile); }));
            _trayMenu.Items.Add(MenuItem("许可证", delegate { ShowDocument(LicenseResource, LicenseFile); }));

            // A ToolStripDropDown defers its first paint, so the very first popup can show up
            // empty until some input forces a repaint. Lay it out and repaint it on open.
            _trayMenu.Opened += delegate
            {
                try
                {
                    _trayMenu.PerformLayout();
                    _trayMenu.Invalidate(true);
                    _trayMenu.Update();
                }
                catch { }
            };

            _tray.ContextMenuStrip = _trayMenu;

            // Build the drop-down window and its layout now, so that the first real popup is
            // not also the first time the window has ever been created.
            try
            {
                if (_trayMenu.Handle == IntPtr.Zero) Log("menu handle not created");
                _trayMenu.PerformLayout();
            }
            catch (Exception ex)
            {
                Log("menu warmup failed " + ex.Message);
            }

            _tray.Visible = true;
        }

        private static ToolStripMenuItem MenuItem(string text, EventHandler onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += onClick;
            return item;
        }

        // The tray icon is the executable's own icon, so there is only one icon to maintain.
        // It has to come from the shell: the embedded icon is a group resource that
        // System.Drawing.Icon cannot read straight out of a file.
        private static System.Drawing.Icon LoadAppIcon()
        {
            IntPtr large = IntPtr.Zero;
            IntPtr small = IntPtr.Zero;
            try
            {
                if (ExtractIconEx(Application.ExecutablePath, 0, out large, out small, 1) == 0) throw new InvalidOperationException();
                IntPtr handle = small != IntPtr.Zero ? small : large;
                if (handle == IntPtr.Zero) throw new InvalidOperationException();
                using (System.Drawing.Icon temp = System.Drawing.Icon.FromHandle(handle))
                {
                    return (System.Drawing.Icon)temp.Clone();
                }
            }
            catch (Exception ex)
            {
                Log("tray icon failed " + ex.Message);
                return System.Drawing.SystemIcons.Application;
            }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
        }

        private static void ShowTray()
        {
            if (_tray == null) return;
            // toggling forces NotifyIcon to re-add the icon after an explorer restart
            try { _tray.Visible = false; }
            catch { }
            try { _tray.Visible = true; }
            catch { }
        }

        private static void Shutdown()
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            Application.Exit();
        }

        private static void InitShowTrayEvent()
        {
            try
            {
                _showTrayEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "PropriaClick.ShowTray");
            }
            catch (Exception ex)
            {
                Log("showtray event failed " + ex.Message);
                return;
            }
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        _showTrayEvent.WaitOne();
                        if (_msgWindow != null && _msgWindow.Handle != IntPtr.Zero)
                        {
                            PostMessage(_msgWindow.Handle, WM_APP_SHOWTRAY, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // Writes the embedded document next to the exe and opens it with its default handler.
        private static void ShowDocument(string resourceName, string fileName)
        {
            string text = ReadEmbedded(resourceName);
            if (text == null)
            {
                Log("missing resource " + resourceName);
                return;
            }

            string path = null;
            try
            {
                path = WriteDocument(Path.GetDirectoryName(Application.ExecutablePath), fileName, text);
            }
            catch { }
            if (path == null)
            {
                try { path = WriteDocument(Path.GetTempPath(), fileName, text); }
                catch { }
            }
            if (path == null)
            {
                MessageBox.Show("无法在软件目录或临时目录中创建 " + fileName + "。", "PropriaClick",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            OpenFile(path);
        }

        private static string ReadEmbedded(string name)
        {
            try
            {
                using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("read resource failed " + ex.Message);
                return null;
            }
        }

        private static string WriteDocument(string directory, string fileName, string text)
        {
            if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException();
            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, text, new UTF8Encoding(true));
            return path;
        }

        private static void OpenFile(string path)
        {
            SHELLEXECUTEINFO sei = new SHELLEXECUTEINFO();
            sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            sei.fMask = SEE_MASK_FLAG_NO_UI;
            sei.hwnd = _msgWindow != null ? _msgWindow.Handle : IntPtr.Zero;
            sei.lpVerb = "open";
            sei.lpFile = path;
            sei.nShow = SW_SHOWNORMAL;
            if (ShellExecuteEx(ref sei)) return;

            // no handler for this extension (common for markdown): show the text instead
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log("open document failed " + ex.Message); }
        }

        private sealed class MessageWindow : NativeWindow
        {
            private readonly bool _worker;

            public MessageWindow(bool worker)
            {
                _worker = worker;
                CreateParams cp = new CreateParams();
                cp.Caption = worker ? "PropriaClick.WorkerWindow" : "PropriaClick.MessageWindow";
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (_worker)
                {
                    if (m.Msg == WM_APP_CLICK)
                    {
                        POINT pt;
                        IntPtr hwnd;
                        lock (_clickSync)
                        {
                            if (!_clickQueued) return;
                            _clickQueued = false;
                            pt = _clickPt;
                            hwnd = _clickHwnd;
                        }
                        try
                        {
                            HandlePendingClick(pt, hwnd);
                        }
                        catch (Exception ex)
                        {
                            Log("click exception " + ex);
                        }
                        return;
                    }
                    base.WndProc(ref m);
                    return;
                }

                if (m.Msg == WM_APP_SHOWTRAY)
                {
                    ShowTray();
                    return;
                }
                if (_taskbarCreatedMsg != 0 && (uint)m.Msg == _taskbarCreatedMsg)
                {
                    ShowTray();
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // Runs on the thread that installed the hook and must return fast: no file I/O,
        // no UIA, no COM, no ShellExecuteEx in here.
        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_RBUTTONDOWN)
                    {
                        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                        {
                            MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                            if (IsInShellView(info.pt))
                            {
                                _pending = true;
                                _pendingPt = info.pt;
                                _pendingHwnd = WindowFromPoint(info.pt);
                                _pendingTick = Environment.TickCount;
                                return (IntPtr)1;
                            }
                        }
                        // a fresh non-matching press means the earlier one never saw its
                        // button-up: drop it instead of swallowing the next click
                        _pending = false;
                    }
                    else if (msg == WM_RBUTTONUP && _pending)
                    {
                        _pending = false;
                        int elapsed = unchecked(Environment.TickCount - _pendingTick);
                        if (elapsed >= 0 && elapsed < STALE_CLICK_MS)
                        {
                            MessageWindow w = _clickWindow;
                            if (w != null && w.Handle != IntPtr.Zero)
                            {
                                lock (_clickSync)
                                {
                                    _clickPt = _pendingPt;
                                    _clickHwnd = _pendingHwnd;
                                    _clickQueued = true;
                                }
                                PostMessage(w.Handle, WM_APP_CLICK, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex)
            {
                try { Log("hook exception " + ex.Message); }
                catch { }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsInShellView(POINT pt)
        {
            IntPtr h = WindowFromPoint(pt);
            for (int i = 0; i < 16 && h != IntPtr.Zero; i++)
            {
                if (GetClassNameStr(h) == "SHELLDLL_DefView") return true;
                h = GetParent(h);
            }
            return false;
        }

        // Dedicated STA worker with its own message pump, so UIA / COM / ShellExecuteEx never
        // block the hook thread. Clicks reach it through PostMessage, which never blocks.
        private static void StartClickWorker()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    _clickWindow = new MessageWindow(true);
                    _workerReady.Set();
                    MSG msg;
                    while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
                catch (Exception ex)
                {
                    Log("click worker died " + ex);
                    _workerReady.Set();
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _workerReady.WaitOne(3000);
        }

        // 0 = nothing usable, 1 = shell view item (selected through UIA),
        // 2 = navigation pane tree node (name captured, selection left untouched).
        private static int GetUiaHit(POINT pt, out string treeItemName)
        {
            treeItemName = null;
            try
            {
                AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(pt.x, pt.y));
                AutomationElement cur = el;
                for (int i = 0; i < 30 && cur != null; i++)
                {
                    ControlType ct = null;
                    string nm = "";
                    try { ct = cur.Current.ControlType; } catch { }
                    try { nm = cur.Current.Name; } catch { }

                    if (ct == ControlType.ListItem || ct == ControlType.DataItem)
                    {
                        object pattern;
                        if (!cur.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) return 0;
                        ((SelectionItemPattern)pattern).Select();
                        // let the view commit the selection before it is read back
                        Thread.Sleep(60);
                        return 1;
                    }
                    if (ct == ControlType.TreeItem)
                    {
                        treeItemName = nm;
                        return 2;
                    }
                    cur = TreeWalker.ControlViewWalker.GetParent(cur);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log("uia exception " + ex.Message);
                return 0;
            }
        }

        private static void HandlePendingClick(POINT pt, IntPtr hwnd)
        {
            IntPtr top = GetAncestor(hwnd, GA_ROOT);
            string cls = GetClassNameStr(top);

            string treeItemName;
            int kind = GetUiaHit(pt, out treeItemName);

            if (kind == 2)
            {
                // navigation pane node: no item id list available, resolve it by display name
                IntPtr navPidl = ResolveNavigationName(treeItemName);
                if (navPidl == IntPtr.Zero)
                {
                    Log("nav: cannot resolve '" + treeItemName + "'");
                    return;
                }
                try { ShowProperties(navPidl, top); }
                finally { CoTaskMemFree(navPidl); }
                return;
            }

            IFolderView fv;
            IShellView sv;
            if (cls == "Progman" || cls == "WorkerW")
            {
                if (!TryGetDesktopFolderView(out fv, out sv)) return;
            }
            else if (cls == "CabinetWClass")
            {
                if (!TryGetExplorerFolderView(top, out fv, out sv)) return;
            }
            else
            {
                return;
            }

            int index = -1;
            if (kind == 1)
            {
                if (fv.GetSelectionMarkedItem(out index) != 0 || index < 0)
                {
                    if (fv.GetFocusedItem(out index) != 0 || index < 0) return;
                }
            }
            else
            {
                // Some views (the Windows 11 home and gallery pages) do not report their items
                // through UIA or ItemAt. Try the cheapest exact answer first, then the general
                // ones that do not depend on hit testing.
                if (HitTestIndex(fv, sv, pt, out index) != 0 || index < 0) index = HitTestByPosition(fv, sv, pt);
                if (index < 0)
                {
                    IntPtr xamlPidl = ResolveXamlItem(fv, pt);
                    if (xamlPidl != IntPtr.Zero)
                    {
                        try { ShowProperties(xamlPidl, top); }
                        finally { CoTaskMemFree(xamlPidl); }
                    }
                    return;
                }
            }

            IntPtr itemPidl;
            if (fv.Item(index, out itemPidl) != 0 || itemPidl == IntPtr.Zero) return;

            // IFolderView::Item returns a PIDL relative to the view's folder; ShellExecuteEx
            // needs a desktop-absolute one.
            IntPtr folderPidl = GetFolderPidl(fv);
            try
            {
                IntPtr absolute = folderPidl != IntPtr.Zero ? ILCombine(folderPidl, itemPidl) : IntPtr.Zero;
                bool combined = absolute != IntPtr.Zero;
                if (!combined) absolute = itemPidl;

                ShowProperties(absolute, top);
                if (combined) CoTaskMemFree(absolute);
            }
            finally
            {
                CoTaskMemFree(itemPidl);
                if (folderPidl != IntPtr.Zero) CoTaskMemFree(folderPidl);
            }
        }

        private static int HitTestIndex(IFolderView fv, IShellView sv, POINT screenPt, out int index)
        {
            index = -1;
            IntPtr vh;
            int hr = sv.GetWindow(out vh);
            if (hr != 0 || vh == IntPtr.Zero) return hr != 0 ? hr : -1;
            POINT p = screenPt;
            ScreenToClient(vh, ref p);
            return fv.ItemAt(p, out index);
        }

        // Some shell views (the Windows 11 home and gallery pages) do not implement
        // IFolderView::ItemAt, but they still report where their items are. Accept a position
        // only when the click lands inside that item's cell.
        private static int HitTestByPosition(IFolderView fv, IShellView sv, POINT screenPt)
        {
            try
            {
                IntPtr vh;
                if (sv.GetWindow(out vh) != 0 || vh == IntPtr.Zero) return -1;

                int count;
                if (fv.ItemCount(SVGIO_ALLVIEW, out count) != 0 || count <= 0) return -1;

                POINT spacing;
                if (fv.GetSpacing(out spacing) != 0 || spacing.x <= 0 || spacing.y <= 0)
                {
                    spacing.x = 96;
                    spacing.y = 96;
                }

                int n = count > 5000 ? 5000 : count;
                List<int> ids = new List<int>(n);
                List<POINT> pos = new List<POINT>(n);
                for (int i = 0; i < n; i++)
                {
                    IntPtr pidl;
                    if (fv.Item(i, out pidl) != 0 || pidl == IntPtr.Zero) continue;
                    POINT p;
                    int hrp = fv.GetItemPosition(pidl, out p);
                    CoTaskMemFree(pidl);
                    if (hrp != 0) continue;
                    ids.Add(i);
                    pos.Add(p);
                }
                if (ids.Count == 0) return -1;

                IntPtr[] origins = CandidateOrigins(vh);
                for (int oi = 0; oi < origins.Length; oi++)
                {
                    POINT local = screenPt;
                    ScreenToClient(origins[oi], ref local);
                    int best = -1;
                    int bestD = int.MaxValue;
                    for (int k = 0; k < ids.Count; k++)
                    {
                        int dx = local.x - pos[k].x;
                        int dy = local.y - pos[k].y;
                        if (dx < 0 || dy < 0 || dx >= spacing.x || dy >= spacing.y) continue;
                        int d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; best = k; }
                    }
                    if (best >= 0) return ids[best];
                }
                return -1;
            }
            catch (Exception ex)
            {
                Log("position hit test failed " + ex.Message);
                return -1;
            }
        }

        // Item positions are reported relative to the shell view window or to one of the DUI /
        // XAML hosts nested inside it, so every plausible origin is tried.
        private static IntPtr[] CandidateOrigins(IntPtr viewWindow)
        {
            List<IntPtr> list = new List<IntPtr>();
            list.Add(viewWindow);
            IntPtr child = GetWindow(viewWindow, GW_CHILD);
            int guard = 0;
            while (child != IntPtr.Zero && guard++ < 32)
            {
                if (IsItemHostClass(GetClassNameStr(child))) list.Add(child);
                IntPtr grandChild = GetWindow(child, GW_CHILD);
                int inner = 0;
                while (grandChild != IntPtr.Zero && inner++ < 32)
                {
                    if (IsItemHostClass(GetClassNameStr(grandChild))) list.Add(grandChild);
                    grandChild = GetWindow(grandChild, GW_HWNDNEXT);
                }
                child = GetWindow(child, GW_HWNDNEXT);
            }
            return list.ToArray();
        }

        private static bool IsItemHostClass(string c)
        {
            return c == "DUIViewWndClassName" || c == "DUIListView" || c == "DirectUIHWND" ||
                   c.IndexOf("DesktopChildSiteBridge") >= 0;
        }

        // Last resort for views whose items cannot be hit tested (the Windows 11 home and gallery
        // pages render inside a XAML island). The accessibility tree still names the element under
        // the cursor, so that name is used to look the matching shell item up.
        private static IntPtr ResolveXamlItem(IFolderView fv, POINT pt)
        {
            try
            {
                AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(pt.x, pt.y));
                if (el == null) return IntPtr.Zero;

                AutomationElementCollection all = el.FindAll(TreeScope.Subtree, Condition.TrueCondition);
                if (all == null || all.Count == 0) return IntPtr.Zero;

                AutomationElement bestSel = null;
                AutomationElement bestItem = null;
                AutomationElement bestAny = null;
                double areaSel = double.MaxValue;
                double areaItem = double.MaxValue;
                double areaAny = double.MaxValue;
                for (int i = 0; i < all.Count; i++)
                {
                    AutomationElement e = all[i];
                    System.Windows.Rect r;
                    string nm;
                    try { r = e.Current.BoundingRectangle; }
                    catch { continue; }
                    if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                    if (pt.x < r.Left || pt.x >= r.Right || pt.y < r.Top || pt.y >= r.Bottom) continue;
                    try { nm = e.Current.Name; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(nm)) continue;

                    object pattern;
                    bool selectable = e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern);
                    ControlType ctl = null;
                    try { ctl = e.Current.ControlType; } catch { }
                    bool itemLike = ctl == ControlType.ListItem || ctl == ControlType.DataItem ||
                                    ctl == ControlType.TreeItem;

                    // Prefer a real list item over the decoration painted inside it (icon, caption)
                    // and over the section container that holds a whole group of items.
                    double area = r.Width * r.Height;
                    if (selectable && area < areaSel) { areaSel = area; bestSel = e; }
                    if (itemLike && area < areaItem) { areaItem = area; bestItem = e; }
                    if (area < areaAny) { areaAny = area; bestAny = e; }
                }

                AutomationElement chosen = bestSel != null ? bestSel : (bestItem != null ? bestItem : bestAny);
                if (chosen == null) return IntPtr.Zero;

                string name = ItemNameAt(chosen);
                if (string.IsNullOrEmpty(name)) return IntPtr.Zero;

                IntPtr pidl = FindItemByDisplayName(fv, name);
                if (pidl != IntPtr.Zero) return pidl;
                return FindItemByShellItemArray(fv, name);
            }
            catch (Exception ex)
            {
                Log("accessibility hit test failed " + ex.Message);
                return IntPtr.Zero;
            }
        }

        // The element under the cursor is usually a piece of the item's visual tree (an icon or
        // a caption), so walk up to the containing list item and use its name.
        private static string ItemNameAt(AutomationElement e)
        {
            AutomationElement cur = e;
            for (int i = 0; i < 12 && cur != null; i++)
            {
                ControlType ctl = null;
                string nm = null;
                try { ctl = cur.Current.ControlType; } catch { }
                try { nm = cur.Current.Name; } catch { }
                if ((ctl == ControlType.ListItem || ctl == ControlType.DataItem ||
                     ctl == ControlType.TreeItem) && !string.IsNullOrEmpty(nm))
                {
                    return nm;
                }
                cur = TreeWalker.ControlViewWalker.GetParent(cur);
            }
            try { return e.Current.Name; }
            catch { return null; }
        }

        // Returns the first item whose display name equals the name UI Automation exposed for the
        // click. Nothing is assumed about the layout, so any view that can enumerate its contents
        // works.
        private static IntPtr FindItemByDisplayName(IFolderView fv, string name)
        {
            int count;
            if (fv.ItemCount(SVGIO_ALLVIEW, out count) != 0 || count <= 0) return IntPtr.Zero;

            IntPtr folderPidl = GetFolderPidl(fv);
            try
            {
                int n = count > 5000 ? 5000 : count;
                for (int i = 0; i < n; i++)
                {
                    IntPtr pidl;
                    if (fv.Item(i, out pidl) != 0 || pidl == IntPtr.Zero) break;

                    IntPtr abs = IntPtr.Zero;
                    try
                    {
                        abs = folderPidl != IntPtr.Zero ? ILCombine(folderPidl, pidl) : ClonePidl(pidl);
                        if (abs == IntPtr.Zero) continue;
                        if (string.Equals(RawName(abs, SIGDN_NORMALDISPLAY), name, StringComparison.OrdinalIgnoreCase))
                        {
                            IntPtr found = abs;
                            abs = IntPtr.Zero;
                            return found;
                        }
                    }
                    finally
                    {
                        if (abs != IntPtr.Zero) CoTaskMemFree(abs);
                        CoTaskMemFree(pidl);
                    }
                }
                return IntPtr.Zero;
            }
            finally
            {
                if (folderPidl != IntPtr.Zero) CoTaskMemFree(folderPidl);
            }
        }

        // Same idea through IShellItemArray, which some views implement even when
        // IFolderView::Item does not work.
        private static IntPtr FindItemByShellItemArray(IFolderView fv, string name)
        {
            IntPtr arrPtr = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemArray).GUID;
                if (fv.Items(SVGIO_ALLVIEW, ref iid, out arrPtr) != 0 || arrPtr == IntPtr.Zero) return IntPtr.Zero;

                IShellItemArray arr = (IShellItemArray)Marshal.GetObjectForIUnknown(arrPtr);
                uint count;
                if (arr.GetCount(out count) != 0) return IntPtr.Zero;

                for (uint i = 0; i < count && i < 5000; i++)
                {
                    IShellItem item;
                    if (arr.GetItemAt(i, out item) != 0 || item == null) continue;
                    IntPtr nm;
                    if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out nm) != 0 || nm == IntPtr.Zero) continue;
                    string disp;
                    try { disp = Marshal.PtrToStringUni(nm); }
                    finally { CoTaskMemFree(nm); }
                    if (!string.Equals(disp, name, StringComparison.OrdinalIgnoreCase)) continue;

                    IntPtr full;
                    if (item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out full) != 0 || full == IntPtr.Zero)
                        return IntPtr.Zero;
                    string path;
                    try { path = Marshal.PtrToStringUni(full); }
                    finally { CoTaskMemFree(full); }

                    IntPtr pidl;
                    uint attrs;
                    if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs) != 0) return IntPtr.Zero;
                    return pidl;
                }
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Log("shell item array lookup failed " + ex.Message);
                return IntPtr.Zero;
            }
            finally
            {
                if (arrPtr != IntPtr.Zero) Marshal.Release(arrPtr);
            }
        }

        private static IntPtr ClonePidl(IntPtr pidl)
        {
            uint size = ILGetSize(pidl);
            if (size == 0) return IntPtr.Zero;
            byte[] buffer = new byte[size];
            Marshal.Copy(pidl, buffer, 0, (int)size);
            IntPtr copy = Marshal.AllocCoTaskMem((int)size);
            Marshal.Copy(buffer, 0, copy, (int)size);
            return copy;
        }

        private static void ShowProperties(IntPtr pidl, IntPtr owner)
        {
            List<IntPtr> before = TopLevelDialogs();
            SHELLEXECUTEINFO sei = new SHELLEXECUTEINFO();
            sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            sei.fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
            // owning the property sheet by the window the click came from keeps it in front of
            // that window instead of letting Explorer cover it
            sei.hwnd = owner;
            sei.lpVerb = "properties";
            sei.lpIDList = pidl;
            sei.nShow = SW_SHOWNORMAL;
            if (!ShellExecuteEx(ref sei))
            {
                Log("ShellExecuteEx failed err=" + Marshal.GetLastWin32Error());
                return;
            }
            RaiseNewDialog(before);
        }

        // The shell creates the property sheet on another thread, so it can end up behind the
        // window it belongs to. Watch for the new dialog and bring it to the front.
        private static void RaiseNewDialog(List<IntPtr> before)
        {
            IntPtr dlg = IntPtr.Zero;
            for (int i = 0; i < 20 && dlg == IntPtr.Zero; i++)
            {
                Thread.Sleep(40);
                List<IntPtr> now = TopLevelDialogs();
                for (int k = 0; k < now.Count; k++)
                {
                    if (!before.Contains(now[k])) { dlg = now[k]; break; }
                }
            }
            if (dlg == IntPtr.Zero) return;

            // A fresh property sheet is usually refused the foreground because Explorer still
            // holds it, so raise it through the topmost band, which is not subject to the
            // foreground rules, and then try to activate it.
            PushToFront(dlg);
            Thread.Sleep(150);
            PushToFront(dlg);
            if (!Activate(dlg))
            {
                Thread.Sleep(150);
                PushToFront(dlg);
                Activate(dlg);
            }
        }

        private static void PushToFront(IntPtr h)
        {
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private static bool Activate(IntPtr h)
        {
            SetForegroundWindow(h);
            if (GetForegroundWindow() == h) return true;

            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
            uint myThread = GetCurrentThreadId();
            if (fgThread == 0 || fgThread == myThread) return false;

            bool attached = AttachThreadInput(myThread, fgThread, true);
            try
            {
                if (attached) SetForegroundWindow(h);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
            return GetForegroundWindow() == h;
        }

        private static List<IntPtr> TopLevelDialogs()
        {
            List<IntPtr> list = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (IsWindowVisible(h) && GetClassNameStr(h) == "#32770") list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static IntPtr GetFolderPidl(IFolderView fv)
        {
            IntPtr folderPtr;
            Guid iid = IID_IShellFolder;
            if (fv.GetFolder(ref iid, out folderPtr) != 0 || folderPtr == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr pidl;
                if (SHGetIDListFromObject(folderPtr, out pidl) != 0) return IntPtr.Zero;
                return pidl;
            }
            finally
            {
                Marshal.Release(folderPtr);
            }
        }

        // Maps a navigation pane node (identified by its localized display name and nothing else)
        // to a shell item. Virtual folders are addressed by CLSID, plain folders by known folder
        // path, and drives by root path.
        private static IntPtr ResolveNavigationName(string name)
        {
            if (string.IsNullOrEmpty(name)) return IntPtr.Zero;

            string[] virtualFolders = new string[]
            {
                "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", // this pc
                "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", // network
                "::{645FF040-5081-101B-9F08-00AA002F954E}", // recycle bin
                "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", // control panel
                "::{031E4825-7B94-4DC3-B131-E946B44C8DD5}", // libraries
                "::{00021400-0000-0000-C000-000000000046}", // desktop
            };
            for (int i = 0; i < virtualFolders.Length; i++)
            {
                IntPtr pidl;
                if (TryParseAndMatch(virtualFolders[i], name, out pidl)) return pidl;
            }

            for (int i = 0; i < NavFolderIds.Length; i++)
            {
                IntPtr pathPidl = KnownFolderPidl(ref NavFolderIds[i]);
                if (pathPidl == IntPtr.Zero) continue;
                if (PidlMatchesName(pathPidl, name)) return pathPidl;
                CoTaskMemFree(pathPidl);
            }

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    IntPtr pidl;
                    if (TryParseAndMatch(drives[i].RootDirectory.FullName, name, out pidl)) return pidl;
                }
            }
            catch (Exception ex)
            {
                Log("drive enum failed " + ex.Message);
            }

            return IntPtr.Zero;
        }

        private static bool TryParseAndMatch(string pathOrClsid, string name, out IntPtr pidl)
        {
            pidl = IntPtr.Zero;
            uint attrs;
            if (SHParseDisplayName(pathOrClsid, IntPtr.Zero, out pidl, 0, out attrs) != 0 || pidl == IntPtr.Zero)
                return false;
            if (PidlMatchesName(pidl, name)) return true;
            CoTaskMemFree(pidl);
            pidl = IntPtr.Zero;
            return false;
        }

        private static IntPtr KnownFolderPidl(ref Guid id)
        {
            IntPtr pathPtr;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pathPtr) != 0 || pathPtr == IntPtr.Zero)
                return IntPtr.Zero;
            string path;
            try { path = Marshal.PtrToStringUni(pathPtr); }
            finally { CoTaskMemFree(pathPtr); }
            if (string.IsNullOrEmpty(path)) return IntPtr.Zero;

            IntPtr pidl;
            uint attrs;
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs) != 0) return IntPtr.Zero;
            return pidl;
        }

        private static bool PidlMatchesName(IntPtr pidl, string name)
        {
            string s = RawName(pidl, SIGDN_NORMALDISPLAY);
            if (string.IsNullOrEmpty(s)) return false;
            if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
            // the navigation pane sometimes decorates a folder with its owner, e.g.
            // "user - personal" for what the shell calls just "user"
            return name.StartsWith(s + " - ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetDesktopFolderView(out IFolderView folderView, out IShellView shellView)
        {
            folderView = null;
            shellView = null;
            try
            {
                IShellWindows shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows));

                IntPtr v1 = Marshal.AllocHGlobal(16);
                IntPtr v2 = Marshal.AllocHGlobal(16);
                try
                {
                    for (int k = 0; k < 16; k++)
                    {
                        Marshal.WriteByte(v1, k, 0);
                        Marshal.WriteByte(v2, k, 0);
                    }

                    int hwnd;
                    IntPtr disp;
                    if (shellWindows.FindWindowSW(v1, v2, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH, out disp) != 0 ||
                        disp == IntPtr.Zero) return false;

                    object desktop = Marshal.GetObjectForIUnknown(disp);
                    Marshal.Release(disp);

                    IShellBrowser browser = GetShellBrowser(desktop);
                    if (browser == null) return false;

                    IShellView sv;
                    if (browser.QueryActiveShellView(out sv) != 0 || sv == null) return false;

                    folderView = sv as IFolderView;
                    shellView = sv;
                    return folderView != null;
                }
                finally
                {
                    Marshal.FreeHGlobal(v1);
                    Marshal.FreeHGlobal(v2);
                }
            }
            catch (Exception ex)
            {
                Log("desktop view failed " + ex.Message);
                return false;
            }
        }

        private static bool TryGetExplorerFolderView(IntPtr topHwnd, out IFolderView folderView, out IShellView shellView)
        {
            folderView = null;
            shellView = null;
            try
            {
                IShellWindows shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows));

                for (int i = 0; i < 64; i++)
                {
                    IntPtr disp;
                    if (shellWindows.Item((object)i, out disp) != 0 || disp == IntPtr.Zero) break;

                    object item;
                    try { item = Marshal.GetObjectForIUnknown(disp); }
                    finally { Marshal.Release(disp); }

                    IShellBrowser browser = GetShellBrowser(item);
                    if (browser == null) continue;

                    IntPtr bh;
                    if (browser.GetWindow(out bh) != 0 || bh == IntPtr.Zero) continue;
                    if (GetAncestor(bh, GA_ROOT) != topHwnd) continue;

                    IShellView sv;
                    if (browser.QueryActiveShellView(out sv) != 0 || sv == null) continue;

                    IFolderView fv = sv as IFolderView;
                    if (fv == null) continue;
                    folderView = fv;
                    shellView = sv;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log("explorer view failed " + ex.Message);
                return false;
            }
        }

        private static IShellBrowser GetShellBrowser(object disp)
        {
            if (disp == null) return null;
            IntPtr punk = Marshal.GetIUnknownForObject(disp);
            if (punk == IntPtr.Zero) return null;
            try
            {
                Guid iidSP = typeof(IServiceProvider).GUID;
                IntPtr spPtr;
                if (Marshal.QueryInterface(punk, ref iidSP, out spPtr) != 0 || spPtr == IntPtr.Zero) return null;
                try
                {
                    IServiceProvider sp = (IServiceProvider)Marshal.GetObjectForIUnknown(spPtr);
                    Guid sid = SID_STopLevelBrowser;
                    Guid iidSB = typeof(IShellBrowser).GUID;
                    IntPtr sbPtr;
                    if (sp.QueryService(ref sid, ref iidSB, out sbPtr) != 0 || sbPtr == IntPtr.Zero) return null;
                    try
                    {
                        return (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
                    }
                    finally
                    {
                        Marshal.Release(sbPtr);
                    }
                }
                finally
                {
                    Marshal.Release(spPtr);
                }
            }
            finally
            {
                Marshal.Release(punk);
            }
        }

        private static string GetClassNameStr(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string RawName(IntPtr pidl, int sigdn)
        {
            if (pidl == IntPtr.Zero) return null;
            IntPtr nm;
            if (SHGetNameFromIDList(pidl, sigdn, out nm) != 0 || nm == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(nm); }
            finally { CoTaskMemFree(nm); }
        }

        private static void InitLogPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                string path = Path.Combine(dir, "PropriaClick.log");
                using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                _logPath = path;
            }
            catch
            {
                _logPath = Path.Combine(Path.GetTempPath(), "PropriaClick.log");
            }
        }

        // Logging is off the hook path, so it writes directly. Only failures and exceptions
        // are recorded; a normal run leaves the log empty.
        private static void Log(string message)
        {
            if (_logPath == null) return;
            try
            {
                lock (_logLock)
                {
                    FileInfo info = new FileInfo(_logPath);
                    if (info.Exists && info.Length > MAX_LOGGED_SIZE) File.WriteAllText(_logPath, string.Empty);
                    File.AppendAllText(_logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
