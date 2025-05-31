using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace WindowInfoMcpServer
{
    public class PipeMcpTest
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine("=== パイプ経由MCPテスト ===");
            
            // MCPサーバーのプロセスを起動
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "run --project WindowInfoMcpServer.csproj",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory
                }
            };

            try
            {
                process.Start();
                Console.WriteLine($"MCPサーバー開始 (PID: {process.Id})");

                // エラー出力を監視
                var errorTask = Task.Run(async () =>
                {
                    using var reader = process.StandardError;
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        Console.WriteLine($"[STDERR] {line}");
                    }
                });

                // 標準出力を監視
                var outputTask = Task.Run(async () =>
                {
                    using var reader = process.StandardOutput;
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        Console.WriteLine($"[STDOUT] {line}");
                    }
                });

                // 少し待ってからメッセージを送信
                await Task.Delay(2000);

                // initializeメッセージを送信
                var initMessage = new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { } },
                        clientInfo = new { name = "pipe-test", version = "1.0.0" }
                    }
                };

                var initJson = JsonSerializer.Serialize(initMessage);
                Console.WriteLine($"送信: {initJson}");
                await process.StandardInput.WriteLineAsync(initJson);
                await process.StandardInput.FlushAsync();

                // 応答を待つ
                await Task.Delay(2000);

                // tools/listを送信
                var toolsMessage = new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/list",
                    @params = new { }
                };

                var toolsJson = JsonSerializer.Serialize(toolsMessage);
                Console.WriteLine($"送信: {toolsJson}");
                await process.StandardInput.WriteLineAsync(toolsJson);
                await process.StandardInput.FlushAsync();

                // 応答を待つ
                await Task.Delay(2000);

                // tools/callを送信
                var callMessage = new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "tools/call",
                    @params = new
                    {
                        name = "ListWindows",
                        arguments = new { }
                    }
                };

                var callJson = JsonSerializer.Serialize(callMessage);
                Console.WriteLine($"送信: {callJson}");
                await process.StandardInput.WriteLineAsync(callJson);
                await process.StandardInput.FlushAsync();

                // 最終応答を待つ
                await Task.Delay(5000);

                Console.WriteLine("テスト完了");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"エラー: {ex.Message}");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
                process.Dispose();
            }
        }
    }
}
