# WindowInfoMcpServer Stdio テスト

このプロジェクトには、WindowInfoMcpServer の stdio 通信をテストするためのテストプログラムが含まれています。

## テストプログラム

### PipeMcpTest.cs

パイプ経由で MCP サーバーとの通信をテストする実用的なテストプログラム

## テスト実行方法

```powershell
dotnet run --project PipeMcpTest.csproj
```

## テスト内容

### 1. 初期化シーケンス

- `initialize` リクエストの送信
- サーバーの capabilities 確認
- `notifications/initialized` 通知の送信

### 2. ツール一覧取得

- `tools/list` リクエストの送信
- 利用可能なツールの確認

### 3. ツール呼び出し

- `tools/call` リクエストで ListWindows ツールを実行
- ウィンドウ情報の取得と検証

## 期待される結果

テストが成功すると以下のような出力が表示されます：

```
=== パイプ経由MCPテスト ===
MCPサーバー開始 (PID: XXXX)
送信: {"jsonrpc":"2.0","id":1,"method":"initialize",...}
[STDOUT] {"result":{"capabilities":...},"id":1,"jsonrpc":"2.0"}
送信: {"jsonrpc":"2.0","id":2,"method":"tools/list",...}
[STDOUT] {"result":{"tools":[{"name":"ListWindows",...}]},"id":2,"jsonrpc":"2.0"}
送信: {"jsonrpc":"2.0","id":3,"method":"tools/call",...}
[STDOUT] {"result":{"content":[{"type":"text","text":"[ウィンドウ情報のJSON]"}]},"id":3,"jsonrpc":"2.0"}
```

MCP サーバーの stdio 通信テスト開始...

## テスト成功の確認ポイント

✅ **MCP プロトコルの動作確認**

- Initialize handshake
- Tools/list (ListWindows ツールの発見)
- Tools/call (ウィンドウ情報の取得)

✅ **取得されるウィンドウ情報**

- 現在開いているアプリケーション一覧
- プロセス名、タイトル、会社名などの詳細情報
- JSON 形式でのシリアライズ

## トラブルシューティング

### MCP サーバーが応答しない場合

- `Program.cs` で余計な標準出力が発生していないか確認
- 複数のエントリーポイント（Main メソッド）が競合していないか確認

### プロセス起動エラー

- `WindowInfoMcpServer.csproj` が同じディレクトリに存在することを確認
- .NET 9.0 SDK がインストールされていることを確認

### ウィンドウ情報が取得できない場合

- 管理者権限でテストを実行してみてください
- セキュリティソフトによってプロセス情報へのアクセスがブロックされている可能性があります
