using System;
using System.Collections.Generic;
using System.IO;

namespace VideoTools.Automation
{
    /// <summary>
    /// 与桌面端共用的 INI 配置读写（AppData\.VideoTools_250405\config.ini）。
    /// </summary>
    public class IniConfig
    {
        private readonly string _filePath;

        public IniConfig(string filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath) ? AppPaths.ConfigFile : filePath;
            var directory = Path.GetDirectoryName(_filePath);
            AppPaths.EnsureDirectory(directory);
            if (!File.Exists(_filePath))
            {
                File.WriteAllText(_filePath, "[Program]\nFFmpegPath=\nGpuUse=disable\nGpuSelect=-1\nCpuMax=disable\n");
            }
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public string Read(string section, string key)
        {
            if (!File.Exists(_filePath)) return null;
            var sectionPattern = "[" + section + "]";
            var inSection = false;
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var trimmed = line.Trim();
                if (trimmed == sectionPattern)
                {
                    inSection = true;
                    continue;
                }
                if (inSection && trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    break;
                }
                if (inSection && trimmed.Contains("="))
                {
                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts[0].Trim() == key) return parts[1].Trim();
                }
            }
            return null;
        }

        public Dictionary<string, string> ReadSection(string section)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_filePath)) return result;
            var sectionPattern = "[" + section + "]";
            var inSection = false;
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var trimmed = line.Trim();
                if (trimmed == sectionPattern)
                {
                    inSection = true;
                    continue;
                }
                if (inSection && trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    break;
                }
                if (inSection && trimmed.Contains("="))
                {
                    var parts = trimmed.Split(new[] { '=' }, 2);
                    result[parts[0].Trim()] = parts[1].Trim();
                }
            }
            return result;
        }

        public void Write(string section, string key, string value)
        {
            var lines = File.Exists(_filePath) ? new List<string>(File.ReadAllLines(_filePath)) : new List<string>();
            var sectionPattern = "[" + section + "]";
            var foundSection = false;
            var sectionStart = -1;
            var sectionEnd = lines.Count;

            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() == sectionPattern)
                {
                    foundSection = true;
                    sectionStart = i;
                    for (var j = i + 1; j < lines.Count; j++)
                    {
                        if (lines[j].Trim().StartsWith("[") && lines[j].Trim().EndsWith("]"))
                        {
                            sectionEnd = j;
                            break;
                        }
                    }
                    break;
                }
            }

            if (foundSection)
            {
                var keyFound = false;
                for (var i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Contains("="))
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts[0].Trim() == key)
                        {
                            lines[i] = key + "=" + value;
                            keyFound = true;
                            break;
                        }
                    }
                }
                if (!keyFound) lines.Insert(sectionEnd, key + "=" + value);
            }
            else
            {
                lines.Add(sectionPattern);
                lines.Add(key + "=" + value);
            }

            File.WriteAllLines(_filePath, lines.ToArray());
        }

        public string FfmpegPath
        {
            get { return Read("Program", "FFmpegPath"); }
            set { Write("Program", "FFmpegPath", value ?? string.Empty); }
        }

        public bool GpuEnabled
        {
            get
            {
                var value = Read("Program", "GpuUse");
                if (string.IsNullOrEmpty(value)) return false;
                value = value.Trim().ToLowerInvariant();
                return value == "enable" || value == "enbale" || value == "true" || value == "1";
            }
            set { Write("Program", "GpuUse", value ? "enable" : "disable"); }
        }

        /// <summary>0=Intel qsv, 1=Nvidia nvenc, 2=AMD amf，-1 表示未选择。</summary>
        public int GpuVendorIndex
        {
            get
            {
                var value = Read("Program", "GpuSelect");
                int index;
                if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out index)) return index;
                return -1;
            }
            set { Write("Program", "GpuSelect", value.ToString()); }
        }

        public bool CpuMax
        {
            get
            {
                var value = Read("Program", "CpuMax");
                return !string.IsNullOrEmpty(value) && value.Trim().ToLowerInvariant() == "enable";
            }
            set { Write("Program", "CpuMax", value ? "enable" : "disable"); }
        }

        /// <summary>界面上的“视频文件输出路径”。</summary>
        public string OutputDirectory
        {
            get
            {
                var value = Read("Program", "VideoSaveFloder");
                if (string.IsNullOrWhiteSpace(value)) value = Read("Program", "OutputDir");
                return string.IsNullOrWhiteSpace(value) ? AppPaths.DefaultOutputDirectory : value;
            }
            set
            {
                Write("Program", "VideoSaveFloder", value ?? string.Empty);
                Write("Program", "OutputDir", value ?? string.Empty);
            }
        }
    }
}
