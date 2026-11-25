using System;
using System.IO;
using System.Text;
using VideoTools.Automation;
using VideoTools.Automation.Mcp;

namespace VideoTools.Mcp
{
    /// <summary>
    /// MCP stdio 服务入口。stdout 只输出 JSON-RPC，日志一律走 stderr。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                var first = args[0].ToLowerInvariant();
                if (first == "--version" || first == "-v")
                {
                    Console.WriteLine("videotools-mcp 1.0.0");
                    return 0;
                }
                if (first == "--help" || first == "-h")
                {
                    Console.WriteLine("videotools-mcp：VideoTools 的 MCP stdio 服务，由 AI 客户端自动启动，无需手动运行。");
                    return 0;
                }
                if (first == "--selftest")
                {
                    var service = new AutomationService(Environment.GetEnvironmentVariable("VIDEOTOOLS_FFMPEG"));
                    var result = service.Execute("capabilities", null, null, System.Threading.CancellationToken.None);
                    Console.WriteLine(Json.Serialize(result));
                    return 0;
                }
            }

            var stdout = Console.OpenStandardOutput();
            Console.SetOut(Console.Error);

            try
            {
                var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
                var service = new AutomationService(Environment.GetEnvironmentVariable("VIDEOTOOLS_FFMPEG"));
                new McpServer(reader, writer, service).Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MCP 服务异常退出：" + ex);
                return 1;
            }
        }
    }
}
