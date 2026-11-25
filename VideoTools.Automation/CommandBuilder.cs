using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VideoTools.Automation
{
    public class CommandPlan
    {
        public string Operation { get; set; }
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public string Arguments { get; set; }
        public double TotalSeconds { get; set; }
        public string CodecLabel { get; set; }
        public bool Overwrite { get; set; }

        public string DisplayCommand
        {
            get { return "ffmpeg " + Arguments; }
        }
    }

    /// <summary>
    /// 把参数转换成 ffmpeg 命令行，规则与桌面端保持一致（码率、GPU 回退、命名等）。
    /// </summary>
    public static class CommandBuilder
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, List<string>> EncoderCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] Operations =
        {
            "compress", "convert", "trim", "gif", "speed", "resize",
            "audio_extract", "audio_remove", "audio_replace"
        };

        public static bool IsOperation(string name)
        {
            return Array.IndexOf(Operations, name) >= 0;
        }

        public static CommandPlan Build(string operation, IDictionary<string, object> args, IniConfig config, string ffmpegPath)
        {
            if (!IsOperation(operation)) throw ToolException.InvalidArgument("不支持的操作：" + operation);

            var inputPath = ResolveInputPath(args);
            if (!File.Exists(inputPath)) throw ToolException.InvalidArgument("输入文件不存在：" + inputPath);

            var overwrite = Json.Bool(args, "overwrite") ?? false;
            var media = MediaProbe.Probe(ffmpegPath, inputPath);
            var builder = new StringBuilder();
            builder.Append("-hide_banner -nostdin ");
            builder.Append(config.CpuMax ? "-threads 0 " : "-threads " + Math.Max(1, Environment.ProcessorCount / 2) + " ");
            builder.Append("-i ").Append(FfmpegLocator.Quote(inputPath)).Append(' ');
            if (overwrite) builder.Append("-y ");

            var plan = new CommandPlan
            {
                Operation = operation,
                InputPath = inputPath,
                TotalSeconds = media.DurationSeconds,
                Overwrite = overwrite
            };

            switch (operation)
            {
                case "compress":
                    AppendCompress(builder, args, config, ffmpegPath, media, inputPath, plan);
                    break;
                case "convert":
                    AppendConvert(builder, args, config, ffmpegPath, inputPath, plan);
                    break;
                case "trim":
                    AppendTrim(builder, args, config, ffmpegPath, media, inputPath, plan);
                    break;
                case "gif":
                    AppendGif(builder, args, config, media, inputPath, plan);
                    break;
                case "speed":
                    AppendSpeed(builder, args, config, ffmpegPath, media, inputPath, plan);
                    break;
                case "resize":
                    AppendResize(builder, args, config, media, inputPath, plan);
                    break;
                case "audio_extract":
                case "audio_remove":
                case "audio_replace":
                    AppendAudio(builder, operation, args, config, inputPath, plan);
                    break;
            }

            builder.Append("-threads ").Append(config.CpuMax ? "0" : Math.Max(1, Environment.ProcessorCount / 2).ToString()).Append(' ');
            builder.Append(FfmpegLocator.Quote(plan.OutputPath));

            plan.Arguments = builder.ToString();
            return plan;
        }

        private static string ResolveInputPath(IDictionary<string, object> args)
        {
            var inputPath = Json.Str(args, "filePath") ?? Json.Str(args, "input") ?? Json.Str(args, "file");
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                var files = Json.StrList(args, "files");
                if (files.Count > 0) inputPath = files[0];
            }
            if (string.IsNullOrWhiteSpace(inputPath)) throw ToolException.InvalidArgument("缺少输入文件参数 filePath。");
            return Path.GetFullPath(inputPath.Trim());
        }

        private static void AppendCompress(StringBuilder builder, IDictionary<string, object> args, IniConfig config, string ffmpegPath, MediaInfo media, string inputPath, CommandPlan plan)
        {
            var codec = (Json.Str(args, "codec", "h264") ?? "h264").Trim().ToLowerInvariant();
            if (codec == "h.264" || codec == "x264") codec = "h264";
            if (codec == "h.265" || codec == "hevc" || codec == "x265") codec = "h265";
            if (codec != "h264" && codec != "h265" && codec != "av1" && codec != "mpeg4")
            {
                throw ToolException.InvalidArgument("不支持的视频编码：" + codec + "（可选 h264/h265/av1/mpeg4）。");
            }

            var crf = Json.Int(args, "crf") ?? 25;
            if (crf < 0 || crf > 51) throw ToolException.InvalidArgument("crf 必须在 0-51 之间。");
            var preset = Json.Str(args, "preset", "medium") ?? "medium";

            string encoder;
            bool hardware;
            var vendor = ResolveGpuVendor(args, config);
            if (vendor == null)
            {
                encoder = CpuEncoderFor(codec);
                hardware = false;
            }
            else
            {
                encoder = PickHardwareEncoder(ffmpegPath, codec, vendor);
                hardware = true;
                if (encoder == null)
                {
                    encoder = CpuEncoderFor(codec);
                    hardware = false;
                }
            }

            builder.Append("-c:v ").Append(encoder).Append(' ');
            var parameterLabel = "crf" + crf.ToString(CultureInfo.InvariantCulture);
            if (hardware)
            {
                /* 与桌面界面共用同一套换算；并按源码率封顶，避免“压缩后反而变大” */
                var targetBitrate = BitratePolicy.CapToSource(
                    BitratePolicy.TargetMbps(media.Width, media.Height, crf), media.BitRateKbps);
                builder.Append("-b:v ").Append(targetBitrate.ToString("F2", CultureInfo.InvariantCulture)).Append("M ");
                parameterLabel = "bv" + targetBitrate.ToString("F2", CultureInfo.InvariantCulture) + "M";
            }
            else
            {
                builder.Append("-crf ").Append(crf.ToString(CultureInfo.InvariantCulture)).Append(' ');
            }
            builder.Append("-preset ").Append(preset).Append(' ');
            builder.Append("-c:a copy ");

            var extension = Path.GetExtension(inputPath);
            var hardwareLabel = hardware ? (encoder.Contains("nvenc") ? "nvenc" : encoder.Contains("qsv") ? "qsv" : encoder.Contains("amf") ? "amf" : "gpu") : "cpu";
            var suffix = "_compress_" + codec + "_" + hardwareLabel + "_" + parameterLabel + "_" + Timestamp();

            plan.CodecLabel = encoder;
            plan.OutputPath = ResolveOutputPath(inputPath, args, config, suffix, extension);
        }

        private static void AppendConvert(StringBuilder builder, IDictionary<string, object> args, IniConfig config, string ffmpegPath, string inputPath, CommandPlan plan)
        {
            var format = (Json.Str(args, "format", "mp4") ?? "mp4").Trim().ToLowerInvariant().TrimStart('.');
            if (format != "mp4" && format != "avi" && format != "mkv" && format != "mov" && format != "webm")
            {
                throw ToolException.InvalidArgument("不支持的目标格式：" + format + "（可选 mp4/avi/mkv/mov/webm）。");
            }

            var vendor = ResolveGpuVendor(args, config);
            string encoderArgs;
            switch (format)
            {
                case "mp4":
                    encoderArgs = vendor != null && HasEncoderCached(ffmpegPath, "h264_" + vendor)
                        ? "-c:v h264_" + vendor + " -c:a aac "
                        : "-c:v libx264 -c:a aac ";
                    break;
                case "avi":
                    encoderArgs = "-c:v mpeg4 -c:a libmp3lame ";
                    break;
                case "mkv":
                    encoderArgs = vendor != null && HasEncoderCached(ffmpegPath, "h264_" + vendor)
                        ? "-c:v h264_" + vendor + " -c:a copy "
                        : "-c:v libx264 -c:a copy ";
                    break;
                case "mov":
                    encoderArgs = "-c:v libx264 -preset medium -c:a aac ";
                    break;
                default:
                    encoderArgs = "-c:v libvpx-vp9 -c:a libopus ";
                    break;
            }

            builder.Append(encoderArgs);
            plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_format_" + Timestamp(), "." + format);
        }

        private static void AppendTrim(StringBuilder builder, IDictionary<string, object> args, IniConfig config, string ffmpegPath, MediaInfo media, string inputPath, CommandPlan plan)
        {
            var start = FfmpegRunner.ParseTime(Json.Str(args, "start", "0"));
            var hasDuration = args.ContainsKey("duration") && args["duration"] != null;
            var hasEnd = args.ContainsKey("end") && args["end"] != null;
            double duration;
            if (hasDuration)
            {
                duration = FfmpegRunner.ParseTime(Json.Str(args, "duration"));
            }
            else if (hasEnd)
            {
                duration = FfmpegRunner.ParseTime(Json.Str(args, "end")) - start;
            }
            else
            {
                duration = media.DurationSeconds - start;
            }

            if (start < 0) throw ToolException.InvalidArgument("start 不能小于 0。");
            if (duration <= 0) throw ToolException.InvalidArgument("裁剪时长必须大于 0。");
            if (media.DurationSeconds > 0 && start >= media.DurationSeconds) throw ToolException.InvalidArgument("start 超出视频总时长。");

            builder.Append("-ss ").Append(FfmpegRunner.ToTimeArgument(start)).Append(' ');
            builder.Append("-t ").Append(duration.ToString("F3", CultureInfo.InvariantCulture)).Append(' ');

            var reencode = Json.Bool(args, "reencode") ?? false;
            if (reencode)
            {
                builder.Append("-c:v libx264 -preset medium -crf ").Append((Json.Int(args, "crf") ?? 25).ToString(CultureInfo.InvariantCulture)).Append(" -c:a aac ");
            }
            else
            {
                builder.Append("-c copy ");
            }

            plan.TotalSeconds = duration;
            plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_cut_" + Timestamp(), Path.GetExtension(inputPath));
        }

        private static void AppendGif(StringBuilder builder, IDictionary<string, object> args, IniConfig config, MediaInfo media, string inputPath, CommandPlan plan)
        {
            var format = (Json.Str(args, "format", "gif") ?? "gif").Trim().ToLowerInvariant().TrimStart('.');
            if (format != "gif" && format != "webp") throw ToolException.InvalidArgument("动图格式仅支持 gif 或 webp。");

            var fps = Json.Dbl(args, "fps") ?? 10;
            if (fps <= 0 || fps > 60) throw ToolException.InvalidArgument("fps 必须在 0-60 之间。");
            var width = Json.Int(args, "width") ?? (media.Width > 0 ? media.Width : 480);
            if (width < 2) throw ToolException.InvalidArgument("width 必须大于 2。");
            var palette = Json.Bool(args, "palette") ?? true;
            var loop = Json.Bool(args, "loop") ?? true;

            var start = FfmpegRunner.ParseTime(Json.Str(args, "start", "0"));
            double duration = 0;
            if (args.ContainsKey("duration") && args["duration"] != null)
            {
                duration = FfmpegRunner.ParseTime(Json.Str(args, "duration"));
            }
            else if (args.ContainsKey("end") && args["end"] != null)
            {
                duration = FfmpegRunner.ParseTime(Json.Str(args, "end")) - start;
            }

            if (start > 0 || duration > 0)
            {
                builder.Append("-ss ").Append(FfmpegRunner.ToTimeArgument(start)).Append(' ');
                if (duration > 0) builder.Append("-t ").Append(duration.ToString("F3", CultureInfo.InvariantCulture)).Append(' ');
            }

            var scale = "fps=" + fps.ToString("0.###", CultureInfo.InvariantCulture) + ",scale=" + width + ":-1:flags=lanczos";
            if (format == "webp")
            {
                builder.Append("-vf \"").Append(scale).Append("\" -an -vcodec libwebp -lossless 0 -compression_level 6 -q:v 65 -loop ").Append(loop ? "0" : "1").Append(' ');
            }
            else if (palette)
            {
                builder.Append("-vf \"").Append(scale).Append(",split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse=dither=none\" -an -loop ").Append(loop ? "0" : "1").Append(' ');
            }
            else
            {
                builder.Append("-vf \"").Append(scale).Append("\" -an -loop ").Append(loop ? "0" : "1").Append(' ');
            }

            plan.TotalSeconds = duration > 0 ? duration : Math.Max(0, media.DurationSeconds - start);
            plan.OutputPath = ResolveOutputPath(inputPath, args, config,
                (format == "webp" ? "_webp_" : "_gif_") + Timestamp(), "." + format);
        }

        private static void AppendSpeed(StringBuilder builder, IDictionary<string, object> args, IniConfig config, string ffmpegPath, MediaInfo media, string inputPath, CommandPlan plan)
        {
            var rate = Json.Dbl(args, "rate") ?? Json.Dbl(args, "speed") ?? 0;
            if (rate < 0.1 || rate > 10) throw ToolException.InvalidArgument("倍速 rate 必须在 0.1-10 之间。");
            var mute = Json.Bool(args, "mute") ?? false;

            var codec = (Json.Str(args, "codec", "h264") ?? "h264").Trim().ToLowerInvariant();
            if (codec == "h265" || codec == "hevc") codec = "h265";
            if (codec != "h264" && codec != "h265" && codec != "av1" && codec != "mpeg4") codec = "h264";

            var crf = Json.Int(args, "crf") ?? 25;
            var vendor = ResolveGpuVendor(args, config);
            var encoder = vendor == null ? null : PickHardwareEncoder(ffmpegPath, codec, vendor);
            var hardware = encoder != null;
            if (encoder == null) encoder = CpuEncoderFor(codec);

            var ptsFactor = 1.0 / rate;
            var extension = Path.GetExtension(inputPath);

            if (mute)
            {
                builder.Append("-vf \"setpts=").Append(ptsFactor.ToString("0.###", CultureInfo.InvariantCulture)).Append("*PTS\" -an ");
            }
            else
            {
                var audioChain = BuildAtempoChain(rate);
                builder.Append("-filter_complex \"[0:v]setpts=").Append(ptsFactor.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("*PTS[v];[0:a]").Append(audioChain).Append("[a]\" -map \"[v]\" -map \"[a]\" ");
            }

            builder.Append("-c:v ").Append(encoder).Append(' ');
            if (hardware)
            {
                var targetBitrate = BitratePolicy.CapToSource(
                    BitratePolicy.TargetMbps(media.Width, media.Height, crf), media.BitRateKbps);
                builder.Append("-b:v ").Append(targetBitrate.ToString("F2", CultureInfo.InvariantCulture)).Append("M ");
            }
            else
            {
                builder.Append("-crf ").Append(crf.ToString(CultureInfo.InvariantCulture)).Append(' ');
            }
            builder.Append("-preset medium ");

            if (!mute)
            {
                var extLower = extension.ToLowerInvariant();
                if (extLower == ".webm") builder.Append("-c:a libopus ");
                else if (extLower == ".ogv") builder.Append("-c:a libvorbis ");
                else if (extLower == ".avi" || extLower == ".mpg" || extLower == ".mpeg") builder.Append("-c:a libmp3lame ");
                else builder.Append("-c:a aac ");
            }

            plan.TotalSeconds = media.DurationSeconds > 0 ? media.DurationSeconds / rate : 0;
            plan.OutputPath = ResolveOutputPath(inputPath, args, config,
                "_multiple_x" + rate.ToString("0.###", CultureInfo.InvariantCulture) + "_" + Timestamp(), extension);
        }

        private static void AppendResize(StringBuilder builder, IDictionary<string, object> args, IniConfig config, MediaInfo media, string inputPath, CommandPlan plan)
        {
            var width = Json.Int(args, "width") ?? -1;
            var height = Json.Int(args, "height") ?? -1;
            if (width <= 0 && height <= 0) throw ToolException.InvalidArgument("width 和 height 至少需要一个大于 0（可传 -1 表示按比例自动计算）。");

            builder.Append("-vf \"scale=").Append(width).Append(':').Append(height).Append("\" ");

            plan.OutputPath = ResolveOutputPath(inputPath, args, config,
                "_resize_" + (width > 0 ? width.ToString() : "auto") + "x" + (height > 0 ? height.ToString() : "auto") + "_" + Timestamp(),
                Path.GetExtension(inputPath));
        }

        private static void AppendAudio(StringBuilder builder, string operation, IDictionary<string, object> args, IniConfig config, string inputPath, CommandPlan plan)
        {
            var extension = Path.GetExtension(inputPath);

            if (operation == "audio_extract")
            {
                var format = (Json.Str(args, "format", "aac") ?? "aac").Trim().ToLowerInvariant().TrimStart('.');
                switch (format)
                {
                    case "aac":
                        builder.Append("-vn -acodec copy ");
                        plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_voice_" + Timestamp(), ".aac");
                        break;
                    case "mp3":
                        builder.Append("-vn -c:a libmp3lame -q:a 2 ");
                        plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_voice_" + Timestamp(), ".mp3");
                        break;
                    case "wav":
                        builder.Append("-vn -c:a pcm_s16le ");
                        plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_voice_" + Timestamp(), ".wav");
                        break;
                    case "m4a":
                        builder.Append("-vn -c:a aac -b:a 192k ");
                        plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_voice_" + Timestamp(), ".m4a");
                        break;
                    default:
                        throw ToolException.InvalidArgument("提取音频格式仅支持 aac/mp3/wav/m4a。");
                }
                return;
            }

            if (operation == "audio_remove")
            {
                builder.Append("-an -c:v copy ");
                plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_mute_" + Timestamp(), extension);
                return;
            }

            var audioPath = Json.Str(args, "audioPath") ?? Json.Str(args, "audio");
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                throw ToolException.InvalidArgument("替换音频需要提供有效的 audioPath。");
            }

            builder.Append("-i ").Append(FfmpegLocator.Quote(Path.GetFullPath(audioPath))).Append(' ');

            var extLower = extension.ToLowerInvariant();
            string audioCodec;
            if (extLower == ".webm") audioCodec = "-c:a libopus ";
            else if (extLower == ".ogv") audioCodec = "-c:a libvorbis ";
            else if (extLower == ".avi" || extLower == ".mpg" || extLower == ".mpeg") audioCodec = "-c:a libmp3lame ";
            else audioCodec = "-c:a aac ";

            builder.Append("-filter_complex \"[1:a]apad[a]\" -map 0:v:0 -map \"[a]\" -shortest -c:v copy ").Append(audioCodec);
            plan.OutputPath = ResolveOutputPath(inputPath, args, config, "_replaceaudio_" + Timestamp(), extension);
        }

        private static string ResolveOutputPath(string inputPath, IDictionary<string, object> args, IniConfig config, string suffix, string extension)
        {
            var explicitPath = Json.Str(args, "outputPath") ?? Json.Str(args, "output");
            var overwrite = Json.Bool(args, "overwrite") ?? false;
            var fileName = Path.GetFileNameWithoutExtension(inputPath) + suffix + extension;

            string target;
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                explicitPath = explicitPath.Trim();
                if (Directory.Exists(explicitPath) || explicitPath.EndsWith("\\") || explicitPath.EndsWith("/"))
                {
                    target = Path.Combine(explicitPath, fileName);
                }
                else
                {
                    target = explicitPath;
                    var parent = Path.GetDirectoryName(Path.GetFullPath(target));
                    if (!string.IsNullOrEmpty(parent)) AppPaths.EnsureDirectory(parent);
                }
                if (File.Exists(target) && !overwrite) throw ToolException.OutputExists(target);
                return Path.GetFullPath(target);
            }

            var outputDir = Json.Str(args, "outputDir");
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = config.OutputDirectory;
            outputDir = AppPaths.EnsureOutputDirectory(outputDir);

            target = Path.Combine(outputDir, fileName);
            if (File.Exists(target) && !overwrite)
            {
                var index = 2;
                var baseName = Path.GetFileNameWithoutExtension(inputPath) + suffix;
                do
                {
                    target = Path.Combine(outputDir, baseName + "_" + index + extension);
                    index++;
                } while (File.Exists(target));
            }
            return Path.GetFullPath(target);
        }

        private static string ResolveGpuVendor(IDictionary<string, object> args, IniConfig config)
        {
            var gpu = Json.Str(args, "gpu");
            if (!string.IsNullOrWhiteSpace(gpu))
            {
                gpu = gpu.Trim().ToLowerInvariant();
                if (gpu == "none" || gpu == "cpu" || gpu == "false" || gpu == "0") return null;
                if (gpu == "intel") return "qsv";
                if (gpu == "nvidia") return "nvenc";
                if (gpu == "amd") return "amf";
                if (gpu == "qsv" || gpu == "nvenc" || gpu == "amf") return gpu;
            }

            var useGpu = Json.Bool(args, "useGpu");
            if (useGpu.HasValue)
            {
                if (!useGpu.Value) return null;
                return VendorFromIndex(config.GpuVendorIndex);
            }

            if (!config.GpuEnabled) return null;
            return VendorFromIndex(config.GpuVendorIndex);
        }

        private static string VendorFromIndex(int index)
        {
            switch (index)
            {
                case 0: return "qsv";
                case 1: return "nvenc";
                case 2: return "amf";
                default: return null;
            }
        }

        private static string PickHardwareEncoder(string ffmpegPath, string codec, string vendor)
        {
            if (codec == "mpeg4") return "mpeg4";
            var desired = (codec == "h265" ? "hevc_" : codec + "_") + vendor;
            if (HasEncoderCached(ffmpegPath, desired)) return desired;

            if (vendor == "nvenc" && codec == "av1" && HasEncoderCached(ffmpegPath, "h264_nvenc"))
            {
                return "h264_nvenc";
            }
            return null;
        }

        private static string CpuEncoderFor(string codec)
        {
            switch (codec)
            {
                case "h265": return "libx265";
                case "av1": return "mpeg4";
                case "mpeg4": return "mpeg4";
                default: return "libx264";
            }
        }

        private static bool HasEncoderCached(string ffmpegPath, string encoder)
        {
            List<string> encoders;
            lock (CacheLock)
            {
                if (!EncoderCache.TryGetValue(ffmpegPath, out encoders))
                {
                    encoders = FfmpegRunner.ListEncoders(ffmpegPath);
                    EncoderCache[ffmpegPath] = encoders;
                }
            }
            return encoders.Exists(e => string.Equals(e, encoder, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildAtempoChain(double rate)
        {
            var tempos = new List<double>();
            var remaining = rate;
            const double epsilon = 1e-6;
            if (remaining >= 1.0)
            {
                while (remaining > 2.0 + epsilon)
                {
                    tempos.Add(2.0);
                    remaining /= 2.0;
                }
                tempos.Add(remaining);
            }
            else
            {
                while (remaining < 0.5 - epsilon)
                {
                    tempos.Add(0.5);
                    remaining /= 0.5;
                }
                tempos.Add(remaining);
            }

            var parts = new List<string>();
            foreach (var value in tempos)
            {
                parts.Add("atempo=" + value.ToString("0.###", CultureInfo.InvariantCulture));
            }
            return string.Join(",", parts);
        }

        private static string Timestamp()
        {
            return DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        }
    }
}
