using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VideoTools.Automation
{
    public class RunResult
    {
        public int ExitCode { get; set; }
        public bool Cancelled { get; set; }
        public double ElapsedSeconds { get; set; }
        public string LogTail { get; set; }
        public double MaxProgress { get; set; }
        public int FrameCount { get; set; }
    }

    /// <summary>
    /// 执行 ffmpeg 并解析进度，供 CLI / MCP / GUI 面板共用。
    /// </summary>
    public static class FfmpegRunner
    {
        private static readonly Regex TimeRegex = new Regex(@"time=(\d+):(\d+):(\d+)(?:\.(\d+))?", RegexOptions.Compiled);
        private static readonly Regex SpeedRegex = new Regex(@"speed=\s*([\d.]+)x", RegexOptions.Compiled);
        private static readonly Regex FrameRegex = new Regex(@"frame=\s*(\d+)", RegexOptions.Compiled);

        public static RunResult Run(
            string ffmpegPath,
            string arguments,
            double totalSeconds,
            Action<double, string> onProgress,
            CancellationToken cancellationToken,
            Action<int> onProcessStarted = null)
        {
            var result = new RunResult();
            var log = new Queue<string>();
            var startedAt = DateTime.UtcNow;
            var lastReport = DateTime.MinValue;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (string.IsNullOrEmpty(args.Data)) return;

                    lock (log)
                    {
                        log.Enqueue(args.Data);
                        while (log.Count > 80) log.Dequeue();
                    }

                    var frameMatch = FrameRegex.Match(args.Data);
                    if (frameMatch.Success)
                    {
                        int frames;
                        if (int.TryParse(frameMatch.Groups[1].Value, out frames)) result.FrameCount = frames;
                    }

                    var timeMatch = TimeRegex.Match(args.Data);
                    if (timeMatch.Success && totalSeconds > 0)
                    {
                        var fraction = timeMatch.Groups[4].Success ? timeMatch.Groups[4].Value.PadRight(2, '0').Substring(0, 2) : "00";
                        var current = new TimeSpan(
                            int.Parse(timeMatch.Groups[1].Value),
                            int.Parse(timeMatch.Groups[2].Value),
                            int.Parse(timeMatch.Groups[3].Value)).TotalSeconds + int.Parse(fraction) / 100.0;
                        var progress = Math.Min(100.0, Math.Max(0.0, current / totalSeconds * 100.0));
                        if (progress > result.MaxProgress) result.MaxProgress = progress;

                        if (onProgress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 500)
                        {
                            lastReport = DateTime.UtcNow;
                            var speedMatch = SpeedRegex.Match(args.Data);
                            var speed = speedMatch.Success ? speedMatch.Groups[1].Value + "x" : null;
                            onProgress(progress, speed);
                        }
                    }
                };

                process.Start();
                if (onProcessStarted != null) onProcessStarted(process.Id);

                try
                {
                    process.PriorityClass = ProcessPriorityClass.AboveNormal;
                }
                catch (Exception)
                {
                    // 某些系统权限下无法调整优先级，忽略即可
                }

                CancellationTokenRegistration registration = default(CancellationTokenRegistration);
                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!process.HasExited) process.Kill();
                        }
                        catch (Exception)
                        {
                        }
                    });
                }

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.WaitForExit();
                registration.Dispose();

                result.ExitCode = process.ExitCode;
                result.Cancelled = cancellationToken.IsCancellationRequested;
            }

            result.ElapsedSeconds = Math.Round((DateTime.UtcNow - startedAt).TotalSeconds, 3);
            lock (log)
            {
                result.LogTail = string.Join(Environment.NewLine, log.ToArray());
            }
            if (onProgress != null && result.ExitCode == 0) onProgress(100, null);
            return result;
        }

        /// <summary>列出 ffmpeg 支持的编码器名称。</summary>
        public static List<string> ListEncoders(string ffmpegPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -nostdin -v 0 -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var encoders = new List<string>();
            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(10000);

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = Regex.Match(line, @"^\s*[VAS][F.][S.][X.][B.][D.]\s+(\S+)");
                    if (match.Success) encoders.Add(match.Groups[1].Value);
                }
            }
            return encoders;
        }

        public static bool HasEncoder(string ffmpegPath, string encoderName)
        {
            if (string.IsNullOrEmpty(encoderName)) return false;
            return ListEncoders(ffmpegPath).Exists(e => string.Equals(e, encoderName, StringComparison.OrdinalIgnoreCase));
        }

        public static string GetVersion(string ffmpegPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -nostdin -version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    return firstLine.Length > 0 ? firstLine[0] : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把秒数或 hh:mm:ss 统一转换为秒。</summary>
        public static double ParseTime(string text, double fallback = 0)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Trim();
            // 纯数字按秒处理，避免 TimeSpan 把 "2" 解析成 2 天
            double seconds;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return seconds;
            TimeSpan span;
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out span)) return span.TotalSeconds;
            throw ToolException.InvalidArgument("无法识别的时间格式：" + text + "（支持秒数或 hh:mm:ss）");
        }

        public static string ToTimeArgument(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var span = TimeSpan.FromSeconds(seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
                (int)span.TotalHours, span.Minutes, span.Seconds, span.Milliseconds);
        }
    }
}
