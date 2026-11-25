using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VideoTools
{
    /* 界面语言：system 跟随系统 / zh 中文 / en 英文 */
    internal static class Lang
    {
        public const string System = "system";
        public const string Chinese = "zh";
        public const string English = "en";

        public static string Current = System;

        private static readonly DependencyProperty OriginalProperty =
            DependencyProperty.RegisterAttached("Original", typeof(string), typeof(Lang), new PropertyMetadata(null));

        private static readonly DependencyProperty OriginalTipProperty =
            DependencyProperty.RegisterAttached("OriginalTip", typeof(string), typeof(Lang), new PropertyMetadata(null));

        /* 中文原文 → 英文，键必须是界面里的完整原串 */
        private static readonly Dictionary<string, string> En = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            /* 窗口与侧栏 */
            { "视频工具箱", "Video Toolbox" },
            { "视频处理", "Video" },
            { "格式转换", "Convert" },
            { "视频压缩", "Compress" },
            { "视频裁剪", "Trim" },
            { "视频加速", "Speed" },
            { "修改尺寸", "Resize" },
            { "音频处理", "Audio" },
            { "转动图", "GIF" },
            { "自动化", "Automation" },
            { "设置", "Settings" },
            { "批量压缩", "Batch Compress" },
            { "批量转换", "Batch Convert" },
            { "展开/收起", "Expand/Collapse" },

            /* 压缩 */
            { "视频编码", "Codec" },
            { "无损", "Lossless" },
            { "推荐", "Recommended" },
            { "质量损失较高", "Low quality" },
            { "CRF值越高，视频质量损失越高，文件体积越小", "Higher CRF means lower quality and smaller file" },
            { "可直接输入 0-50，回车生效", "Type 0-50 and press Enter" },
            { "开始处理", "Start" },
            { "目标格式", "Format" },
            { "开始转换", "Convert" },

            /* 裁剪 / 加速 / 尺寸 */
            { "开始位置", "Start" },
            { "结束位置", "End" },
            { "倍数", "Speed" },
            { "加速音频", "Speed up audio" },
            { "勾选后同时加速音频（超出0.5-2倍会自动链式处理）。默认不勾选则关闭音频。", "Also speed up audio (values beyond 0.5-2x are chained). Audio is muted when unchecked." },
            { "目标尺寸", "Target size" },
            { "宽", "W" },
            { "高", "H" },

            /* 音频 */
            { "音频提取", "Extract" },
            { "删除音频", "Remove" },
            { "替换音频", "Replace" },
            { "选择音频", "Choose audio" },

            /* 动图 */
            { "动图格式", "Format" },
            { "动图大小", "Size" },
            { "每秒帧数(FPS)", "FPS" },
            { "启用着色器", "Palette" },
            { "启用着色器可以获得更好的gif质量，但是处理时间以及文件体积会变大！", "Palette improves GIF quality but takes longer and produces larger files." },
            { "动图循环播放", "Loop" },
            { "原始", "Original" },

            /* 文件列表 */
            { "拖放视频文件到这里", "Drop video files here" },
            { "（可直接拖拽多个视频到下方列表导入）", "(drag multiple videos into the list below)" },
            { "文件名", "Name" },
            { "路径", "Path" },
            { "清空列表", "Clear" },
            { "移除该文件", "Remove" },
            { "打开文件", "Open" },
            { "打开视频文件", "Open video" },
            { "选择多个文件", "Select files" },

            /* 设置页 */
            { "ffmpeg程序路径", "FFmpeg path" },
            { "视频处理功能依赖于ffmepg，请自行配置或者选择自动联网下载", "Requires FFmpeg. Set the path or download it." },
            { "视频文件输出路径", "Output folder" },
            { "选择目录", "Browse" },
            { "手动选择", "Browse" },
            { "在线下载", "Download" },
            { "保存", "Save" },
            { "启用GPU加速", "Enable GPU" },
            { "最大CPU利用率", "Max CPU usage" },
            { "取消", "Cancel" },
            { "请务必确保你选择的显卡类型与你电脑匹配，否则可能会无法进行视频处理！", "Make sure the GPU type matches your hardware, otherwise processing may fail." },
            { "勾选将使用允许的最大资源进行处理! 默认使用一半资源!", "Use all CPU cores. Half of the cores are used when unchecked." },
            { "跟随系统", "Follow system" },
            { "简体中文", "Chinese (Simplified)" },

            /* 自动化页 */
            { "自动化（MCP / CLI）", "Automation (MCP / CLI)" },
            { "把压缩、格式转换、裁剪、GIF、倍速、改尺寸、音频处理开放给 Cursor、Claude Code、Codex 等 AI 工具，也可在终端脚本或自动化流水线中运行。automation 文件夹需与主程序同目录，整体复制程序文件夹即可，无需安装。",
              "Expose compress, convert, trim, GIF, speed, resize and audio to Cursor, Claude Code, Codex and other AI tools, or run it from scripts. Keep the automation folder next to the main program; no installation needed." },
            { "MCP 服务程序", "MCP server" },
            { "复制通用配置", "Copy generic config" },
            { "复制 Claude 配置", "Copy Claude config" },
            { "复制 Codex 配置", "Copy Codex config" },
            { "打开自动化目录", "Open automation folder" },
            { "复制 CLI 路径", "Copy CLI path" },
            { "其他 AI 工具（Trae、WorkBuddy、Cherry Studio 等）也复制第一项。", "Other AI tools (Trae, WorkBuddy, Cherry Studio...) also use the first one." },
            { "命令行示例（在 automation 目录下执行，JSON 参数用单引号包住）", "CLI examples (run inside the automation folder; quote JSON with single quotes)" },
            { "JSON 格式，适用于 Cursor、Trae、WorkBuddy、Cherry Studio 等 mcpServers 结构的客户端", "JSON for clients that use mcpServers (Cursor, Trae, WorkBuddy...)" },
            { "状态：已就绪，AI 工具可直接连接", "Status: ready, AI tools can connect" },
            { "状态：未找到 videotools-mcp.exe，请确认 automation 文件夹与主程序同目录", "Status: videotools-mcp.exe not found; keep the automation folder next to the main program" }
        };

        public static bool IsEnglish
        {
            get
            {
                if (Current == English) return true;
                if (Current == Chinese) return false;
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh";
            }
        }

        public static string T(string chinese)
        {
            if (!IsEnglish || string.IsNullOrEmpty(chinese)) return chinese;
            string value;
            return En.TryGetValue(chinese, out value) ? value : chinese;
        }

        /* 遍历窗口，把界面上的中文换成目标语言；原文用附加属性记住，便于来回切换 */
        public static void Apply(DependencyObject root)
        {
            if (root == null) return;
            Localize(root);
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                var dependencyObject = child as DependencyObject;
                if (dependencyObject != null) Apply(dependencyObject);
            }
        }

        private static void Localize(DependencyObject o)
        {
            var window = o as Window;
            if (window != null) Set(window, Window.TitleProperty, window.Title);

            var element = o as FrameworkElement;
            if (element != null && element.ToolTip is string)
            {
                element.ToolTip = Translate(element, OriginalTipProperty, (string)element.ToolTip);
            }

            var textBlock = o as TextBlock;
            if (textBlock != null)
            {
                Set(textBlock, TextBlock.TextProperty, textBlock.Text);
                return;
            }

            var label = o as Label;
            if (label != null && label.Content is string)
            {
                Set(label, Label.ContentProperty, (string)label.Content);
                return;
            }

            var item = o as ComboBoxItem;
            if (item != null && item.Content is string)
            {
                Set(item, ComboBoxItem.ContentProperty, (string)item.Content);
                return;
            }

            var button = o as ButtonBase;
            if (button != null && button.Content is string)
            {
                Set(button, ContentControl.ContentProperty, (string)button.Content);
            }
        }

        private static void Set(DependencyObject o, DependencyProperty property, string current)
        {
            if (string.IsNullOrEmpty(current)) return;
            var original = o.GetValue(OriginalProperty) as string;
            if (original == null)
            {
                if (!HasChinese(current)) return;
                original = current;
                o.SetValue(OriginalProperty, original);
            }
            var target = IsEnglish ? T(original) : original;
            if (!string.Equals(current, target, StringComparison.Ordinal))
            {
                o.SetValue(property, target);
            }
        }

        private static string Translate(DependencyObject o, DependencyProperty cache, string current)
        {
            if (string.IsNullOrEmpty(current)) return current;
            var original = o.GetValue(cache) as string;
            if (original == null)
            {
                if (!HasChinese(current)) return current;
                original = current;
                o.SetValue(cache, original);
            }
            return IsEnglish ? T(original) : original;
        }

        private static bool HasChinese(string text)
        {
            foreach (var c in text)
            {
                if (c >= '\u4e00' && c <= '\u9fa5') return true;
            }
            return false;
        }
    }
}
