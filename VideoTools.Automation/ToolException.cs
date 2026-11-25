using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VideoTools.Automation
{
    /// <summary>
    /// 带错误码的业务异常，CLI 会据此返回退出码，MCP 会返回 isError=true。
    /// </summary>
    public class ToolException : Exception
    {
        public ToolException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; private set; }

        public static ToolException InvalidArgument(string message)
        {
            return new ToolException("INVALID_ARGUMENT", message);
        }

        public static ToolException FfmpegNotFound()
        {
            return new ToolException("FFMPEG_NOT_FOUND", "未找到 ffmpeg。请先在软件设置中选择 ffmpeg 路径，或通过 --ffmpeg / 环境变量 VIDEOTOOLS_FFMPEG 指定。");
        }

        public static ToolException OutputExists(string path)
        {
            return new ToolException("OUTPUT_EXISTS", "输出文件已存在：" + path + "。如需覆盖请传入 overwrite=true。");
        }

        public static ToolException ProcessFailed(int exitCode, string logTail)
        {
            var message = "ffmpeg 执行失败（退出码 " + exitCode + "）。";
            if (!string.IsNullOrWhiteSpace(logTail))
            {
                message += Environment.NewLine + logTail.Trim();
            }
            return new ToolException("PROCESS_FAILED", message);
        }

        public int ExitCode
        {
            get
            {
                switch (Code)
                {
                    case "INVALID_ARGUMENT":
                    case "INVALID_JSON":
                        return 2;
                    case "CANCELLED":
                        return 130;
                    default:
                        return 3;
                }
            }
        }
    }

    /// <summary>
    /// 定位 ffmpeg 可执行文件：显式参数 → 环境变量 → 桌面端配置 → 应用目录 → PATH。
    /// </summary>
    public static class FfmpegLocator
    {
        public static string Resolve(IniConfig config, string explicitPath = null)
        {
            return Resolve(config == null ? null : config.FfmpegPath, explicitPath);
        }

        /// <summary>供仅持有配置字符串的调用方（如桌面端）使用。</summary>
        public static string Resolve(string configuredPath, string explicitPath = null)
        {
            var candidates = new List<string>();
            AddCandidate(candidates, explicitPath);
            AddCandidate(candidates, Environment.GetEnvironmentVariable("VIDEOTOOLS_FFMPEG"));
            AddCandidate(candidates, configuredPath);
            candidates.Add(Path.Combine(AppPaths.ExeDirectory, "ffmpeg.exe"));
            candidates.Add(Path.Combine(AppPaths.ExeDirectory, "bin", "ffmpeg.exe"));
            candidates.Add(Path.Combine(AppPaths.ExeDirectory, "automation", "ffmpeg.exe"));

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            return FindOnPath("ffmpeg.exe");
        }

        public static string ResolveOrThrow(IniConfig config, string explicitPath = null)
        {
            var path = Resolve(config, explicitPath);
            if (string.IsNullOrEmpty(path)) throw ToolException.FfmpegNotFound();
            return path;
        }

        /// <summary>相对路径按程序目录解析，方便便携包换电脑后仍然能找到 ffmpeg。</summary>
        private static void AddCandidate(List<string> candidates, string path)
        {
            var resolved = AppPaths.ResolvePath(path);
            if (!string.IsNullOrEmpty(resolved)) candidates.Add(resolved);
        }

        private static string FindOnPath(string fileName)
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathVariable.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH 中可能存在非法路径片段，忽略即可
                }
            }
            return null;
        }

        public static string Quote(string value)
        {
            if (value == null) return "\"\"";
            var builder = new StringBuilder("\"");
            foreach (var ch in value)
            {
                if (ch == '"') builder.Append("\\\"");
                else builder.Append(ch);
            }
            builder.Append("\"");
            return builder.ToString();
        }
    }
}
