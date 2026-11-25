using System;
using System.IO;

namespace VideoTools.Automation
{
    /// <summary>
    /// 统一管理配置与数据路径。
    /// 便携优先：程序目录下 data 可写时，配置与任务记录都放在程序旁边，整个文件夹可拷走；
    /// 若程序目录不可写（如 C:\Program Files），自动回退到 %APPDATA%。
    /// </summary>
    public static class AppPaths
    {
        public const string AppFolderName = ".VideoTools_250405";
        public const string PortableDataFolderName = "data";

        private static string _dataDirectory;

        /// <summary>程序根目录；若可执行文件位于 automation 子目录，则取上一级，保证 GUI、CLI、MCP 共用同一份配置。</summary>
        public static string ExeDirectory
        {
            get
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(Path.GetFileName(dir), "automation", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }
                return dir;
            }
        }

        public static string DataDirectory
        {
            get
            {
                if (_dataDirectory == null) _dataDirectory = ResolveDataDirectory();
                return _dataDirectory;
            }
        }

        /// <summary>true 表示配置保存在程序目录，属于便携模式。</summary>
        public static bool IsPortable
        {
            get { return DataDirectory.StartsWith(ExeDirectory, StringComparison.OrdinalIgnoreCase); }
        }

        public static string ConfigFile
        {
            get { return Path.Combine(DataDirectory, "config.ini"); }
        }

        public static string JobsDirectory
        {
            get { return Path.Combine(DataDirectory, "jobs"); }
        }

        public static string DefaultOutputDirectory
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); }
        }

        private static string ResolveDataDirectory()
        {
            var overrideDir = Environment.GetEnvironmentVariable("VIDEOTOOLS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir)) return EnsureDirectory(overrideDir.Trim());

            var portable = Path.Combine(ExeDirectory, PortableDataFolderName);
            if (IsWritableDirectory(portable))
            {
                MigrateLegacyConfig(portable);
                return portable;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        }

        /// <summary>
        /// 首次以便携模式运行时，把旧的 %APPDATA% 配置复制过来，避免用户重复设置 ffmpeg。
        /// </summary>
        private static void MigrateLegacyConfig(string portableDirectory)
        {
            try
            {
                var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, "config.ini");
                var target = Path.Combine(portableDirectory, "config.ini");
                if (File.Exists(target) || !File.Exists(legacy)) return;
                if (string.Equals(Path.GetDirectoryName(legacy), portableDirectory, StringComparison.OrdinalIgnoreCase)) return;
                File.Copy(legacy, target);
            }
            catch (Exception)
            {
                // 迁移失败不影响使用，按默认配置重新设置即可
            }
        }

        public static string EnsureDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>目录是否可创建且可写（用于判定便携模式与输出目录可用性）。</summary>
        public static bool IsWritableDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                /* 文件名带随机后缀，避免批处理并发检测同一目录时互相抢占 */
                var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
                using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 确保输出目录可用；目录不存在或不可写时回退到系统“视频”目录，再回退到 data\output。
        /// </summary>
        public static string EnsureOutputDirectory(string preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred) && IsWritableDirectory(preferred.Trim())) return preferred.Trim();

            var myVideos = DefaultOutputDirectory;
            if (!string.IsNullOrWhiteSpace(myVideos) && IsWritableDirectory(myVideos)) return myVideos;

            return EnsureDirectory(Path.Combine(DataDirectory, "output"));
        }

        /// <summary>把配置中的相对路径按程序目录解析，便于整个文件夹拷到别的电脑。</summary>
        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var value = path.Trim().Trim('"');
            if (Path.IsPathRooted(value)) return value;
            return Path.GetFullPath(Path.Combine(ExeDirectory, value));
        }

        /// <summary>
        /// MCP 服务可执行文件位置：优先取应用目录下 automation 子目录。
        /// </summary>
        public static string FindMcpExecutable()
        {
            var candidates = new[]
            {
                Path.Combine(ExeDirectory, "automation", "videotools-mcp.exe"),
                Path.Combine(ExeDirectory, "videotools-mcp.exe")
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return candidates[0];
        }

        public static string FindCliExecutable()
        {
            var candidates = new[]
            {
                Path.Combine(ExeDirectory, "automation", "videotools.exe"),
                Path.Combine(ExeDirectory, "videotools.exe")
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return candidates[0];
        }
    }
}
