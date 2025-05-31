using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace WindowInfoMcpServer
{
    public class WindowEnumerator
    {
        public class WindowInfo
        {
            public long Handle { get; set; } // IntPtrではなくlongに変更
            public string Title { get; set; } = string.Empty;
            public uint ProcessId { get; set; }
            public string ProcessName { get; set; } = string.Empty;
            public string ApplicationName { get; set; } = string.Empty; // 正式なアプリケーション名
            public string CompanyName { get; set; } = string.Empty; // 開発会社名
        }

        public static List<WindowInfo> GetAllVisibleWindows()
        {
            var windows = new List<WindowInfo>();

            // コールバック定義
            NativeMethods.EnumWindowsProc callback = (hWnd, lParam) =>
            {
                // ユーザーに表示されるべきウィンドウかどうかをチェック
                if (!IsUserVisibleWindow(hWnd))
                    return true; // true を返すことで列挙を継続

                // ウィンドウタイトル長を取得
                int length = NativeMethods.GetWindowTextLength(hWnd);
                var titleBuilder = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                string windowTitle = titleBuilder.ToString();

                // タイトルが空の場合は無視
                if (string.IsNullOrWhiteSpace(windowTitle))
                    return true;

                // ウィンドウクラス名を取得してシステムウィンドウを除外
                var classNameBuilder = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, classNameBuilder, classNameBuilder.Capacity);
                string className = classNameBuilder.ToString();

                // 除外すべきクラス名をチェック
                if (IsSystemWindowClass(className))
                    return true;

                // プロセスID取得
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);

                // プロセス名を取得。例外処理を含めると堅牢
                string processName;
                string applicationName = string.Empty;
                string companyName = string.Empty;
                try
                {
                    var process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName;

                    // 実行ファイルのパスからアプリケーション情報を取得
                    try
                    {
                        string executablePath = process.MainModule?.FileName ?? string.Empty;
                        if (!string.IsNullOrEmpty(executablePath))
                        {
                            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                            applicationName = versionInfo.ProductName ?? string.Empty;
                            companyName = versionInfo.CompanyName ?? string.Empty;
                            
                            // 空の場合はファイル名から推測
                            if (string.IsNullOrEmpty(applicationName))
                            {
                                applicationName = GetFriendlyApplicationName(processName);
                            }
                        }
                    }
                    catch
                    {
                        // バージョン情報の取得に失敗した場合はプロセス名から推測
                        applicationName = GetFriendlyApplicationName(processName);
                    }

                    // システムプロセスを除外
                    if (IsSystemProcess(processName))
                        return true;

                    // ユーザーアプリケーションでないものを除外
                    if (!IsUserApplication(processName, windowTitle))
                        return true;
                }
                catch
                {
                    return true; // プロセス情報が取得できない場合は除外
                }

                windows.Add(new WindowInfo
                {
                    Handle = hWnd.ToInt64(), // IntPtrをlongに変換
                    Title = windowTitle,
                    ProcessId = pid,
                    ProcessName = processName,
                    ApplicationName = applicationName,
                    CompanyName = companyName
                });

                return true;
            };

            // 列挙実行
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            return windows;
        }

        private static bool IsUserVisibleWindow(IntPtr hWnd)
        {
            // 可視でないウィンドウを除外
            if (!NativeMethods.IsWindowVisible(hWnd))
                return false;

            // 最小化されたウィンドウは含める（タスクバーに表示されるため）
            // if (NativeMethods.IsIconic(hWnd))
            //     return false;

            // 拡張スタイルを取得
            uint exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);

            // ツールウィンドウは除外（ただし、WS_EX_APPWINDOWが設定されている場合は含める）
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && 
                (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
                return false;

            // 所有者のあるウィンドウは通常ダイアログなので除外（ただし、WS_EX_APPWINDOWが設定されている場合は含める）
            IntPtr owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
            if (owner != IntPtr.Zero && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
                return false;

            return true;
        }

        private static bool IsSystemWindowClass(string className)
        {
            // システムウィンドウクラスを除外
            string[] systemClasses = {
                "Shell_TrayWnd",        // タスクバー
                "DV2ControlHost",       // デスクトップウィンドウマネージャー
                "MsgrIMEWindowClass",   // IME
                "SysShadow",           // ウィンドウの影
                "Button",              // システムボタン
                "WorkerW",             // デスクトップワーカー
                "Progman",             // プログラムマネージャー
                "DWMSystemMonitorWindow" // DWMシステムモニター
            };

            return Array.Exists(systemClasses, sc => className.Contains(sc, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSystemProcess(string processName)
        {
            // システムプロセスを除外
            string[] systemProcesses = {
                "dwm",           // Desktop Window Manager
                "winlogon",      // Windows ログオン
                "csrss",         // Client Server Runtime Subsystem
                "smss",          // Session Manager Subsystem
                "wininit",       // Windows Initialization
                "services",      // Service Control Manager
                "lsass",         // Local Security Authority Subsystem
                "conhost",       // Console Window Host
                "RuntimeBroker", // Runtime Broker
                "SearchApp",     // Windows Search
                "StartMenuExperienceHost", // スタートメニュー
                "ShellExperienceHost",     // シェル体験ホスト
                "ApplicationFrameHost",    // アプリケーションフレームホスト
                "TextInputHost", // Windows 入力エクスペリエンス
                "SecurityHealthSystray",   // Windows セキュリティ
                "Video.UI",      // 映画 & テレビアプリ
                "Microsoft.Photos", // フォトアプリ
                "LockApp",       // ロック画面アプリ
                "WinStore.App",  // Microsoft Store
                "UserOOBEBroker" // ユーザー体験ブローカー
            };

            return Array.Exists(systemProcesses, sp => processName.Equals(sp, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUserApplication(string processName, string windowTitle)
        {
            // 明らかにユーザーアプリケーションと判断できるもの
            string[] userApplicationProcesses = {
                "Code",          // Visual Studio Code
                "devenv",        // Visual Studio
                "chrome",        // Google Chrome
                "firefox",       // Mozilla Firefox
                "brave",         // Brave Browser
                "msedge",        // Microsoft Edge
                "notepad",       // メモ帳
                "notepad++",     // Notepad++
                "explorer",      // ファイルエクスプローラー
                "cmd",           // コマンドプロンプト
                "powershell",    // PowerShell
                "WindowsTerminal", // Windows Terminal
                "Teams",         // Microsoft Teams
                "Slack",         // Slack
                "Discord",       // Discord
                "Spotify",       // Spotify
                "vlc",           // VLC Media Player
                "winword",       // Microsoft Word
                "excel",         // Microsoft Excel
                "powerpnt",      // Microsoft PowerPoint
                "outlook",       // Microsoft Outlook
                "steam",         // Steam
                "Skype"          // Skype
            };

            // プロセス名での判定
            if (Array.Exists(userApplicationProcesses, app => 
                processName.Equals(app, StringComparison.OrdinalIgnoreCase)))
                return true;

            // Windows標準アプリは基本的に除外（ただし、設定アプリなど一部は含める）
            string[] includedSystemApps = {
                "SystemSettings", // 設定
                "Calculator",     // 電卓
                "mspaint",        // ペイント
                "SnippingTool"    // Snipping Tool
            };

            if (Array.Exists(includedSystemApps, app => 
                processName.Equals(app, StringComparison.OrdinalIgnoreCase)))
                return true;

            // UWPアプリやその他のシステムアプリは除外
            string[] excludedProcesses = {
                "RtkUWP",        // Realtek Audio Console
                "Nahimic3",      // Nahimic
                "Video.UI",      // 映画 & テレビ
                "WinStore.App",  // Microsoft Store
                "HxOutlook",     // Outlook (UWP)
                "Microsoft.Photos", // フォト
                "Cortana",       // Cortana
                "SearchUI",      // 検索UI
                "dllhost",       // COM Surrogate
                "svchost"        // Service Host
            };

            if (Array.Exists(excludedProcesses, app => 
                processName.Equals(app, StringComparison.OrdinalIgnoreCase)))
                return false;

            // タイトルに基づく判定（ファイル名や明確なアプリ名があるかどうか）
            if (windowTitle.Contains(" - ") || // ファイル名 - アプリ名の形式
                windowTitle.Contains(".txt") || 
                windowTitle.Contains(".docx") ||
                windowTitle.Contains(".xlsx") ||
                windowTitle.Contains(".pdf") ||
                windowTitle.Length > 20) // 意味のあるタイトルがある
                return true;

            // その他は除外
            return false;
        }

        private static string GetFriendlyApplicationName(string processName)
        {
            // プロセス名から推測される正式なアプリケーション名のマッピング
            var processToAppNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Code", "Visual Studio Code" },
                { "devenv", "Visual Studio" },
                { "chrome", "Google Chrome" },
                { "firefox", "Mozilla Firefox" },
                { "brave", "Brave Browser" },
                { "msedge", "Microsoft Edge" },
                { "notepad", "Notepad" },
                { "notepad++", "Notepad++" },
                { "explorer", "File Explorer" },
                { "cmd", "Command Prompt" },
                { "powershell", "Windows PowerShell" },
                { "WindowsTerminal", "Windows Terminal" },
                { "Teams", "Microsoft Teams" },
                { "Slack", "Slack" },
                { "Discord", "Discord" },
                { "Spotify", "Spotify" },
                { "vlc", "VLC Media Player" },
                { "winword", "Microsoft Word" },
                { "excel", "Microsoft Excel" },
                { "powerpnt", "Microsoft PowerPoint" },
                { "outlook", "Microsoft Outlook" },
                { "steam", "Steam" },
                { "Skype", "Skype" },
                { "SystemSettings", "Settings" },
                { "Calculator", "Calculator" },
                { "mspaint", "Paint" },
                { "SnippingTool", "Snipping Tool" }
            };

            return processToAppNameMap.TryGetValue(processName, out var appName) ? appName : processName;
        }
    }
}
