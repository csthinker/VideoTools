using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoTools.Automation
{
    /// <summary>
    /// 自动化统一入口：CLI 与 MCP 都调用这里，保证行为一致。
    /// </summary>
    public class AutomationService
    {
        private readonly string _ffmpegOverride;
        private readonly string _configPath;

        public AutomationService(string ffmpegOverride = null, string configPath = null)
        {
            _ffmpegOverride = ffmpegOverride;
            _configPath = configPath;
        }

        public IniConfig Config
        {
            get { return new IniConfig(_configPath); }
        }

        public string ResolveFfmpeg()
        {
            return FfmpegLocator.ResolveOrThrow(Config, _ffmpegOverride);
        }

        public Dictionary<string, object> Execute(
            string operation,
            IDictionary<string, object> args,
            Action<double, string> onProgress,
            CancellationToken cancellationToken,
            Action<int> onFfmpegStarted = null)
        {
            args = args ?? new Dictionary<string, object>();
            switch (operation)
            {
                case "probe":
                    return Probe(args);
                case "batch":
                    return Batch(args, onProgress, cancellationToken, onFfmpegStarted);
                case "pipeline":
                    return Pipeline(args, onProgress, cancellationToken, onFfmpegStarted);
                case "config_get":
                    return ConfigGet();
                case "config_set":
                    return ConfigSet(args);
                case "encoders":
                    return Encoders(args);
                case "doctor":
                    return Doctor();
                case "capabilities":
                    return Capabilities();
                default:
                    return RunSingle(operation, args, onProgress, cancellationToken, onFfmpegStarted);
            }
        }

        private Dictionary<string, object> Probe(IDictionary<string, object> args)
        {
            var ffmpeg = ResolveFfmpeg();
            var inputPath = Json.Str(args, "filePath") ?? Json.Str(args, "input") ?? Json.Str(args, "file");
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                var files = Json.StrList(args, "files");
                if (files.Count > 0) inputPath = files[0];
            }
            if (string.IsNullOrWhiteSpace(inputPath)) throw ToolException.InvalidArgument("缺少输入文件参数 filePath。");

            var media = MediaProbe.Probe(ffmpeg, Path.GetFullPath(inputPath.Trim()));
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", "probe" },
                { "media", media.ToDictionary() }
            };
        }

        private Dictionary<string, object> RunSingle(
            string operation,
            IDictionary<string, object> args,
            Action<double, string> onProgress,
            CancellationToken cancellationToken,
            Action<int> onFfmpegStarted)
        {
            var ffmpeg = ResolveFfmpeg();
            var plan = CommandBuilder.Build(operation, args, Config, ffmpeg);
            var run = FfmpegRunner.Run(ffmpeg, plan.Arguments, plan.TotalSeconds, onProgress, cancellationToken, onFfmpegStarted);
            if (run.Cancelled) throw new ToolException("CANCELLED", "任务已取消。");
            if (run.ExitCode != 0) throw ToolException.ProcessFailed(run.ExitCode, run.LogTail);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", operation },
                { "inputPath", plan.InputPath },
                { "outputPath", plan.OutputPath },
                { "outputSizeBytes", SafeFileSize(plan.OutputPath) },
                { "elapsedSeconds", run.ElapsedSeconds },
                { "encoder", plan.CodecLabel },
                { "command", plan.DisplayCommand }
            };
        }

        private Dictionary<string, object> Batch(
            IDictionary<string, object> args,
            Action<double, string> onProgress,
            CancellationToken cancellationToken,
            Action<int> onFfmpegStarted)
        {
            var files = Json.StrList(args, "files");
            if (files.Count == 0) throw ToolException.InvalidArgument("批处理需要 files 数组。");

            var operation = Json.Str(args, "operation", "compress") ?? "compress";
            var parameters = Json.AsObject(args.ContainsKey("params") ? args["params"] : null) ?? new Dictionary<string, object>();
            var concurrency = Json.Int(args, "concurrency") ?? 2;
            if (concurrency < 1) concurrency = 1;
            if (concurrency > 8) concurrency = 8;
            var overwrite = Json.Bool(args, "overwrite") ?? false;

            var results = new object[files.Count];
            var succeeded = 0;
            var failed = 0;
            var completed = 0;
            var gate = new object();

            using (var semaphore = new SemaphoreSlim(concurrency))
            {
                var tasks = new List<Task>();
                for (var i = 0; i < files.Count; i++)
                {
                    var index = i;
                    var file = files[i];
                    tasks.Add(Task.Run(() =>
                    {
                        semaphore.Wait();
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var itemArgs = Json.Merge(parameters, new Dictionary<string, object>
                            {
                                { "filePath", file },
                                { "overwrite", overwrite }
                            });
                            var batchOutputDir = Json.Str(args, "outputDir");
                            if (!string.IsNullOrWhiteSpace(batchOutputDir)) itemArgs["outputDir"] = batchOutputDir;
                            var result = RunSingle(operation, itemArgs, null, cancellationToken, onFfmpegStarted);
                            lock (gate)
                            {
                                results[index] = result;
                                succeeded++;
                            }
                        }
                        catch (Exception ex)
                        {
                            var toolError = ex as ToolException;
                            lock (gate)
                            {
                                results[index] = new Dictionary<string, object>
                                {
                                    { "ok", false },
                                    { "filePath", file },
                                    { "errorCode", toolError != null ? toolError.Code : "UNEXPECTED_ERROR" },
                                    { "error", ex.Message }
                                };
                                failed++;
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                            int done;
                            lock (gate) done = ++completed;
                            if (onProgress != null) onProgress(done * 100.0 / files.Count, done + "/" + files.Count);
                        }
                    }));
                }
                Task.WaitAll(tasks.ToArray());
            }

            return new Dictionary<string, object>
            {
                { "ok", failed == 0 },
                { "operation", "batch" },
                { "childOperation", operation },
                { "total", files.Count },
                { "succeeded", succeeded },
                { "failed", failed },
                { "results", new List<object>(results) }
            };
        }

        private Dictionary<string, object> Pipeline(
            IDictionary<string, object> args,
            Action<double, string> onProgress,
            CancellationToken cancellationToken,
            Action<int> onFfmpegStarted)
        {
            var steps = Json.AsArray(args.ContainsKey("steps") ? args["steps"] : null);
            if (steps == null || steps.Count == 0) throw ToolException.InvalidArgument("流水线需要 steps 数组。");

            var inputPath = Json.Str(args, "filePath") ?? Json.Str(args, "input") ?? Json.Str(args, "file");
            if (string.IsNullOrWhiteSpace(inputPath)) throw ToolException.InvalidArgument("流水线缺少 filePath。");
            inputPath = Path.GetFullPath(inputPath.Trim());
            if (!File.Exists(inputPath)) throw ToolException.InvalidArgument("输入文件不存在：" + inputPath);

            var finalOutput = Json.Str(args, "outputPath") ?? Json.Str(args, "output");
            var overwrite = Json.Bool(args, "overwrite") ?? false;
            var tempDirectory = Path.Combine(Path.GetTempPath(), "videotools_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDirectory);

            var stepResults = new List<object>();
            var current = inputPath;
            try
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var step = Json.AsObject(steps[i]);
                    if (step == null) throw ToolException.InvalidArgument("第 " + (i + 1) + " 步格式不正确。");
                    var operation = Json.Str(step, "operation") ?? Json.Str(step, "op");
                    if (!CommandBuilder.IsOperation(operation))
                    {
                        throw ToolException.InvalidArgument("第 " + (i + 1) + " 步操作不受支持：" + operation);
                    }

                    var parameters = Json.AsObject(step.ContainsKey("params") ? step["params"] : null) ?? new Dictionary<string, object>();
                    var stepArgs = Json.Merge(parameters, new Dictionary<string, object> { { "filePath", current } });
                    if (i == steps.Count - 1)
                    {
                        if (!string.IsNullOrWhiteSpace(finalOutput)) stepArgs["outputPath"] = finalOutput;
                        stepArgs["overwrite"] = overwrite;
                    }
                    else
                    {
                        stepArgs["outputDir"] = tempDirectory;
                        stepArgs["overwrite"] = true;
                    }

                    var stepIndex = i;
                    var stepCount = steps.Count;
                    Action<double, string> stepProgress = null;
                    if (onProgress != null)
                    {
                        stepProgress = (percent, speed) =>
                            onProgress((stepIndex + percent / 100.0) / stepCount * 100.0, "step " + (stepIndex + 1) + "/" + stepCount + (speed != null ? " " + speed : ""));
                    }

                    var result = RunSingle(operation, stepArgs, stepProgress, cancellationToken, onFfmpegStarted);
                    stepResults.Add(result);
                    current = Convert.ToString(result["outputPath"], CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
                }
                catch (Exception)
                {
                }
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", "pipeline" },
                { "outputPath", current },
                { "outputSizeBytes", SafeFileSize(current) },
                { "steps", stepResults }
            };
        }

        private Dictionary<string, object> ConfigGet()
        {
            var config = Config;
            var section = config.ReadSection("Program");
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in section) result[pair.Key] = pair.Value;
            result["resolvedFfmpegPath"] = FfmpegLocator.Resolve(config, _ffmpegOverride);
            result["configFile"] = config.FilePath;
            result["outputDirectory"] = config.OutputDirectory;
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", "config_get" },
                { "config", result }
            };
        }

        private Dictionary<string, object> ConfigSet(IDictionary<string, object> args)
        {
            var config = Config;
            var changed = new List<string>();

            var ffmpegPath = Json.Str(args, "ffmpegPath");
            if (!string.IsNullOrWhiteSpace(ffmpegPath))
            {
                if (!File.Exists(ffmpegPath)) throw ToolException.InvalidArgument("ffmpeg 路径不存在：" + ffmpegPath);
                config.FfmpegPath = ffmpegPath;
                changed.Add("FFmpegPath");
            }

            var gpuUse = Json.Bool(args, "gpuUse");
            if (gpuUse.HasValue)
            {
                config.GpuEnabled = gpuUse.Value;
                changed.Add("GpuUse");
            }

            var gpuSelect = Json.Str(args, "gpuSelect");
            var gpuIndex = Json.Int(args, "gpuSelect");
            if (!string.IsNullOrWhiteSpace(gpuSelect) && gpuIndex == null)
            {
                var text = gpuSelect.Trim().ToLowerInvariant();
                if (text == "intel" || text == "qsv") gpuIndex = 0;
                else if (text == "nvidia" || text == "nvenc") gpuIndex = 1;
                else if (text == "amd" || text == "amf") gpuIndex = 2;
            }
            if (gpuIndex.HasValue)
            {
                config.GpuVendorIndex = gpuIndex.Value;
                changed.Add("GpuSelect");
            }

            var cpuMax = Json.Bool(args, "cpuMax");
            if (cpuMax.HasValue)
            {
                config.CpuMax = cpuMax.Value;
                changed.Add("CpuMax");
            }

            var outputDir = Json.Str(args, "outputDir") ?? Json.Str(args, "videoSaveFolder");
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                AppPaths.EnsureDirectory(outputDir);
                config.OutputDirectory = outputDir;
                changed.Add("VideoSaveFloder");
            }

            if (changed.Count == 0) throw ToolException.InvalidArgument("没有需要写入的配置项。");

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", "config_set" },
                { "changed", changed }
            };
        }

        private Dictionary<string, object> Encoders(IDictionary<string, object> args)
        {
            var ffmpeg = ResolveFfmpeg();
            var all = FfmpegRunner.ListEncoders(ffmpeg);
            var filter = Json.Str(args, "filter");
            var selected = new List<object>();
            foreach (var encoder in all)
            {
                if (!string.IsNullOrWhiteSpace(filter) && encoder.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                selected.Add(encoder);
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", "encoders" },
                { "count", selected.Count },
                { "encoders", selected },
                { "hardware", new Dictionary<string, object>
                    {
                        { "h264_nvenc", all.Exists(e => string.Equals(e, "h264_nvenc", StringComparison.OrdinalIgnoreCase)) },
                        { "hevc_nvenc", all.Exists(e => string.Equals(e, "hevc_nvenc", StringComparison.OrdinalIgnoreCase)) },
                        { "av1_nvenc", all.Exists(e => string.Equals(e, "av1_nvenc", StringComparison.OrdinalIgnoreCase)) },
                        { "h264_qsv", all.Exists(e => string.Equals(e, "h264_qsv", StringComparison.OrdinalIgnoreCase)) },
                        { "hevc_qsv", all.Exists(e => string.Equals(e, "hevc_qsv", StringComparison.OrdinalIgnoreCase)) },
                        { "h264_amf", all.Exists(e => string.Equals(e, "h264_amf", StringComparison.OrdinalIgnoreCase)) },
                        { "hevc_amf", all.Exists(e => string.Equals(e, "hevc_amf", StringComparison.OrdinalIgnoreCase)) }
                    }
                }
            };
        }

        private Dictionary<string, object> Doctor()
        {
            var config = Config;
            var ffmpeg = FfmpegLocator.Resolve(config, _ffmpegOverride);
            var mcpExe = AppPaths.FindMcpExecutable();
            var cliExe = AppPaths.FindCliExecutable();
            var result = new Dictionary<string, object>
            {
                { "ok", !string.IsNullOrEmpty(ffmpeg) },
                { "operation", "doctor" },
                { "ffmpegPath", ffmpeg },
                { "ffmpegVersion", string.IsNullOrEmpty(ffmpeg) ? null : FfmpegRunner.GetVersion(ffmpeg) },
                { "configFile", config.FilePath },
                { "dataDirectory", AppPaths.DataDirectory },
                { "jobsDirectory", AppPaths.JobsDirectory },
                { "outputDirectory", config.OutputDirectory },
                { "gpuEnabled", config.GpuEnabled },
                { "gpuSelect", config.GpuVendorIndex },
                { "cpuMax", config.CpuMax },
                { "cliPath", cliExe },
                { "cliExists", File.Exists(cliExe) },
                { "mcpPath", mcpExe },
                { "mcpExists", File.Exists(mcpExe) }
            };
            if (!string.IsNullOrEmpty(ffmpeg))
            {
                var encoders = FfmpegRunner.ListEncoders(ffmpeg);
                result["hardwareEncoders"] = new Dictionary<string, object>
                {
                    { "h264_nvenc", encoders.Exists(e => string.Equals(e, "h264_nvenc", StringComparison.OrdinalIgnoreCase)) },
                    { "h264_qsv", encoders.Exists(e => string.Equals(e, "h264_qsv", StringComparison.OrdinalIgnoreCase)) },
                    { "h264_amf", encoders.Exists(e => string.Equals(e, "h264_amf", StringComparison.OrdinalIgnoreCase)) }
                };
            }
            return result;
        }

        private Dictionary<string, object> Capabilities()
        {
            var operations = new List<object>();
            foreach (var operation in CommandBuilder.Operations)
            {
                operations.Add(new Dictionary<string, object>
                {
                    { "name", operation },
                    { "cli", "videotools " + CliNameFor(operation) },
                    { "mcpTool", "videotools_" + operation }
                });
            }
            operations.Add(new Dictionary<string, object> { { "name", "probe" }, { "cli", "videotools probe" }, { "mcpTool", "videotools_probe" } });
            operations.Add(new Dictionary<string, object> { { "name", "batch" }, { "cli", "videotools batch" }, { "mcpTool", "videotools_batch" } });
            operations.Add(new Dictionary<string, object> { { "name", "pipeline" }, { "cli", "videotools pipeline" }, { "mcpTool", "videotools_pipeline" } });

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", "capabilities" },
                { "operations", operations }
            };
        }

        private static string CliNameFor(string operation)
        {
            if (operation == "audio_extract") return "audio --action extract";
            if (operation == "audio_remove") return "audio --action remove";
            if (operation == "audio_replace") return "audio --action replace";
            return operation;
        }

        private static long SafeFileSize(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
