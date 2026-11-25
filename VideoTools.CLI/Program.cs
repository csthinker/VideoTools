using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VideoTools.Automation;
using VideoTools.Automation.Mcp;

namespace VideoTools.CLI
{
    /// <summary>
    /// videotools 命令行入口：与 MCP 服务共用同一套 AutomationService。
    /// </summary>
    public static class Program
    {
        private static readonly HashSet<string> BooleanOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "overwrite", "mute", "reencode", "palette", "loop", "json", "text", "async", "wait",
            "install", "help", "quiet", "no-palette", "no-loop"
        };

        private static readonly Dictionary<string, string> OptionAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "output", "outputPath" },
            { "output-path", "outputPath" },
            { "output-dir", "outputDir" },
            { "speed", "rate" },
            { "audio", "audioPath" },
            { "audio-file", "audioPath" },
            { "file", "filePath" },
            { "input", "filePath" },
            { "id", "id" },
            { "job", "id" }
        };

        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (ToolException ex)
            {
                WriteError(ex.Code, ex.Message);
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                WriteError("UNEXPECTED_ERROR", ex.Message);
                return 1;
            }
        }

        private static int Run(string[] rawArgs)
        {
            if (rawArgs.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            var args = new List<string>(rawArgs);
            if (args[0] == "--version" || args[0] == "-v")
            {
                Console.WriteLine("videotools 1.0.0");
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            args.RemoveAt(0);

            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var positionals = new List<string>();
            ParseOptions(args, options, positionals);

            if (options.ContainsKey("help") || command == "help")
            {
                PrintHelp();
                return 0;
            }

            var ffmpegOverride = Get(options, "ffmpeg");
            var service = new AutomationService(ffmpegOverride);
            var jsonOutput = !options.ContainsKey("text");

            switch (command)
            {
                case "probe":
                    return ExecuteDirect(service, "probe", BuildRequest(options, positionals), jsonOutput);
                case "compress":
                case "convert":
                case "trim":
                case "gif":
                case "speed":
                case "resize":
                    return ExecuteJob(service, command, BuildRequest(options, positionals), options, jsonOutput);
                case "audio":
                    return RunAudio(service, options, positionals, jsonOutput);
                case "batch":
                    return ExecuteJob(service, "batch", BuildBatchRequest(options), options, jsonOutput);
                case "pipeline":
                    return ExecuteJob(service, "pipeline", BuildPipelineRequest(options), options, jsonOutput);
                case "jobs":
                    return RunJobs(positionals, options, jsonOutput);
                case "config":
                    return RunConfig(service, positionals, options, jsonOutput);
                case "encoders":
                    return ExecuteDirect(service, "encoders", BuildRequest(options, positionals), jsonOutput);
                case "doctor":
                    return ExecuteDirect(service, "doctor", new Dictionary<string, object>(), jsonOutput);
                case "capabilities":
                    return ExecuteDirect(service, "capabilities", new Dictionary<string, object>(), jsonOutput);
                case "setup":
                    return RunSetup(positionals, options, jsonOutput);
                case "run-job":
                    return RunJobInBackgroundWorker(service, options);
                default:
                    WriteError("INVALID_ARGUMENT", "未知命令：" + command + "。运行 videotools help 查看用法。");
                    return 2;
            }
        }

        private static void ParseOptions(List<string> args, Dictionary<string, string> options, List<string> positionals)
        {
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--"))
                {
                    positionals.Add(arg);
                    continue;
                }

                var key = arg.Substring(2);
                string value = null;
                var equalsIndex = key.IndexOf('=');
                if (equalsIndex > 0)
                {
                    value = key.Substring(equalsIndex + 1);
                    key = key.Substring(0, equalsIndex);
                }

                var canonicalKey = OptionAliases.ContainsKey(key) ? OptionAliases[key] : key;

                if (value == null && BooleanOptions.Contains(key))
                {
                    options[canonicalKey] = key.StartsWith("no-", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
                    continue;
                }

                if (value == null)
                {
                    if (i + 1 >= args.Count)
                    {
                        throw ToolException.InvalidArgument("参数 --" + key + " 缺少值。");
                    }
                    value = args[++i];
                }

                options[canonicalKey] = value;
            }
        }

        private static string Get(Dictionary<string, string> options, string key)
        {
            string value;
            return options.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> BuildRequest(Dictionary<string, string> options, List<string> positionals)
        {
            var request = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var inputJson = Get(options, "input-json");
            if (!string.IsNullOrWhiteSpace(inputJson))
            {
                var text = inputJson.Trim() == "-" ? Console.In.ReadToEnd() : File.ReadAllText(inputJson);
                request = Json.ParseObject(text);
            }

            if (positionals.Count > 0 && !request.ContainsKey("filePath")) request["filePath"] = positionals[0];

            foreach (var pair in options)
            {
                switch (pair.Key.ToLowerInvariant())
                {
                    case "ffmpeg":
                    case "input-json":
                    case "async":
                    case "wait":
                    case "timeout":
                    case "json":
                    case "text":
                    case "quiet":
                    case "help":
                    case "client":
                    case "install":
                        continue;
                    case "palette":
                    case "loop":
                    case "overwrite":
                    case "mute":
                    case "reencode":
                        request[pair.Key] = pair.Value == "true";
                        break;
                    default:
                        request[pair.Key] = pair.Value;
                        break;
                }
            }
            return request;
        }

        private static Dictionary<string, object> BuildBatchRequest(Dictionary<string, string> options)
        {
            var request = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var inputJson = Get(options, "input-json");
            if (!string.IsNullOrWhiteSpace(inputJson))
            {
                var text = inputJson.Trim() == "-" ? Console.In.ReadToEnd() : File.ReadAllText(inputJson);
                request = Json.ParseObject(text);
            }

            var files = Get(options, "files");
            if (!string.IsNullOrWhiteSpace(files)) request["files"] = files;
            var operation = Get(options, "operation");
            if (!string.IsNullOrWhiteSpace(operation)) request["operation"] = operation;
            var parameters = Get(options, "params");
            if (!string.IsNullOrWhiteSpace(parameters)) request["params"] = Json.ParseObject(parameters);
            var concurrency = Get(options, "concurrency");
            if (!string.IsNullOrWhiteSpace(concurrency)) request["concurrency"] = concurrency;
            var outputDir = Get(options, "outputDir");
            if (!string.IsNullOrWhiteSpace(outputDir)) request["outputDir"] = outputDir;
            var overwrite = Get(options, "overwrite");
            if (!string.IsNullOrWhiteSpace(overwrite)) request["overwrite"] = overwrite == "true";
            return request;
        }

        private static Dictionary<string, object> BuildPipelineRequest(Dictionary<string, string> options)
        {
            var request = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var inputJson = Get(options, "input-json");
            if (!string.IsNullOrWhiteSpace(inputJson))
            {
                var text = inputJson.Trim() == "-" ? Console.In.ReadToEnd() : File.ReadAllText(inputJson);
                request = Json.ParseObject(text);
            }

            var filePath = Get(options, "filePath");
            if (!string.IsNullOrWhiteSpace(filePath)) request["filePath"] = filePath;
            var steps = Get(options, "steps");
            if (!string.IsNullOrWhiteSpace(steps)) request["steps"] = Json.Parse(steps);
            var outputPath = Get(options, "outputPath");
            if (!string.IsNullOrWhiteSpace(outputPath)) request["outputPath"] = outputPath;
            if (options.ContainsKey("overwrite")) request["overwrite"] = true;
            return request;
        }

        private static int ExecuteDirect(AutomationService service, string operation, Dictionary<string, object> request, bool jsonOutput)
        {
            var result = service.Execute(operation, request, null, CancellationToken.None);
            WriteResult(result, jsonOutput);
            return 0;
        }

        private static int ExecuteJob(AutomationService service, string operation, Dictionary<string, object> request, Dictionary<string, string> options, bool jsonOutput)
        {
            var runAsync = options.ContainsKey("async");
            var wait = options.ContainsKey("wait");
            var timeoutMs = ParseTimeout(options);

            if (runAsync)
            {
                var job = JobManager.Create(operation, request);
                JobManager.StartBackground(job, Get(options, "ffmpeg"));
                if (wait)
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (!job.IsFinished && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(500);
                        job = JobManager.Load(job.Id);
                    }
                }
                WriteResult(new Dictionary<string, object>
                {
                    { "ok", job.Status != "failed" },
                    { "jobId", job.Id },
                    { "status", job.Status },
                    { "outputPath", job.OutputPath },
                    { "error", job.Error }
                }, jsonOutput);
                return job.Status == "failed" ? 3 : 0;
            }

            var record = JobManager.Create(operation, request);
            var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                JobManager.Run(record, service, cts.Token);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }

            if (record.Status == "completed")
            {
                WriteResult(record.Result ?? new Dictionary<string, object> { { "ok", true }, { "jobId", record.Id } }, jsonOutput);
                return 0;
            }

            WriteError(record.ErrorCode ?? "JOB_FAILED", record.Error ?? "任务失败。");
            return record.Status == "cancelled" ? 130 : 3;
        }

        private static int RunAudio(AutomationService service, Dictionary<string, string> options, List<string> positionals, bool jsonOutput)
        {
            var action = Get(options, "action");
            if (string.IsNullOrWhiteSpace(action))
            {
                WriteError("INVALID_ARGUMENT", "audio 命令需要 --action extract|remove|replace。");
                return 2;
            }

            string operation;
            switch (action.Trim().ToLowerInvariant())
            {
                case "extract": operation = "audio_extract"; break;
                case "remove": operation = "audio_remove"; break;
                case "replace": operation = "audio_replace"; break;
                default:
                    WriteError("INVALID_ARGUMENT", "action 仅支持 extract / remove / replace。");
                    return 2;
            }

            var request = BuildRequest(options, positionals);
            request.Remove("action");
            return ExecuteJob(service, operation, request, options, jsonOutput);
        }

        private static int RunJobs(List<string> positionals, Dictionary<string, string> options, bool jsonOutput)
        {
            var action = positionals.Count > 0 ? positionals[0].ToLowerInvariant() : "list";
            var id = Get(options, "id");

            if (action == "list")
            {
                var limit = 20;
                var limitText = Get(options, "limit");
                if (!string.IsNullOrWhiteSpace(limitText)) int.TryParse(limitText, out limit);
                var jobs = JobManager.List(limit);
                var items = new List<object>();
                foreach (var job in jobs) items.Add(job.ToDictionary());
                WriteResult(new Dictionary<string, object> { { "ok", true }, { "operation", "jobs_list" }, { "jobs", items } }, jsonOutput);
                return 0;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                WriteError("INVALID_ARGUMENT", "jobs " + action + " 需要 --id。");
                return 2;
            }

            if (action == "get")
            {
                var job = JobManager.Load(id);
                if (options.ContainsKey("wait"))
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(ParseTimeout(options));
                    while (!job.IsFinished && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(400);
                        job = JobManager.Load(id);
                    }
                }
                WriteResult(new Dictionary<string, object> { { "ok", job.Status == "completed" }, { "operation", "jobs_get" }, { "job", job.ToDictionary() } }, jsonOutput);
                return job.Status == "failed" ? 3 : 0;
            }

            if (action == "cancel")
            {
                var job = JobManager.Cancel(id);
                WriteResult(new Dictionary<string, object> { { "ok", true }, { "operation", "jobs_cancel" }, { "job", job.ToDictionary() } }, jsonOutput);
                return 0;
            }

            WriteError("INVALID_ARGUMENT", "jobs 仅支持 list / get / cancel。");
            return 2;
        }

        private static int RunConfig(AutomationService service, List<string> positionals, Dictionary<string, string> options, bool jsonOutput)
        {
            var action = positionals.Count > 0 ? positionals[0].ToLowerInvariant() : "get";
            if (action == "get")
            {
                return ExecuteDirect(service, "config_get", new Dictionary<string, object>(), jsonOutput);
            }
            if (action == "set")
            {
                var request = new Dictionary<string, object>();
                Copy(options, request, "ffmpeg-path", "ffmpegPath");
                Copy(options, request, "ffmpegPath", "ffmpegPath");
                Copy(options, request, "gpu-use", "gpuUse");
                Copy(options, request, "gpuUse", "gpuUse");
                Copy(options, request, "gpu-select", "gpuSelect");
                Copy(options, request, "gpuSelect", "gpuSelect");
                Copy(options, request, "cpu-max", "cpuMax");
                Copy(options, request, "cpuMax", "cpuMax");
                Copy(options, request, "outputDir", "outputDir");
                Copy(options, request, "output-dir", "outputDir");
                return ExecuteDirect(service, "config_set", request, jsonOutput);
            }
            WriteError("INVALID_ARGUMENT", "config 仅支持 get / set。");
            return 2;
        }

        private static void Copy(Dictionary<string, string> options, Dictionary<string, object> target, string key, string targetKey)
        {
            string value;
            if (options.TryGetValue(key, out value) && value != null) target[targetKey] = value;
        }

        private static int RunSetup(List<string> positionals, Dictionary<string, string> options, bool jsonOutput)
        {
            var target = positionals.Count > 0 ? positionals[0].ToLowerInvariant() : "mcp";
            if (target != "mcp")
            {
                WriteError("INVALID_ARGUMENT", "setup 目前仅支持 mcp。");
                return 2;
            }

            var client = (Get(options, "client") ?? "generic").Trim().ToLowerInvariant();
            var ffmpeg = FfmpegLocator.Resolve(new IniConfig(), Get(options, "ffmpeg"));
            var install = options.ContainsKey("install");

            if (client == "claude" && install)
            {
                var command = McpConfig.BuildClaudeAddCommand(ffmpeg);
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + command,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using (var process = Process.Start(startInfo))
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        var error = process.StandardError.ReadToEnd();
                        process.WaitForExit(60000);
                        if (process.ExitCode == 0)
                        {
                            WriteResult(new Dictionary<string, object>
                            {
                                { "ok", true },
                                { "operation", "setup_mcp" },
                                { "client", "claude" },
                                { "command", command },
                                { "output", output.Trim() }
                            }, jsonOutput);
                            return 0;
                        }
                        WriteError("INSTALL_FAILED", "claude mcp add 执行失败：" + (error + output).Trim());
                        return 3;
                    }
                }
                catch (Exception ex)
                {
                    WriteError("INSTALL_FAILED", "未找到 claude 命令，请手动执行：" + command + Environment.NewLine + ex.Message);
                    return 3;
                }
            }

            string snippet;
            if (client == "codex") snippet = McpConfig.BuildCodexToml(ffmpeg);
            else if (client == "claude") snippet = McpConfig.BuildClientJson(ffmpeg) + Environment.NewLine + "# 或执行：" + McpConfig.BuildClaudeAddCommand(ffmpeg);
            else snippet = McpConfig.BuildClientJson(ffmpeg);

            if (jsonOutput)
            {
                WriteResult(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "operation", "setup_mcp" },
                    { "client", client },
                    { "mcpExecutable", AppPaths.FindMcpExecutable() },
                    { "snippet", snippet }
                }, true);
            }
            else
            {
                Console.WriteLine(snippet);
            }
            return 0;
        }

        private static int RunJobInBackgroundWorker(AutomationService service, Dictionary<string, string> options)
        {
            var id = Get(options, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                WriteError("INVALID_ARGUMENT", "run-job 需要 --id。");
                return 2;
            }
            var job = JobManager.Load(id);
            JobManager.Run(job, service);
            return job.Status == "completed" ? 0 : 3;
        }

        private static int ParseTimeout(Dictionary<string, string> options)
        {
            var text = Get(options, "timeout");
            int timeout;
            if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout)) return Math.Max(1000, timeout);
            return 300000;
        }

        private static void WriteResult(Dictionary<string, object> result, bool jsonOutput)
        {
            if (jsonOutput)
            {
                Console.WriteLine(Json.Serialize(result));
                return;
            }

            var outputPath = Json.Str(result, "outputPath");
            if (!string.IsNullOrEmpty(outputPath)) Console.WriteLine("完成：" + outputPath);
            else if (result.ContainsKey("media"))
            {
                var media = Json.AsObject(result["media"]);
                Console.WriteLine("文件：" + Json.Str(media, "path"));
                Console.WriteLine("时长：" + Json.Str(media, "duration") + "　分辨率：" + Json.Str(media, "width") + "x" + Json.Str(media, "height"));
                Console.WriteLine("视频编码：" + Json.Str(media, "videoCodec") + "　音频编码：" + Json.Str(media, "audioCodec"));
            }
            else
            {
                Console.WriteLine(Json.Serialize(result));
            }
        }

        private static void WriteError(string code, string message)
        {
            var payload = new Dictionary<string, object>
            {
                { "ok", false },
                { "errorCode", code },
                { "error", message }
            };
            Console.Error.WriteLine(Json.Serialize(payload));
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"VideoTools 自动化 CLI 1.0.0
用法：videotools <命令> [文件] [选项]

核心命令
  probe <文件>                        读取媒体信息
  compress <文件>                     压缩视频    --codec h264|h265|av1|mpeg4  --crf 25  --gpu none|qsv|nvenc|amf
  convert <文件> --format mp4         格式转换    --format mp4|avi|mkv|mov|webm
  trim <文件> --start 00:00:05        裁剪        --duration 10 / --end 00:00:15  --reencode
  gif <文件>                          转 GIF      --fps 10 --width 480 --format gif|webp --no-palette --no-loop
  speed <文件> --rate 2               倍速        --mute
  resize <文件> --width 1280          改尺寸      --height -1（-1 表示按比例）
  audio <文件> --action extract       音频        extract --format mp3 / remove / replace --audio-path b.mp3
  batch --files a.mp4,b.mp4 --operation compress --params {""crf"":30}
  pipeline --input-json steps.json    多步骤流水线

任务与配置
  jobs list|get|cancel [--id X] [--wait]
  config get
  config set --ffmpeg-path D:\ffmpeg.exe --gpu-use enable --gpu-select nvidia --output-dir D:\out
  encoders [--filter h264]
  doctor
  capabilities

AI 工具接入
  setup mcp --client cursor            输出 Cursor / 通用 MCP JSON
  setup mcp --client claude --install  直接注册到 Claude Code
  setup mcp --client codex             输出 Codex TOML 片段

通用选项
  --output <文件或目录>   指定输出；--output-dir <目录> 指定输出目录
  --overwrite             允许覆盖同名文件
  --json                  输出 JSON（默认）；--text 输出人类可读文本
  --async                 后台执行，立即返回任务 ID；--wait 等待完成；--timeout 毫秒
  --ffmpeg <路径>         临时指定 ffmpeg
  --input-json <文件|->   从 JSON 文件或标准输入读取完整参数

退出码：0 成功，2 参数错误，3 处理失败，130 已取消。");
        }
    }
}
