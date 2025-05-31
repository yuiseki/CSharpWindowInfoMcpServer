以下では、AI エージェントが現在ユーザーが実行中のアプリやウィンドウ情報を取得できるようにするための MCP サーバー実装アプローチを段階的に示します。最初に全体像を要約し、その後で具体的な技術仕様や API 呼び出し、MCP ツールとしての設計、実装例、テスト方法までを詳細に解説します。

## 概要

本アプローチでは、C# の MCP サーバー上で Windows ネイティブ API を P/Invoke 経由で呼び出し、次の２つを実現します。

1. **稼働中のウィンドウ・プロセス情報の列挙**：`EnumWindows`、`GetWindowThreadProcessId`、`GetWindowText`、`IsWindowVisible` などを使い、現在デスクトップ上で表示されているすべてのトップレベルウィンドウを取得し、それに紐づくプロセス ID・プロセス名・ウィンドウタイトルなどを収集します。([Stack Overflow][1], [Neal's Blog][2])
2. **MCP ツールとして情報を返却**：収集したウィンドウ情報を JSON 形式でシリアライズし、AI エージェントがツール呼び出しを通じて「現在のウィンドウ一覧を取得して」と問い合わせるたびにレスポンスとして返せる MCP ツールを実装します。([Microsoft for Developers][3], [GitHub][4])

AI エージェントはこの MCP ツールを呼び出すことで、ユーザーの作業コンテキスト（どのアプリを開いているか、どのウィンドウがアクティブか、各ウィンドウのタイトルなど）をリアルタイムに把握し、例えば「ブラウザが開いているタブに応じたサジェストを表示する」「特定のアプリケーションが起動されたら次のステップを提案する」といった高度なサポートを提供できるようになります([The Verge][5])。以下では、この実装を段階的に示します。

---

## 1. MCP サーバー基盤の準備

### 1.1 プロジェクト作成と依存パッケージ導入

1. **コンソールアプリの作成**

   ```bash
   dotnet new console -n WindowInfoMcpServer
   cd WindowInfoMcpServer
   ```

   これにより、`WindowInfoMcpServer` という名前の .NET コンソールプロジェクトが作成されます。([Microsoft for Developers][3])

2. **MCP C# SDK とホスティング用パッケージの追加**

   ```bash
   dotnet add package ModelContextProtocol --prerelease
   dotnet add package Microsoft.Extensions.Hosting
   ```

   - `ModelContextProtocol` は MCP サーバー機能を提供する公式 C# SDK です([Microsoft for Developers][3], [GitHub][4])。
   - `Microsoft.Extensions.Hosting` はバックグラウンドホスト構築のために使用します([Microsoft for Developers][3], [laurentkempe.com][6])。

### 1.2 `Program.cs` の基本構成

次に、MCP サーバーを起動する最低限のコードを記述します。

```csharp
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace WindowInfoMcpServer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                })
                .ConfigureServices((context, services) =>
                {
                    // MCPサーバー機能を登録し、アセンブリ内のMcpToolをスキャン
                    services.AddMcpServer(options =>
                    {
                        options.ScanAssemblies(typeof(Program).Assembly);
                    });
                })
                .Build();

            System.Console.WriteLine("WindowInfoMcpServer: 起動完了。ツール呼び出しを待機中...");
            await host.RunAsync();
        }
    }
}
```

- `AddMcpServer` を呼び出すことで、SDK が提供する MCP サーバー機能を DI コンテナに登録します([Microsoft for Developers][3], [GitHub][4])。
- `ScanAssemblies` に現在のアセンブリを渡すことで、同一アセンブリ内にある `[McpServerToolType]` 属性付きクラス・メソッドを自動検出し、ツールとして登録します([Microsoft for Developers][3], [laurentkempe.com][6])。

---

## 2. Windows ネイティブ API を使ったウィンドウ／プロセス情報取得

ここでは、Win32 API を P/Invoke 経由で呼び出し、現在表示中のトップレベルウィンドウとそれに紐づくプロセス情報を取得する方法を解説します。

### 2.1 必要な P/Invoke シグネチャ

以下の Win32 API を利用します。各メソッドを `User32.dll` からインポートします。

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowInfoMcpServer
{
    internal static class NativeMethods
    {
        // EnumWindows: 全トップレベルウィンドウを列挙
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        // get プロセスID
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        // ウィンドウタイトルの文字列長取得
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        // ウィンドウタイトル取得
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        // ウィンドウが表示中かどうか確認
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
```

- **EnumWindows**：すべてのトップレベルウィンドウのハンドル (HWND) を列挙し、ユーザー定義コールバック `EnumWindowsProc` に通知します。([Stack Overflow][1], [Neal's Blog][2])
- **GetWindowThreadProcessId**：指定したウィンドウハンドルに紐づくプロセス ID を取得します。([Stack Overflow][1], [Neal's Blog][2])
- **GetWindowTextLength/GetWindowText**：ウィンドウタイトルの長さを取り、実際の文字列を取得します([Reddit][7], [PInvoke][8])。
- **IsWindowVisible**：非表示ウィンドウ（最小化状態や裏に隠れているウィンドウなど）を除外するのに使います([Neal's Blog][2])。

### 2.2 ウィンドウ・プロセス情報を収集するロジック

これらのネイティブ API を組み合わせ、すべてのトップレベルウィンドウで以下を行います。

1. `IsWindowVisible` が `true` の場合のみ処理を続行（可視状態のウィンドウだけを対象）。
2. `GetWindowTextLength` でタイトル長を取得し、`GetWindowText` でタイトル文字列を取得。
3. `GetWindowThreadProcessId` で取得したプロセス ID を使い、`System.Diagnostics.Process.GetProcessById()` からプロセス名を取得。

以下は、上記の一連の処理をまとめたサンプルメソッド例です。

```csharp
namespace WindowInfoMcpServer
{
    public class WindowEnumerator
    {
        public class WindowInfo
        {
            public IntPtr Handle { get; set; }
            public string Title { get; set; }
            public uint ProcessId { get; set; }
            public string ProcessName { get; set; }
        }

        public static List<WindowInfo> GetAllVisibleWindows()
        {
            var windows = new List<WindowInfo>();

            // コールバック定義
            NativeMethods.EnumWindowsProc callback = (hWnd, lParam) =>
            {
                // 表示中のウィンドウのみ対象
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true; // true を返すことで列挙を継続

                // ウィンドウタイトル長を取得
                int length = NativeMethods.GetWindowTextLength(hWnd);
                var titleBuilder = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                string windowTitle = titleBuilder.ToString();

                // タイトルが空（たとえば非インタラクティブのデスクトップウィンドウなど）の場合は無視
                if (string.IsNullOrWhiteSpace(windowTitle))
                    return true;

                // プロセスID取得
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);

                // プロセス名を取得。例外処理を含めると堅牢
                string processName;
                try
                {
                    processName = Process.GetProcessById((int)pid).ProcessName;
                }
                catch
                {
                    processName = "<Unknown>";
                }

                windows.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = windowTitle,
                    ProcessId = pid,
                    ProcessName = processName
                });

                return true;
            };

            // 列挙実行
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            return windows;
        }
    }
}
```

- `IsWindowVisible` で可視ウィンドウのみ収集し、タスクバーに表示されているアプリケーションウィンドウをざっくり対象とします([Neal's Blog][2], [PInvoke][8])。
- タイトルが空（`string.IsNullOrWhiteSpace`）のウィンドウはしばしばインターナルなもの（Invisible/Overlay/Utility Window など）なので除外しています。
- プロセス取得時には、既に終了している可能性や権限不足で例外が発生するため、`try-catch` でフォールバックを準備しています([Code Bude][9], [PInvoke][8])。

---

## 3. MCP ツールとして「ウィンドウ情報を返す」機能を実装

### 3.1 `[McpServerToolType]` および `[McpServerTool]` 属性の付与

MCP の C# SDK では、サーバーが提供するツールを定義するために、まず **ツールをまとめたクラス** に `[McpServerToolType]` 属性を付与します。該当クラス内のメソッドを `[McpServerTool]` としてマークすると、そのメソッドが TCP/STDIO などの MCP プロトコル経由で呼び出せるようになります([Microsoft for Developers][3], [GitHub][4])。

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace WindowInfoMcpServer
{
    [McpServerToolType]
    public static class WindowInfoTool
    {
        // ツール名は "list_windows" とし、戻り値は WindowInfo のリストをJSONで返却
        [McpServerTool(Name = "list_windows"), System.ComponentModel.Description("現在の可視ウィンドウ一覧を取得します。")]
        public static Task<string> ListWindowsAsync()
        {
            // WindowEnumerator.GetAllVisibleWindows() で収集した情報リストをシリアライズ
            List<WindowEnumerator.WindowInfo> windows = WindowEnumerator.GetAllVisibleWindows();
            // JSON文字列化
            string json = JsonSerializer.Serialize(windows);
            return Task.FromResult(json);
        }
    }
}
```

- `[McpServerTool(Name = "list_windows")]` とすると、AI エージェントは `"tool": "list_windows"` で本メソッドを呼び出せます([Microsoft for Developers][3], [GitHub][4])。
- 戻り値を `Task<string>` とし、JSON 文字列を返すだけのシンプル構成にしています。AI エージェント側でパースして表示やルールに利用する想定です。
- `System.Text.Json.JsonSerializer` を使い、.NET 標準のシリアライズ機能で `WindowInfo` オブジェクトのリストを JSON に変換しています。([Neal's Blog][2], [Microsoft for Developers][3])。

### 3.2 JSON スキーマ例

上記のツール呼び出し時に返却される JSON の例は以下のようになります。

```jsonc
[
  {
    "Handle": 123456,
    "Title": "Visual Studio 2022 - Program.cs",
    "ProcessId": 9876,
    "ProcessName": "devenv"
  },
  {
    "Handle": 789012,
    "Title": "Edge - Model Context Protocol Docs",
    "ProcessId": 1234,
    "ProcessName": "msedge"
  }
  // … 以下、省略 …
]
```

- `Handle`：ウィンドウハンドル (HWND) を整数として格納。
- `Title`：ウィンドウタイトル（タブ名やドキュメント名を含む文字列）。
- `ProcessId`：該当ウィンドウを所有するプロセス ID。
- `ProcessName`：該当プロセスの実行ファイル名（拡張子を除外）。

AI エージェントはこのリストを受け取り、「現在開いているブラウザタブ一覧」や「どのウィンドウがアクティブか」を把握し、たとえば「Visual Studio で編集中のファイルに対してコード補完を行う」「Edge のタブ名を解析して検索提案を出す」などのタスクを行うことが可能になります([The Verge][5])。

---

## 4. 実装全体の流れとコード構成例

ここまでの実装をまとめた上で、主要なソースコードファイル構成と内容の一例を示します。

```
WindowInfoMcpServer/
├─ Program.cs
├─ NativeMethods.cs
├─ WindowEnumerator.cs
└─ WindowInfoTool.cs
```

### 4.1 `Program.cs`

```csharp
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace WindowInfoMcpServer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddMcpServer(options =>
                    {
                        options.ScanAssemblies(typeof(Program).Assembly);
                    });
                })
                .Build();

            System.Console.WriteLine("WindowInfoMcpServer: 起動完了。list_windows ツール呼び出しを待機中...");
            await host.RunAsync();
        }
    }
}
```

- MCP サーバーとして登録し、同一アセンブリに存在するすべての `McpServerTool` メソッドを自動検出します([Microsoft for Developers][3], [laurentkempe.com][6])。

### 4.2 `NativeMethods.cs`

```csharp
using System;
using System.Runtime.InteropServices;

namespace WindowInfoMcpServer
{
    internal static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
```

- Win32 API を P/Invoke でインポートし、ウィンドウ列挙・タイトル取得・可視判定・プロセス ID 取得を可能にします([Stack Overflow][1], [Neal's Blog][2])。

### 4.3 `WindowEnumerator.cs`

```csharp
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
            public IntPtr Handle { get; set; }
            public string Title { get; set; }
            public uint ProcessId { get; set; }
            public string ProcessName { get; set; }
        }

        public static List<WindowInfo> GetAllVisibleWindows()
        {
            var windows = new List<WindowInfo>();

            NativeMethods.EnumWindowsProc callback = (hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;

                int length = NativeMethods.GetWindowTextLength(hWnd);
                var titleBuilder = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                string windowTitle = titleBuilder.ToString();

                if (string.IsNullOrWhiteSpace(windowTitle))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);

                string processName;
                try
                {
                    processName = Process.GetProcessById((int)pid).ProcessName;
                }
                catch
                {
                    processName = "<Unknown>";
                }

                windows.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = windowTitle,
                    ProcessId = pid,
                    ProcessName = processName
                });

                return true;
            };

            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            return windows;
        }
    }
}
```

- P/Invoke で取得した情報を収集し、`WindowInfo` オブジェクトとしてリスト化します([Neal's Blog][2], [PInvoke][8])。

### 4.4 `WindowInfoTool.cs`

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace WindowInfoMcpServer
{
    [McpServerToolType]
    public static class WindowInfoTool
    {
        [McpServerTool(Name = "list_windows"), System.ComponentModel.Description("現在の可視ウィンドウ一覧を取得します。")]
        public static Task<string> ListWindowsAsync()
        {
            List<WindowEnumerator.WindowInfo> windows = WindowEnumerator.GetAllVisibleWindows();
            string json = JsonSerializer.Serialize(windows);
            return Task.FromResult(json);
        }
    }
}
```

- MCP ツールとして `list_windows` を定義し、呼び出されるとリアルタイムでウィンドウ情報を JSON で返します([Microsoft for Developers][3], [Reddit][10])。

---

## 5. テストと動作確認

### 5.1 MCP Inspector による手動テスト

1. **MCP Inspector の準備**

   - [MCP C# SDK の GitHub リポジトリ](https://github.com/modelcontextprotocol/csharp-sdk) には、MCP サーバーを簡単にテストできるブラウザベースの “MCP Inspector” が同梱されています([GitHub][4], [GitHub][11])。

2. **サーバー起動**

   ```bash
   dotnet run --project WindowInfoMcpServer.csproj
   ```

   コンソールに「WindowInfoMcpServer: 起動完了。list_windows ツール呼び出しを待機中...」と表示されます。

3. **ブラウザで Inspector に接続**

   - デフォルトでは STDIO ではなく HTTP ポートを用いる設定が必要です。`Program.cs` の `AddMcpServer` オプションを修正し、`WithHttpServerTransport` を追加してポートを指定します。たとえば、ポート 5000 を使う場合は以下のように変更します。

   ```csharp
   // ConfigureServices メソッド内
   services.AddMcpServer(options =>
   {
       options.ScanAssemblies(typeof(Program).Assembly);
       options.WithHttpServerTransport(5000); // ポート 5000 でリッスン
   });
   ```

   - サーバー再起動後、ブラウザで `http://localhost:5000` にアクセス。
   - Inspector の UI 上で “list_windows” ツールを選択し、実行すると JSON レスポンスが表示されるとともに、ブラウザ上で整形されたリストが見えます。([Microsoft for Developers][3], [laurentkempe.com][6])。

### 5.2 cURL による動作確認

HTTP エンドポイントを使った場合、以下のように cURL で叩くことも可能です。

```bash
curl -X POST http://localhost:5000/tools/list_windows -H "Content-Type: application/json" -d '{}'
```

- 上記コマンドでは `"parameters"` が不要なため、空の JSON オブジェクトを投げれば OK です。レスポンスにはウィンドウ情報の JSON 配列が返ってきます。([laurentkempe.com][6], [Microsoft for Developers][3])。

---

## 6. AI エージェントからの利用イメージ

1. **ユーザーが AI に指示**

   - 例）「今開いているアプリケーション一覧を教えて」
   - AI は LLM のプロンプト内で `call_tool` フォーマットを生成し、以下の JSON を MCP サーバーに送信します。

     ```jsonc
     {
       "tool": "list_windows",
       "parameters": {}
     }
     ```

   ([Medium][12], [Microsoft for Developers][13])

2. **MCP サーバーが `ListWindowsAsync` を実行**

   - 上述の P/Invoke コードで可視ウィンドウを列挙し、プロセス情報を含む JSON 文字列を生成します。
   - 例）戻り値

     ```jsonc
     [
       {
         "Handle": 123456,
         "Title": "Visual Studio 2022 - Program.cs",
         "ProcessId": 9876,
         "ProcessName": "devenv"
       },
       {
         "Handle": 789012,
         "Title": "Edge - Model Context Protocol Docs",
         "ProcessId": 1234,
         "ProcessName": "msedge"
       }
     ]
     ```

   ([Neal's Blog][2], [Stack Overflow][1])

3. **AI エージェントが結果を処理**

   - AI は受け取った JSON をパースし、自然言語で「現在開いているウィンドウは以下の通りです：1. Visual Studio (プロセス名: devenv)、2. Edge (プロセス名: msedge) …」とユーザーに回答できます。
   - さらに「Visual Studio で `Program.cs` を編集中ですね。次にどのようなヘルプが必要ですか？」など、コンテキストを踏まえた高度な提案を行えます([The Verge][5])。

---

## 7. 拡張アイデアと注意点

### 7.1 フィルタリングやソートの追加

- `ListWindowsAsync` にパラメータを追加し、「プロセス名が含まれるものだけ取得」「ウィンドウタイトルで絞り込み」「起動時間順に並べる」などを実装してもよいでしょう。
- 例）MCP ツール定義を以下のように変更し、パラメータを受け取る。

  ```csharp
  public class WindowFilterParams
  {
      public string ProcessNameContains { get; set; }
      public string TitleContains { get; set; }
  }

  [McpServerTool(Name = "filter_windows")]
  public static Task<string> FilterWindowsAsync(WindowFilterParams filter)
  {
      var all = WindowEnumerator.GetAllVisibleWindows();
      var filtered = all
          .Where(w => string.IsNullOrEmpty(filter.ProcessNameContains) || w.ProcessName.Contains(filter.ProcessNameContains, StringComparison.OrdinalIgnoreCase))
          .Where(w => string.IsNullOrEmpty(filter.TitleContains) || w.Title.Contains(filter.TitleContains, StringComparison.OrdinalIgnoreCase))
          .ToList();
      string json = JsonSerializer.Serialize(filtered);
      return Task.FromResult(json);
  }
  ```

  これにより、AI エージェントは `"tool":"filter_windows","parameters":{"processNameContains":"chrome","titleContains":"Gmail"}` のように呼び出せます。

### 7.2 権限とセキュリティ

- 一部のプロセスやウィンドウは、システム権限や別ユーザーセッションのために列挙・情報取得できない場合があります。その場合、例外処理や結果に `<Unknown>` を入れるなどしてフォールバックします([Stack Overflow][1], [Code Bude][9])。
- 公開サーバーとして外部に公開するなら、認証・認可を導入し、「ローカルからのみ呼び出し可能」「特定のトークン保持者のみアクセス可」などの制限をおこないましょう。

### 7.3 ユーザー通知との連携

- 先に構築した「Toast 通知ツール」と組み合わせ、AI が「指定のウィンドウタイトルがアクティブになったら通知してほしい」といったリクエストにも対応できます。

  1. AI が `"tool":"monitor_window_active","parameters":{"title":"Outlook - 受信トレイ"}}` のように指示。
  2. MCP サーバー側では別タスクで定期的に `EnumWindows` を呼び出し、指定タイトルのウィンドウが表示されたら `notify` ツールを使って Toast 通知。

- 上記にはタイマー機構（例：`System.Threading.Timer`）やバックグラウンドホスティング (`IHostedService`) の実装が必要ですが、ユーザー体験が大きく向上します。([The Verge][5])。

### 7.4 パフォーマンスに関する考慮

- 毎回 `EnumWindows` を呼び出すとコストがかかるため、頻繁に呼び出す用途ではキャッシュや差分検出ロジックを検討してください。
- たとえば、1 秒ごとに列挙して前回の結果と異なる部分だけを通知するようにすれば、AI エージェントへのイベント通知フローを高速化できます。

---

## 8. まとめ

- **全体構成**：C# の MCP サーバーに Win32 API を P/Invoke で組み込み、`EnumWindows` ～ `GetWindowText` ～ `GetWindowThreadProcessId` などを用いて可視ウィンドウ情報を取得する。([Neal's Blog][2], [Stack Overflow][1])
- **MCP ツール実装**：取得したウィンドウ情報を JSON にシリアライズし、`[McpServerTool(Name="list_windows")]` の形で公開。AI エージェントは `"tool":"list_windows"` を呼び出してリアルタイム情報を得られる。([Microsoft for Developers][3], [GitHub][4])
- **AI エージェントの活用例**：

  - 現在開いているアプリ・ウィンドウ群を把握し、そのコンテキストに応じたサジェストを行う
  - 特定のウィンドウが起動したらトリガーとして通知や別ツール起動を行う
  - 作業中のウィンドウタイトルからタスクを推測し、自動化フローを提案する
    ([The Verge][5])

- **拡張性**：フィルタリング、ソート、バックグラウンド監視、通知連携、認証強化などによって、柔軟にカスタマイズ可能。

以上を参考に、C# と MCP を組み合わせた「ウィンドウ・プロセス情報取得サーバー」を構築し、AI エージェントと協調して動作コンテキストをリアルタイムに捉える高度なソリューションを開発してみてください。

---
