using System;
using System.Collections.Generic;
using System.Threading;

namespace VideoTools.Automation.Mcp
{
    public class McpToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> InputSchema { get; set; }
        /// <summary>非空表示这是执行类工具，会创建可查询的后台任务。</summary>
        public string Operation { get; set; }
        public Func<Dictionary<string, object>, Dictionary<string, object>> Invoke { get; set; }
    }

    public static class ToolCatalog
    {
        public static List<McpToolDefinition> Build(AutomationService service)
        {
            var tools = new List<McpToolDefinition>
            {
                new McpToolDefinition
                {
                    Name = "videotools_probe",
                    Description = "读取媒体文件信息（时长、分辨率、帧率、编码、码率、音轨等）。只读操作，不修改文件。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入文件的绝对路径""}},""required"":[""filePath""]}"),
                    Invoke = args => Execute(service, "probe", args)
                },
                new McpToolDefinition
                {
                    Name = "videotools_compress",
                    Description = "压缩视频。支持 H.264/H.265/AV1 与 Intel/AMD/NVIDIA 硬件编码，质量参数 crf 越大体积越小。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""codec"":{""type"":""string"",""enum"":[""h264"",""h265"",""av1"",""mpeg4""],""description"":""视频编码，默认 h264""},""crf"":{""type"":""integer"",""minimum"":0,""maximum"":51,""description"":""质量参数，默认 25""},""preset"":{""type"":""string"",""description"":""编码预设，默认 medium""},""gpu"":{""type"":""string"",""enum"":[""none"",""qsv"",""nvenc"",""amf"",""intel"",""nvidia"",""amd""],""description"":""硬件编码器，默认跟随软件设置""},""outputPath"":{""type"":""string"",""description"":""输出文件或目录，省略则写入设置中的输出目录""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath""]}"),
                    Operation = "compress"
                },
                new McpToolDefinition
                {
                    Name = "videotools_convert",
                    Description = "格式转换，目标容器支持 mp4 / avi / mkv / mov / webm，自动匹配编码。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""format"":{""type"":""string"",""enum"":[""mp4"",""avi"",""mkv"",""mov"",""webm""],""description"":""目标格式""},""gpu"":{""type"":""string"",""enum"":[""none"",""qsv"",""nvenc"",""amf""]},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath"",""format""]}"),
                    Operation = "convert"
                },
                new McpToolDefinition
                {
                    Name = "videotools_trim",
                    Description = "裁剪视频片段。默认无损流复制（关键帧对齐），reencode=true 可帧级精确。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""start"":{""type"":""string"",""description"":""开始时间，秒数或 hh:mm:ss，默认 0""},""duration"":{""type"":""string"",""description"":""时长，秒数或 hh:mm:ss，与 end 二选一""},""end"":{""type"":""string"",""description"":""结束时间，秒数或 hh:mm:ss""},""reencode"":{""type"":""boolean"",""description"":""是否重新编码，默认 false""},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath""]}"),
                    Operation = "trim"
                },
                new McpToolDefinition
                {
                    Name = "videotools_gif",
                    Description = "把视频转成 GIF 或 WebP 动图，可设置帧率、宽度、调色板与循环。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""format"":{""type"":""string"",""enum"":[""gif"",""webp""],""description"":""动图格式，默认 gif""},""fps"":{""type"":""number"",""description"":""帧率，默认 10""},""width"":{""type"":""integer"",""description"":""输出宽度，高度按比例，默认原视频宽度""},""palette"":{""type"":""boolean"",""description"":""GIF 是否使用调色板提升画质，默认 true""},""loop"":{""type"":""boolean"",""description"":""是否无限循环，默认 true""},""start"":{""type"":""string""},""duration"":{""type"":""string""},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath""]}"),
                    Operation = "gif"
                },
                new McpToolDefinition
                {
                    Name = "videotools_speed",
                    Description = "调整视频倍速（加速或减速），可同步处理音轨。rate=2 表示两倍速。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""rate"":{""type"":""number"",""description"":""倍速，0.1-10，例如 2 或 0.5""},""mute"":{""type"":""boolean"",""description"":""是否移除音轨，默认 false""},""codec"":{""type"":""string"",""enum"":[""h264"",""h265"",""av1"",""mpeg4""]},""crf"":{""type"":""integer"",""minimum"":0,""maximum"":51},""gpu"":{""type"":""string"",""enum"":[""none"",""qsv"",""nvenc"",""amf""]},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath"",""rate""]}"),
                    Operation = "speed"
                },
                new McpToolDefinition
                {
                    Name = "videotools_resize",
                    Description = "修改分辨率，可只给宽度或高度，另一个传 -1 自动按比例计算。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""width"":{""type"":""integer"",""description"":""目标宽度，-1 表示自动""},""height"":{""type"":""integer"",""description"":""目标高度，-1 表示自动""},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath""]}"),
                    Operation = "resize"
                },
                AudioTool("videotools_audio_extract", "audio_extract", "提取音频为 aac（流复制）/mp3/wav/m4a 文件。"),
                AudioRemoveTool(),
                new McpToolDefinition
                {
                    Name = "videotools_audio_replace",
                    Description = "用外部音频替换视频音轨，视频流保持原样复制。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""audioPath"":{""type"":""string"",""description"":""用于替换的音频文件绝对路径""},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath"",""audioPath""]}"),
                    Operation = "audio_replace"
                },
                new McpToolDefinition
                {
                    Name = "videotools_batch",
                    Description = "批量对多个文件执行同一操作，支持并发数控制，返回每个文件的结果。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""files"":{""type"":""array"",""items"":{""type"":""string""},""description"":""输入文件绝对路径数组""},""operation"":{""type"":""string"",""enum"":[""compress"",""convert"",""trim"",""gif"",""speed"",""resize"",""audio_extract"",""audio_remove"",""audio_replace""],""description"":""子操作，默认 compress""},""params"":{""type"":""object"",""description"":""子操作的参数对象，例如把 crf 设为 30""},""concurrency"":{""type"":""integer"",""description"":""并发数，1-8，默认 2""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""files"",""operation""]}"),
                    Operation = "batch"
                },
                new McpToolDefinition
                {
                    Name = "videotools_pipeline",
                    Description = "对同一个文件串联多个处理步骤，例如先裁剪再压缩再转格式，最后输出一个成品。",
                    InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入文件绝对路径""},""steps"":{""type"":""array"",""description"":""步骤数组，每项包含 operation 与 params，例如先 trim 再 compress 再 resize""},""outputPath"":{""type"":""string"",""description"":""最终输出文件路径""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath"",""steps""]}"),
                    Operation = "pipeline"
                },
                new McpToolDefinition
                {
                    Name = "videotools_jobs_list",
                    Description = "列出最近的自动化任务及状态。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{""limit"":{""type"":""integer"",""description"":""返回数量，默认 20""}}}"),
                    Invoke = args =>
                    {
                        var jobs = JobManager.List(Json.Int(args, "limit") ?? 20);
                        var items = new List<object>();
                        foreach (var job in jobs) items.Add(job.ToDictionary());
                        return Result("jobs_list", new Dictionary<string, object> { { "jobs", items } });
                    }
                },
                new McpToolDefinition
                {
                    Name = "videotools_jobs_get",
                    Description = "查询单个任务状态与结果，可选等待任务完成（wait=true）。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{""id"":{""type"":""string"",""description"":""任务 ID""},""wait"":{""type"":""boolean"",""description"":""是否等待任务完成，默认 false""},""timeoutMs"":{""type"":""integer"",""description"":""最长等待毫秒数，默认 25000""}},""required"":[""id""]}"),
                    Invoke = args =>
                    {
                        var id = Json.Str(args, "id");
                        var job = JobManager.Load(id);
                        if (Json.Bool(args, "wait") ?? false)
                        {
                            var timeout = Json.Int(args, "timeoutMs") ?? 25000;
                            var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                            while (!job.IsFinished && DateTime.UtcNow < deadline)
                            {
                                Thread.Sleep(400);
                                job = JobManager.Load(id);
                            }
                        }
                        return new Dictionary<string, object>
                        {
                            { "ok", job.Status == "completed" },
                            { "operation", "jobs_get" },
                            { "job", job.ToDictionary() }
                        };
                    }
                },
                new McpToolDefinition
                {
                    Name = "videotools_jobs_cancel",
                    Description = "取消正在运行的任务。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{""id"":{""type"":""string"",""description"":""任务 ID""}},""required"":[""id""]}"),
                    Invoke = args =>
                    {
                        var job = JobManager.Cancel(Json.Str(args, "id"));
                        return new Dictionary<string, object> { { "ok", true }, { "operation", "jobs_cancel" }, { "job", job.ToDictionary() } };
                    }
                },
                new McpToolDefinition
                {
                    Name = "videotools_config_get",
                    Description = "读取软件配置（ffmpeg 路径、GPU、输出目录等）。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{}}"),
                    Invoke = args => Execute(service, "config_get", args)
                },
                new McpToolDefinition
                {
                    Name = "videotools_config_set",
                    Description = "写入软件配置。支持 ffmpegPath、gpuUse、gpuSelect、cpuMax、outputDir。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{""ffmpegPath"":{""type"":""string""},""gpuUse"":{""type"":""boolean""},""gpuSelect"":{""type"":""string"",""enum"":[""intel"",""nvidia"",""amd"",""qsv"",""nvenc"",""amf"",""0"",""1"",""2"",""-1""]},""cpuMax"":{""type"":""boolean""},""outputDir"":{""type"":""string""}}}"),
                    Invoke = args => Execute(service, "config_set", args)
                },
                new McpToolDefinition
                {
                    Name = "videotools_encoders",
                    Description = "检测 ffmpeg 支持的编码器与硬件加速能力。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{""filter"":{""type"":""string"",""description"":""按名称过滤，例如 h264""}}}"),
                    Invoke = args => Execute(service, "encoders", args)
                },
                new McpToolDefinition
                {
                    Name = "videotools_doctor",
                    Description = "环境自检：ffmpeg 路径与版本、配置文件、任务目录、MCP/CLI 可执行文件、硬件编码器。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{}}"),
                    Invoke = args => Execute(service, "doctor", args)
                },
                new McpToolDefinition
                {
                    Name = "videotools_capabilities",
                    Description = "列出全部可用的自动化操作及其对应的 CLI 命令与 MCP 工具名。",
                    InputSchema = Schema(@"{""type"":""object"",""properties"":{}}"),
                    Invoke = args => Execute(service, "capabilities", args)
                }
            };

            return tools;
        }

        private static McpToolDefinition AudioTool(string name, string operation, string description)
        {
            return new McpToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""format"":{""type"":""string"",""enum"":[""aac"",""mp3"",""wav"",""m4a""],""description"":""输出音频格式，默认 aac（流复制）""},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath""]}"),
                Operation = operation
            };
        }

        private static McpToolDefinition AudioRemoveTool()
        {
            return new McpToolDefinition
            {
                Name = "videotools_audio_remove",
                Description = "删除视频音轨，视频流直接复制不重新编码。",
                InputSchema = JobSchema(@"{""type"":""object"",""properties"":{""filePath"":{""type"":""string"",""description"":""输入视频绝对路径""},""outputPath"":{""type"":""string""},""outputDir"":{""type"":""string""},""overwrite"":{""type"":""boolean""}},""required"":[""filePath""]}"),
                Operation = "audio_remove"
            };
        }

        private static Dictionary<string, object> Execute(AutomationService service, string operation, Dictionary<string, object> args)
        {
            return service.Execute(operation, args, null, CancellationToken.None);
        }

        private static Dictionary<string, object> Result(string operation, Dictionary<string, object> extra)
        {
            var result = new Dictionary<string, object> { { "ok", true }, { "operation", operation } };
            foreach (var pair in extra) result[pair.Key] = pair.Value;
            return result;
        }

        private static Dictionary<string, object> Schema(string json)
        {
            return Json.ParseObject(json);
        }

        /// <summary>执行类工具统一追加 wait / timeoutMs 参数。</summary>
        private static Dictionary<string, object> JobSchema(string json)
        {
            var schema = Json.ParseObject(json);
            var properties = Json.AsObject(schema.ContainsKey("properties") ? schema["properties"] : null);
            if (properties == null)
            {
                properties = new Dictionary<string, object>();
                schema["properties"] = properties;
            }
            properties["wait"] = new Dictionary<string, object>
            {
                { "type", "boolean" },
                { "description", "是否等待处理完成，默认 true；false 时立即返回任务 ID" }
            };
            properties["timeoutMs"] = new Dictionary<string, object>
            {
                { "type", "integer" },
                { "description", "最长等待毫秒数，默认 300000" }
            };
            return schema;
        }
    }
}
