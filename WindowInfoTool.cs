using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace WindowInfoMcpServer
{
    [McpServerToolType]
    public static class WindowInfoTool
    {
        [McpServerTool, Description("現在の可視ウィンドウ一覧を取得します。")]
        public static Task<string> ListWindows()
        {
            // WindowEnumerator.GetAllVisibleWindows() で収集した情報リストをシリアライズ
            List<WindowEnumerator.WindowInfo> windows = WindowEnumerator.GetAllVisibleWindows();
            // JSON文字列化
            string json = JsonSerializer.Serialize(windows);
            return Task.FromResult(json);
        }
    }
}
