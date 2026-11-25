using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VideoTools.Automation
{
    public class MediaStreamInfo
    {
        public string Type { get; set; }
        public string Codec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string ChannelLayout { get; set; }
        public int BitRateKbps { get; set; }
    }

    public class MediaInfo
    {
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public double DurationSeconds { get; set; }
        public int BitRateKbps { get; set; }
        public string Format { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public bool HasVideo { get { return Width > 0 && Height > 0; } }
        public bool HasAudio { get { return !string.IsNullOrEmpty(AudioCodec); } }
        public List<MediaStreamInfo> Streams { get; set; }

        public Dictionary<string, object> ToDictionary()
        {
            var streams = new List<object>();
            if (Streams != null)
            {
                foreach (var stream in Streams)
                {
                    streams.Add(new Dictionary<string, object>
                    {
                        { "type", stream.Type },
                        { "codec", stream.Codec },
                        { "width", stream.Width },
                        { "height", stream.Height },
                        { "fps", Math.Round(stream.Fps, 3) },
                        { "sampleRate", stream.SampleRate },
                        { "channels", stream.Channels },
                        { "channelLayout", stream.ChannelLayout },
                        { "bitRateKbps", stream.BitRateKbps }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "path", Path },
                { "sizeBytes", SizeBytes },
                { "sizeMB", Math.Round(SizeBytes / 1024.0 / 1024.0, 3) },
                { "durationSeconds", Math.Round(DurationSeconds, 3) },
                { "duration", FormatDuration(DurationSeconds) },
                { "bitRateKbps", BitRateKbps },
                { "format", Format },
                { "width", Width },
                { "height", Height },
                { "fps", Math.Round(Fps, 3) },
                { "videoCodec", VideoCodec },
                { "audioCodec", AudioCodec },
                { "sampleRate", SampleRate },
                { "channels", Channels },
                { "hasVideo", HasVideo },
                { "hasAudio", HasAudio },
                { "streams", streams }
            };
        }

        public static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "00:00:00";
            var span = TimeSpan.FromSeconds(seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds);
        }
    }

    /// <summary>
    /// 通过 ffmpeg -i 解析媒体信息，不依赖 ffprobe。
    /// </summary>
    public static class MediaProbe
    {
        private static readonly Regex DurationRegex = new Regex(@"Duration:\s*(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?", RegexOptions.Compiled);
        private static readonly Regex BitrateRegex = new Regex(@"bitrate:\s*(\d+)\s*kb/s", RegexOptions.Compiled);
        private static readonly Regex VideoStreamRegex = new Regex(@"Stream #\d+:\d+.*?: Video: ([^,\s]+)[^\n]*?(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);
        private static readonly Regex AudioStreamRegex = new Regex(@"Stream #\d+:\d+.*?: Audio: ([^,\s]+)[^\n]*?(\d+)\s*Hz,\s*([^,]+)", RegexOptions.Compiled);
        private static readonly Regex FpsRegex = new Regex(@"([\d.]+)\s*fps", RegexOptions.Compiled);
        private static readonly Regex StreamTypeRegex = new Regex(@"Stream #\d+:\d+.*?: (Video|Audio|Subtitle|Data): ([^,\s]+)", RegexOptions.Compiled);

        public static MediaInfo Probe(string ffmpegPath, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw ToolException.InvalidArgument("输入文件不存在：" + filePath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -nostdin -i " + FfmpegLocator.Quote(filePath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            string output;
            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                output = process.StandardError.ReadToEnd();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit(30000);
            }

            var info = new MediaInfo
            {
                Path = filePath,
                SizeBytes = new FileInfo(filePath).Length,
                Streams = new List<MediaStreamInfo>()
            };

            var durationMatch = DurationRegex.Match(output);
            if (durationMatch.Success)
            {
                var fraction = durationMatch.Groups[4].Success ? durationMatch.Groups[4].Value.PadRight(2, '0').Substring(0, 2) : "00";
                info.DurationSeconds = new TimeSpan(
                    int.Parse(durationMatch.Groups[1].Value),
                    int.Parse(durationMatch.Groups[2].Value),
                    int.Parse(durationMatch.Groups[3].Value)).TotalSeconds + int.Parse(fraction) / 100.0;
            }

            var bitrateMatch = BitrateRegex.Match(output);
            if (bitrateMatch.Success) info.BitRateKbps = int.Parse(bitrateMatch.Groups[1].Value);

            var formatMatch = Regex.Match(output, @"Input #0,\s*([^,]+),");
            if (formatMatch.Success) info.Format = formatMatch.Groups[1].Value.Trim();

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var typeMatch = StreamTypeRegex.Match(line);
                if (!typeMatch.Success) continue;

                var stream = new MediaStreamInfo
                {
                    Type = typeMatch.Groups[1].Value,
                    Codec = typeMatch.Groups[2].Value
                };

                if (stream.Type == "Video")
                {
                    var videoMatch = VideoStreamRegex.Match(line);
                    if (videoMatch.Success)
                    {
                        stream.Codec = videoMatch.Groups[1].Value;
                        stream.Width = int.Parse(videoMatch.Groups[2].Value);
                        stream.Height = int.Parse(videoMatch.Groups[3].Value);
                        var fpsMatch = FpsRegex.Match(line);
                        if (fpsMatch.Success)
                        {
                            double fps;
                            if (double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps)) stream.Fps = fps;
                        }
                        if (info.Width == 0)
                        {
                            info.Width = stream.Width;
                            info.Height = stream.Height;
                            info.Fps = stream.Fps;
                            info.VideoCodec = stream.Codec;
                        }
                    }
                }
                else if (stream.Type == "Audio")
                {
                    var audioMatch = AudioStreamRegex.Match(line);
                    if (audioMatch.Success)
                    {
                        stream.Codec = audioMatch.Groups[1].Value;
                        int sampleRate;
                        if (int.TryParse(audioMatch.Groups[2].Value, out sampleRate)) stream.SampleRate = sampleRate;
                        stream.ChannelLayout = audioMatch.Groups[3].Value.Trim();
                        if (string.IsNullOrEmpty(info.AudioCodec))
                        {
                            info.AudioCodec = stream.Codec;
                            info.SampleRate = stream.SampleRate;
                            info.Channels = ChannelCount(stream.ChannelLayout);
                        }
                    }
                }

                var streamBitrate = Regex.Match(line, @"(\d+)\s*kb/s");
                if (streamBitrate.Success) stream.BitRateKbps = int.Parse(streamBitrate.Groups[1].Value);

                info.Streams.Add(stream);
            }

            return info;
        }

        private static int ChannelCount(string layout)
        {
            if (string.IsNullOrEmpty(layout)) return 0;
            var lower = layout.ToLowerInvariant();
            if (lower.Contains("7.1")) return 8;
            if (lower.Contains("5.1")) return 6;
            if (lower.Contains("stereo")) return 2;
            if (lower.Contains("mono")) return 1;
            return 0;
        }
    }
}
