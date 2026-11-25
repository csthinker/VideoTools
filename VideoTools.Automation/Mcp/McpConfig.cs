using System;
using System.Collections.Generic;
using System.IO;

namespace VideoTools.Automation.Mcp
{
    /// <summary>
    /// 生成 Cursor / Claude Code / Codex 的 MCP 连接配置。
    /// </summary>
    public static class McpConfig
    {
        public const string ServerName = "videotools";

        public static Dictionary<string, object> BuildServerEntry(string ffmpegPath = null)
        {
            var entry = new Dictionary<string, object>
            {
                { "command", AppPaths.FindMcpExecutable() },
                { "args", new List<object>() }
            };

            var env = new Dictionary<string, object>();
            var resolvedFfmpeg = !string.IsNullOrWhiteSpace(ffmpegPath) ? ffmpegPath : FfmpegLocator.Resolve(new IniConfig());
            if (!string.IsNullOrWhiteSpace(resolvedFfmpeg)) env["VIDEOTOOLS_FFMPEG"] = resolvedFfmpeg;
            var dataDir = Environment.GetEnvironmentVariable("VIDEOTOOLS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(dataDir)) env["VIDEOTOOLS_DATA_DIR"] = dataDir;
            if (env.Count > 0) entry["env"] = env;

            return entry;
        }

        /// <summary>Cursor / Claude Code / 通用客户端可直接使用的 JSON。</summary>
        public static string BuildClientJson(string ffmpegPath = null)
        {
            var config = new Dictionary<string, object>
            {
                {
                    "mcpServers", new Dictionary<string, object>
                    {
                        { ServerName, BuildServerEntry(ffmpegPath) }
                    }
                }
            };
            return Json.Serialize(config);
        }

        /// <summary>Codex 的 TOML 配置片段。</summary>
        public static string BuildCodexToml(string ffmpegPath = null)
        {
            var entry = BuildServerEntry(ffmpegPath);
            var command = Convert.ToString(entry["command"]);
            var lines = new List<string>
            {
                "[mcp_servers." + ServerName + "]",
                "command = \"" + EscapeToml(command) + "\"",
                "args = []"
            };

            var env = Json.AsObject(entry.ContainsKey("env") ? entry["env"] : null);
            if (env != null && env.Count > 0)
            {
                var pairs = new List<string>();
                foreach (var pair in env)
                {
                    pairs.Add(pair.Key + " = \"" + EscapeToml(Convert.ToString(pair.Value)) + "\"");
                }
                lines.Add("env = { " + string.Join(", ", pairs.ToArray()) + " }");
            }
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        public static string BuildClaudeAddCommand(string ffmpegPath = null)
        {
            var entry = BuildServerEntry(ffmpegPath);
            var command = "claude mcp add " + ServerName + " -- \"" + Convert.ToString(entry["command"]) + "\"";
            var env = Json.AsObject(entry.ContainsKey("env") ? entry["env"] : null);
            if (env != null && env.ContainsKey("VIDEOTOOLS_FFMPEG"))
            {
                command = "claude mcp add " + ServerName + " -e VIDEOTOOLS_FFMPEG=\"" +
                          Convert.ToString(env["VIDEOTOOLS_FFMPEG"]) + "\" -- \"" + Convert.ToString(entry["command"]) + "\"";
            }
            return command;
        }

        private static string EscapeToml(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
