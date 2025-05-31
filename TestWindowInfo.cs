using System;
using System.Collections.Generic;

namespace WindowInfoMcpServer
{
    public class TestWindowInfo
    {
        public static void TestMain()
        {
            Console.WriteLine("ウィンドウ情報取得テスト開始...");
            
            try
            {
                List<WindowEnumerator.WindowInfo> windows = WindowEnumerator.GetAllVisibleWindows();
                
                Console.WriteLine($"取得されたウィンドウ数: {windows.Count}");
                
                foreach (var window in windows)
                {
                    Console.WriteLine($"ハンドル: {window.Handle}");
                    Console.WriteLine($"タイトル: {window.Title}");
                    Console.WriteLine($"プロセスID: {window.ProcessId}");
                    Console.WriteLine($"プロセス名: {window.ProcessName}");
                    Console.WriteLine($"アプリケーション名: {window.ApplicationName}");
                    Console.WriteLine($"会社名: {window.CompanyName}");
                    Console.WriteLine("---");
                }
                
                // JSON シリアライズテスト
                string json = System.Text.Json.JsonSerializer.Serialize(windows);
                Console.WriteLine($"JSON出力長: {json.Length} 文字");
                
                Console.WriteLine("テスト完了!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"エラー: {ex.Message}");
                Console.WriteLine($"スタックトレース: {ex.StackTrace}");
            }
        }
    }
}
