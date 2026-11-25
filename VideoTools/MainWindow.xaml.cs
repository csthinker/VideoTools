using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
//using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;

using IOPath = System.IO.Path;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;

namespace VideoTools
{
    /* 软件配置文件解析 */
    public class IniConfig
    {
        private readonly string _filePath;

        public IniConfig()
        {
            /* 便携优先：程序目录\data 可写时，配置与程序放在一起，整个文件夹可拷到别的电脑；
               程序目录不可写（如安装到 Program Files）时自动回退到 %APPDATA% */
            _filePath = VideoTools.Automation.AppPaths.ConfigFile;
            VideoTools.Automation.AppPaths.EnsureDirectory(IOPath.GetDirectoryName(_filePath));

            /* 如果文件不存在则创建默认配置 */
            if (!File.Exists(_filePath))
            {
                File.WriteAllText(_filePath, "[Program]\nFFmpegPath=\nGpuUse=enbale\nCpuMax=disable");
            }
        }

        public string Read(string section, string key)
        {
            var lines = File.ReadAllLines(_filePath);
            var sectionPattern = $"[{section}]";
            var inSection = false;

            foreach (var line in lines)
            {
                if (line.Trim() == sectionPattern)
                {
                    inSection = true;
                    continue;
                }

                if (inSection && line.Contains('='))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts[0].Trim() == key)
                        return parts[1].Trim();
                }
            }
            return null;
        }

        public void Write(string section, string key, string value)
        {
            var lines = new List<string>(File.ReadAllLines(_filePath));
            var sectionPattern = $"[{section}]";
            var foundSection = false;
            var sectionStart = -1;
            var sectionEnd = lines.Count;

            /* 查找section的起始和结束位置 */
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() == sectionPattern)
                {
                    foundSection = true;
                    sectionStart = i;
                    /* 寻找section的结束位置（下一个section的起始或文件末尾） */
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
                /* 在section范围内查找键 */
                for (var i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Contains('='))
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts[0].Trim() == key)
                        {
                            lines[i] = $"{key}={value}";
                            keyFound = true;
                            break;
                        }
                    }
                }

                if (!keyFound)
                {
                    /* 在section的末尾插入新行 */
                    lines.Insert(sectionEnd, $"{key}={value}");
                }
            }
            else
            {
                /* 添加新section和键值对到文件末尾 */
                lines.Add(sectionPattern);
                lines.Add($"{key}={value}");
            }

            File.WriteAllLines(_filePath, lines.ToArray());
        }
    }
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private DispatcherTimer uiTimer, hideControlTimer, labelClearTimer;
        private bool isDraggingSlider = false, isPlaying = false, isInited = false, isFileOpened = false;
        private string sVideoFilePath, sFFmpegFilePath, sVideoSaveFolder;
        private string videoCodeChoose = "radiobtn_compress_264", sOutVideoCode = $" -c:v libx264";
        private string sConvertVideoCode = $" -c:v libx264", sVideoFormat = $".mp4", sCrf = $"25", sCompressFormat = " -crf 25 ";
        private string sAnimatedImageFormat = ".gif"; /* .gif 或 .webp */
        private string sToolChoose = "compress";
        private CancellationTokenSource _cancellationTokenSource; /* 全局的取消事件 */
        private double baseBitrate = 0.0;
        private int maxThreads = 0;
        private bool isBatchMode = false; /* 批量压缩模式 */
        private bool isCompressSubExpanded = false; /* 侧栏“视频压缩”子项展开状态 */
        private bool isLangInited = false; /* 语言下拉初始化完成标志 */
        private bool isConvertSubExpanded = false;  /* 侧栏“格式转换”子项展开状态 */

        /* ffmpeg 最近一次错误输出（失败时展示原因、写入日志，便于定位问题） */
        private readonly object _ffmpegErrorLock = new object();
        private string _lastFfmpegError = "";

        /* 批量文件项 */
        private class BatchFileItem
        {
            public string FileName { get; set; } = "";
            public string FilePath { get; set; } = "";
        }

        private ObservableCollection<BatchFileItem> batchFiles = new ObservableCollection<BatchFileItem>();
        private static readonly HashSet<string> _videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4",".avi",".wmv",".mov",".mkv",".webm",".flv",".ts",".m2ts",".mts",".mpg",".mpeg",".m4v",".3gp",".3g2",".ogv",".vob",".asf",".mxf",".rmvb",".rm",".y4m"
        };

        public MainWindow()
        {
            InitializeComponent();
            InitializeTimer();

            // 强制固定窗口尺寸
            this.MaxHeight = this.Height;
            this.MaxWidth = this.Width;
            this.MinHeight = this.Height;
            this.MinWidth = this.Width;

            maxThreads = Environment.ProcessorCount;
        }

        /* 全局: 软件启动引导 */
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var config = new IniConfig();
            sFFmpegFilePath = config.Read("Program", "FFmpegPath");
            /* 配置里可能是相对路径（便携包用 ffmpeg.exe），统一解析成可执行文件的绝对路径 */
            sFFmpegFilePath = VideoTools.Automation.FfmpegLocator.Resolve(sFFmpegFilePath) ?? sFFmpegFilePath;
            sVideoSaveFolder = config.Read("Program", "VideoSaveFloder");

            var gpuuse = config.Read("Program", "GpuUse");
            if (string.IsNullOrEmpty(gpuuse)) {
                config.Write("Program", "GpuUse", "disable");
                checkBox_Setting_GpuUse.IsChecked = false;
            }
            else { 
                checkBox_Setting_GpuUse.IsChecked = (gpuuse == "enable");
            }

            var gpuselect = config.Read("Program", "GpuSelect");
            if (string.IsNullOrEmpty(gpuuse))
            {
                config.Write("Program", "GpuSelect", (-1).ToString());
                comboBox_Setting_GpuSelect.Visibility = Visibility.Collapsed;
            }

            if (true == checkBox_Setting_GpuUse.IsChecked)
            {
                comboBox_Setting_GpuSelect.Visibility = Visibility.Visible;
                switch (gpuselect)
                {
                    case "0":
                        sOutVideoCode = " -c:v h264_qsv ";
                        comboBox_Setting_GpuSelect.SelectedIndex = int.Parse(gpuselect);
                        break;
                    case "1":
                        sOutVideoCode = " -c:v h264_nvenc ";
                        comboBox_Setting_GpuSelect.SelectedIndex = int.Parse(gpuselect);
                        break;
                    case "2":
                        sOutVideoCode = " -c:v h264_amf ";
                        comboBox_Setting_GpuSelect.SelectedIndex = int.Parse(gpuselect);
                        break;
                    default:
                        comboBox_Setting_GpuSelect.SelectedIndex = -1;
                        break;
                }
            }
            else {
                comboBox_Setting_GpuSelect.Visibility = Visibility.Collapsed;
            }

            var cpumax = config.Read("Program", "CpuMax");
            if (string.IsNullOrEmpty(cpumax))
            {
                config.Write("Program", "CpuMax", "disable");
                checkBox_Setting_CpuMax.IsChecked = false;
            }
            else
            {
                checkBox_Setting_CpuMax.IsChecked = (cpumax == "enable");
            }

            if (!isFFmpegExist())
            {
                MessageBox.Show($"ffmpeg未配置，请在设置中设置ffmpeg路径", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                radiobtn_setting.IsChecked = true;
                settingPanel.Visibility = Visibility.Visible;
                videoPlayerFrom.Visibility = Visibility.Collapsed;
            }
            else
            {
                /* 软件启动默认选中视频压缩RadioButton */
                radiobtn_compress.IsChecked = true;
                /* 同时显示对应的面板 */
                compressPanel.Visibility = Visibility.Visible;
                textBoxSettingFmmpegPath.Text = sFFmpegFilePath;
            }

            if (string.IsNullOrEmpty(sVideoSaveFolder)) {
                sVideoSaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            textBoxVideoDir.Text = sVideoSaveFolder;

            config = null;

            isInited = true;

            /* 沿用上一次各功能面板的参数 */
            LoadWorkbenchSettings();

            /* 界面语言：默认跟随系统 */
            var language = new IniConfig().Read("Program", "Language");
            Lang.Current = string.IsNullOrEmpty(language) ? Lang.System : language;
            Lang.Apply(this);
            isLangInited = true;
            comboBoxSettingLanguage.SelectedIndex = Lang.Current == Lang.English ? 2 : (Lang.Current == Lang.Chinese ? 1 : 0);
        }

        /* 切换界面语言：立即生效并写入配置 */
        private void ComboBoxSettingLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLangInited) return;
            var index = comboBoxSettingLanguage.SelectedIndex;
            Lang.Current = index == 2 ? Lang.English : (index == 1 ? Lang.Chinese : Lang.System);
            new IniConfig().Write("Program", "Language", Lang.Current);
            Lang.Apply(this);
            RefreshAutomationPanel();
        }

        /* 工作台: 关闭窗口时保存各面板参数 */
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWorkbenchSettings();
        }

        private void SaveWorkbenchSettings()
        {
            try
            {
                var ini = new IniConfig();
                ini.Write("Workbench", "CompressCodec", videoCodeChoose);
                ini.Write("Workbench", "CompressCrf", Math.Round(sliderCompressCrf.Value).ToString(CultureInfo.InvariantCulture));
                ini.Write("Workbench", "ConvertFormat", sVideoFormat.TrimStart('.'));
                ini.Write("Workbench", "GifFormat", sAnimatedImageFormat.TrimStart('.'));
                ini.Write("Workbench", "GifFps", textBox_Gif_Fps.Text);
                ini.Write("Workbench", "GifWidth", textBox_Gif_Width.Text);
                ini.Write("Workbench", "GifHeight", textBox_Gif_Height.Text);
                ini.Write("Workbench", "GifLoop", checkBox_Gif_Loop.IsChecked == true ? "enable" : "disable");
                ini.Write("Workbench", "GifPalette", checkBox_Gif_EnablePalettegen.IsChecked == true ? "enable" : "disable");
                ini.Write("Workbench", "GifStart", txtStartTime.Text);
                ini.Write("Workbench", "GifEnd", txtEndTime.Text);
                ini.Write("Workbench", "CutStart", cut_start_time.Text);
                ini.Write("Workbench", "CutEnd", cut_end_time.Text);
                ini.Write("Workbench", "SpeedRate", sliderMultiple.Value.ToString("0.##", CultureInfo.InvariantCulture));
                ini.Write("Workbench", "SpeedAudio", checkBox_Multiple_AccelerateAudio.IsChecked == true ? "enable" : "disable");
                ini.Write("Workbench", "ResizeWidth", textBox_Size_Width.Text);
                ini.Write("Workbench", "ResizeHeight", textBox_Size_Height.Text);

                string voice = "extract";
                if (radiobtnVoiceDelete.IsChecked == true)
                {
                    voice = "delete";
                }
                else if (radiobtnVoiceReplace.IsChecked == true)
                {
                    voice = "replace";
                }
                ini.Write("Workbench", "VoiceAction", voice);
            }
            catch { }
        }

        /* 启动时恢复上一次保存的面板参数；没有记录则保持默认值 */
        private void LoadWorkbenchSettings()
        {
            try
            {
                var ini = new IniConfig();

                /* 视频压缩：编码器与 CRF */
                SelectRadioByTag(compressPanel, ini.Read("Workbench", "CompressCodec"), RadioBtn_CompressChoose_Click);
                double crf;
                if (TryParseWorkbenchDouble(ini.Read("Workbench", "CompressCrf"), out crf))
                {
                    if (crf < sliderCompressCrf.Minimum) crf = sliderCompressCrf.Minimum;
                    if (crf > sliderCompressCrf.Maximum) crf = sliderCompressCrf.Maximum;
                    sliderCompressCrf.Value = crf;
                    sCrf = Math.Round(crf).ToString(CultureInfo.InvariantCulture);
                    textBoxCompressCrf.Text = sCrf;
                }

                /* 格式转换：目标格式 */
                SelectRadioByTag(convertPanel, ini.Read("Workbench", "ConvertFormat"), RadioBtn_ConvertChoose_Click);

                /* 动图：格式、帧率、尺寸、循环、着色器、区间 */
                string gifFormat = ini.Read("Workbench", "GifFormat");
                if (gifFormat == "webp")
                {
                    radioGifFormatWebp.IsChecked = true;
                    sAnimatedImageFormat = ".webp";
                }
                else if (gifFormat == "gif")
                {
                    radioGifFormatGif.IsChecked = true;
                    sAnimatedImageFormat = ".gif";
                }
                double fps;
                if (TryParseWorkbenchDouble(ini.Read("Workbench", "GifFps"), out fps))
                {
                    if (fps < sliderGifFps.Minimum) fps = sliderGifFps.Minimum;
                    if (fps > sliderGifFps.Maximum) fps = sliderGifFps.Maximum;
                    sliderGifFps.Value = fps;
                    textBox_Gif_Fps.Text = Math.Round(fps).ToString(CultureInfo.InvariantCulture);
                }
                SetWorkbenchText(textBox_Gif_Width, ini.Read("Workbench", "GifWidth"));
                SetWorkbenchText(textBox_Gif_Height, ini.Read("Workbench", "GifHeight"));
                string gifLoop = ini.Read("Workbench", "GifLoop");
                if (!string.IsNullOrEmpty(gifLoop))
                {
                    checkBox_Gif_Loop.IsChecked = gifLoop == "enable";
                }
                string gifPalette = ini.Read("Workbench", "GifPalette");
                if (!string.IsNullOrEmpty(gifPalette))
                {
                    checkBox_Gif_EnablePalettegen.IsChecked = gifPalette == "enable";
                }
                SetWorkbenchText(txtStartTime, ini.Read("Workbench", "GifStart"));
                SetWorkbenchText(txtEndTime, ini.Read("Workbench", "GifEnd"));

                /* 视频裁剪：起止时间 */
                SetWorkbenchText(cut_start_time, ini.Read("Workbench", "CutStart"));
                SetWorkbenchText(cut_end_time, ini.Read("Workbench", "CutEnd"));

                /* 视频加速：倍率与是否加速音频 */
                double rate;
                if (TryParseWorkbenchDouble(ini.Read("Workbench", "SpeedRate"), out rate))
                {
                    if (rate < sliderMultiple.Minimum) rate = sliderMultiple.Minimum;
                    if (rate > sliderMultiple.Maximum) rate = sliderMultiple.Maximum;
                    sliderMultiple.Value = rate;
                    textBoxMultiple.Text = rate.ToString("0.##", CultureInfo.InvariantCulture);
                }
                string speedAudio = ini.Read("Workbench", "SpeedAudio");
                if (!string.IsNullOrEmpty(speedAudio))
                {
                    checkBox_Multiple_AccelerateAudio.IsChecked = speedAudio == "enable";
                }

                /* 修改尺寸：目标宽高 */
                SetWorkbenchText(textBox_Size_Width, ini.Read("Workbench", "ResizeWidth"));
                SetWorkbenchText(textBox_Size_Height, ini.Read("Workbench", "ResizeHeight"));

                /* 音频处理：上一次选择的动作 */
                string voice = ini.Read("Workbench", "VoiceAction");
                if (voice == "delete")
                {
                    radiobtnVoiceDelete.IsChecked = true;
                }
                else if (voice == "replace")
                {
                    radiobtnVoiceReplace.IsChecked = true;
                }
                else if (voice == "extract")
                {
                    radiobtnVoiceExtract.IsChecked = true;
                }
                if (!string.IsNullOrEmpty(voice))
                {
                    RadioBtn_VoiceAction_Click(null, null);
                }
            }
            catch { }
        }

        private static bool TryParseWorkbenchDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void SetWorkbenchText(TextBox box, string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                box.Text = text;
            }
        }

        /* 按 Tag 选中单选项并执行其点击逻辑（代码设置 IsChecked 不会触发 Click） */
        private static void SelectRadioByTag(Panel panel, string tag, Action<object, RoutedEventArgs> handler)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }
            foreach (var child in panel.Children)
            {
                var radio = child as RadioButton;
                if (radio != null && radio.Tag != null &&
                    string.Equals(radio.Tag.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    radio.IsChecked = true;
                    if (handler != null)
                    {
                        handler(radio, null);
                    }
                    return;
                }
            }
        }

        /* 全局: 播放视频 */
        private void PlayVideo(string filePath)
        {
            try
            {
                if (IsVideoFile(filePath))
                {
                    var uri = new Uri(filePath);
                    mediaPlayer.Source = uri;
                    mediaPlayer.Pause();
                    isPlaying = false;

                    /* 显示文件名 */
                    txtFileName.Text = System.IO.Path.GetFileName(filePath);

                    /* 显示控制栏 */
                    hideControlTimer.Stop();
                    overlayGrid.Visibility = Visibility.Visible;
                    /* 显示顶部信息栏 */
                    topBar.Visibility = Visibility.Visible;
                }
                else
                {
                    MessageBox.Show("不支持的文件格式");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"播放失败: {ex.Message}");
            }
        }

        /* 全局: 验证视频格式 */
        private bool IsVideoFile(string filePath)
        {
            try
            {
                var ext = IOPath.GetExtension(filePath).ToLower();
                return _videoExts.Contains(ext);
            }
            catch { return false; }
        }

        /* 全局: 初始化定时器 */
        private void InitializeTimer()
        {
            uiTimer = new DispatcherTimer(DispatcherPriority.Render);
            uiTimer.Interval = TimeSpan.FromMilliseconds(33); /* 约30FPS */
            uiTimer.Tick += Timer_Tick;

            hideControlTimer = new DispatcherTimer();
            hideControlTimer.Interval = TimeSpan.FromSeconds(0.5); /* 0.5秒后隐藏 */
            hideControlTimer.Tick += (s, e) =>
            {
                controlBar.Visibility = Visibility.Collapsed;
                hideControlTimer.Stop();
            };

            // 初始化labelClearTimer
            labelClearTimer = new DispatcherTimer();
            labelClearTimer.Interval = TimeSpan.FromSeconds(2);
            labelClearTimer.Tick += (s, e) =>
            {
                labelSettingInfo.Content = "";
                labelClearTimer.Stop();
            };

        }

        /* 视频窗口: 拖放文件处理（支持批量模式） */
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    if (isBatchMode)
                    {
                        AddBatchFiles(files);
                    }
                    else
                    {
                        PlayVideo(files[0]);
                        sVideoFilePath = files[0];
                    }
                }
            }
        }

        /* 视频窗口: 按钮点击打开文件 */
        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.avi;*.wmv;*.mov;*.mkv;*.webm;*.flv;*.ts;*.m2ts;*.mts;*.mpg;*.mpeg;*.m4v;*.3gp;*.3g2;*.ogv;*.vob;*.asf;*.mxf;*.rmvb;*.rm;*.y4m|所有文件|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                mediaPlayer.Stop();
                playPauseButton.Content = "\ue87c"; /* 播放图标 */
                isPlaying = false;
                /* 暂停时显示控制栏并停止隐藏计时器 */
                controlBar.Visibility = Visibility.Visible;
                hideControlTimer.Stop();
                PlayVideo(openFileDialog.FileName);
                sVideoFilePath = openFileDialog.FileName;
            }
        }

        /* 批量模式：辅助方法与事件 */
        private void AddBatchFiles(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                if (!IsVideoFile(f)) continue;
                if (batchFiles.Any(x => string.Equals(x.FilePath, f, StringComparison.OrdinalIgnoreCase))) continue;
                batchFiles.Add(new BatchFileItem
                {
                    FilePath = f,
                    FileName = IOPath.GetFileName(f)
                });
            }

            Dispatcher.Invoke(() =>
            {
                batchListView.ItemsSource = batchFiles;
            });
        }

        /* 侧边栏：展开/收起“视频压缩”的子项（批量压缩） */
        private void ToggleCompressSub_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            isCompressSubExpanded = !isCompressSubExpanded;
            var panel = this.FindName("compressSubPanel") as FrameworkElement;
            var arrow = this.FindName("txtCompressArrow") as TextBlock;
            if (panel != null)
            {
                panel.Visibility = isCompressSubExpanded ? Visibility.Visible : Visibility.Collapsed;
            }
            if (arrow != null)
            {
                arrow.Text = isCompressSubExpanded ? "⯅" : "⯆";
            }
            e.Handled = true;
        }

        /* 侧边栏：箭头按钮点击切换展开状态 */
        private void ToggleCompressSub_Click(object sender, RoutedEventArgs e)
        {
            isCompressSubExpanded = !isCompressSubExpanded;
            var panel = this.FindName("compressSubPanel") as FrameworkElement;
            var arrow = this.FindName("txtCompressArrow") as TextBlock;
            if (panel != null)
            {
                panel.Visibility = isCompressSubExpanded ? Visibility.Visible : Visibility.Collapsed;
            }
            if (arrow != null)
            {
                arrow.Text = isCompressSubExpanded ? "⯅" : "⯆";
            }
            e.Handled = true;
        }

        /* 侧边栏：格式转换的“批量转换”子项展开/收起 */
        private void ToggleConvertSub_Click(object sender, RoutedEventArgs e)
        {
            isConvertSubExpanded = !isConvertSubExpanded;
            var panel = this.FindName("convertSubPanel") as FrameworkElement;
            var arrow = this.FindName("txtConvertArrow") as TextBlock;
            if (panel != null)
            {
                panel.Visibility = isConvertSubExpanded ? Visibility.Visible : Visibility.Collapsed;
            }
            if (arrow != null)
            {
                arrow.Text = isConvertSubExpanded ? "⯅" : "⯆";
            }
            e.Handled = true;
        }

        private void UpdateVideoAreaForBatchMode()
        {
            if (isBatchMode)
            {
                overlayGrid.Visibility = Visibility.Collapsed;
                controlBar.Visibility = Visibility.Collapsed;
                mediaPlayer.Visibility = Visibility.Collapsed;
                topBar.Visibility = Visibility.Collapsed;
                batchArea.Visibility = Visibility.Visible;
            }
            else
            {
                batchArea.Visibility = Visibility.Collapsed;
                mediaPlayer.Visibility = Visibility.Visible;
                topBar.Visibility = isFileOpened ? Visibility.Visible : Visibility.Collapsed;
                overlayGrid.Visibility = isFileOpened ? Visibility.Collapsed : Visibility.Visible;
                controlBar.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /* 视频窗口: 如果鼠标进入视频区域 */
        private void PlayerArea_MouseEnter(object sender, MouseEventArgs e)
        {
            if (isFileOpened) {
                controlBar.Visibility = Visibility.Visible;
                hideControlTimer.Stop();
            }
        }

        /* 视频窗口: 如果鼠标离开视频区域 */
        private void PlayerArea_MouseLeave(object sender, MouseEventArgs e)
        {
            if (isPlaying)
            {
                hideControlTimer.Start();
            }
        }

        /* 视频窗口: 在控制栏本身补充事件（防止操作时隐藏）*/
        private void ControlBar_MouseEnter(object sender, MouseEventArgs e)
        {
            hideControlTimer.Stop();
        }

        private void ControlBar_MouseLeave(object sender, MouseEventArgs e)
        {
            if (isPlaying)
            {
                hideControlTimer.Start();
            }
        }

        /* 视频窗口: 定时器事件（更新进度）*/
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!isDraggingSlider && mediaPlayer.Source != null)
            {
                Dispatcher.Invoke(DispatcherPriority.Render, new Action(() =>
                {
                    progressSlider.Value = mediaPlayer.Position.TotalSeconds;
                    timeText.Text = $"{mediaPlayer.Position:hh\\:mm\\:ss} / {mediaPlayer.NaturalDuration:hh\\:mm\\:ss}";
                }));
            }
        }

        /* 视频窗口: 播放/暂停按钮 */
        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (isPlaying)
            {
                mediaPlayer.Pause();
                playPauseButton.Content = "\ue87c"; /* 播放图标 */
                isPlaying = false;
                /* 暂停时显示控制栏并停止隐藏计时器 */
                controlBar.Visibility = Visibility.Visible;
                hideControlTimer.Stop();
            }
            else
            {
                Trace.WriteLine("play...");
                mediaPlayer.Play();
                playPauseButton.Content = "\ue87a"; /* 暂停图标 */
                uiTimer.Start();
                isPlaying = true;
            }
        }

        /* 视频窗口: 停止按钮 */
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            mediaPlayer.Stop();
            playPauseButton.Content = "\ue87c";
            uiTimer.Stop();
            isPlaying = false;
            controlBar.Visibility = Visibility.Visible;
        }

        /* 视频窗口: 实现一个数学函数 */
        public static class MathHelper {
            public static double Clamp(double value, double min, double max) {
                return Math.Max(min, Math.Min(max, value));
            }
        }

        /* 视频窗口: 进度条值改变 */
        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            //Trace.WriteLine("ProgressSlider_ValueChanged... isDraggingSlider = " + isDraggingSlider.ToString());
            if (isDraggingSlider && mediaPlayer.NaturalDuration.HasTimeSpan) { 
                double newPosition = MathHelper.Clamp(
                    progressSlider.Value,
                    0,
                    mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds
                );
                mediaPlayer.Position = TimeSpan.FromSeconds(newPosition);

                // 实时更新时间显示
                timeText.Text = $"{TimeSpan.FromSeconds(newPosition):hh\\:mm\\:ss} / {mediaPlayer.NaturalDuration.TimeSpan:hh\\:mm\\:ss}";
            }
        }

        /* 视频窗口: 进度条拖动按下开始 */
        private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            //Trace.WriteLine("ProgressSlider_PreviewMouseDown...");
            if (mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                isDraggingSlider = true;
                uiTimer.Stop(); // 关键：暂停定时器
                mediaPlayer.Pause(); // 暂停播放避免冲突
            }
        }

        /* 视频窗口: 进度条拖动/点击结束 */
        private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            //Trace.WriteLine("ProgressSlider_PreviewMouseUp...");
            if (mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                isDraggingSlider = false;
                mediaPlayer.Play();
                uiTimer.Start();
            }
        }

        /* 视频窗口: 音量控制 */
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            mediaPlayer.Volume = volumeSlider.Value;
        }

        /* 视频窗口: 计算基准比特率（与自动化共用同一套规则：按画面短边取档，竖屏按 1080 计） */
        private double CalculateBaseBitrate(double width, double height)
        {
            return VideoTools.Automation.BitratePolicy.BaseMbps((int)width, (int)height);
        }

        /* 视频窗口: 视频加载成功 */
        private void MediaElement_MediaOpened(object sender, RoutedEventArgs e)
        {
            /* 显示下方控制栏 */
            hideControlTimer.Stop();
            controlBar.Visibility = Visibility.Visible;

            /* 隐藏叠加层 */
            overlayGrid.Visibility = Visibility.Collapsed;
            
            isFileOpened = true;

            // 获取视频的宽度和高度
            int videoWidth = mediaPlayer.NaturalVideoWidth;
            int videoHeight = mediaPlayer.NaturalVideoHeight;

            if ((videoHeight > 0) && (videoWidth > 0))
            {
                textBox_Size_Height.Text = textBox_Gif_Height.Text = videoHeight.ToString();
                textBox_Size_Width.Text = textBox_Gif_Width.Text = videoWidth.ToString();
            }

            if (mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                baseBitrate = CalculateBaseBitrate(videoWidth, videoHeight);
                progressSlider.Maximum = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                /* 导入后初始化裁剪范围与文本框 */
                cut_start_time.Text = "00:00:00";
                cut_end_time.Text = mediaPlayer.NaturalDuration.TimeSpan.ToString(@"hh\:mm\:ss");
                startPositionSlider.Value = 0;
                endPositionSlider.Value = 100;
                /* 同步 GIF 文本框默认值（便于其它功能） */
                if (txtStartTime != null) txtStartTime.Text = "00:00:00";
                if (txtEndTime != null) txtEndTime.Text = cut_end_time.Text;
                uiTimer.Start();
            }
        }

        /* 视频窗口: 视频加载失败 */
        private void MediaElement_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // 捕获并识别常见的 HRESULT 错误码
            int hr = e.ErrorException?.HResult ?? 0;
            string reason = e.ErrorException?.Message ?? "未知错误";

            if (hr == unchecked((int)0xC00D109B))
            {
                MessageBox.Show(
                    "该视频无法在预览窗口播放（可能码率/分辨率过高或解码器不支持，例如8K）。\n但仍可使用FFmpeg进行压缩或转换。",
                    "预览不支持",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            else
            {
                MessageBox.Show($"视频预览失败，但仍可使用FFmpeg进行处理。\n原因: {reason}", "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 恢复叠加层显示（预览不可用），但不清空路径，以便压缩/转换等功能继续工作
            overlayGrid.Visibility = Visibility.Visible;
            // 在叠加层上提示用户仍可处理
            var tip = this.FindName("txtInstruction") as TextBlock;
            if (tip != null)
            {
                tip.Text = "预览不可用，但仍可进行压缩/转换";
            }
        }

        /* 视频窗口: 视频播放结束 */
        private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            mediaPlayer.Stop();
            isPlaying = false;
            playPauseButton.Content = "";
            uiTimer.Stop();
        }

        /* 侧边栏选择事件 */
        private void RadioBtn_Compress_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as RadioButton;
            string tag = button.Tag.ToString();
            string panelName = tag + "Panel";
            sToolChoose = tag;

            if ("compress_batch" == tag)
            {
                isBatchMode = true;
                panelName = "compressPanel"; // 使用压缩面板配置
                sToolChoose = "compress";    // 复用压缩逻辑
            }
            else if ("convert_batch" == tag)
            {
                isBatchMode = true;
                panelName = "convertPanel";  // 使用格式转换面板配置
                sToolChoose = "convert";     // 复用转换逻辑
            }
            else
            {
                isBatchMode = false;
            }

            if ("setting" != sToolChoose && "automation" != sToolChoose)
            {
                videoPlayerFrom.Visibility = Visibility.Visible;
            }
            else
            {
                videoPlayerFrom.Visibility = Visibility.Collapsed;
            }

            /* 使用反射找到对应面板 */
            var panel = this.FindName(panelName) as FrameworkElement;
            if (panel != null)
            {
                /* 隐藏所有面板 */
                foreach (var child in (panel.Parent as Grid).Children)
                {
                    if (child is FrameworkElement element && element != panel)
                    {
                        element.Visibility = Visibility.Collapsed;
                    }
                }

                /* 显示当前面板 */
                panel.Visibility = Visibility.Visible;
                if ("automation" == tag) RefreshAutomationPanel();
            }

            UpdateVideoAreaForBatchMode();
        }

        /* AI 自动化面板：展示 MCP 路径与可复制的客户端配置 */
        private void RefreshAutomationPanel()
        {
            var mcpPath = VideoTools.Automation.AppPaths.FindMcpExecutable();
            textBoxAutomationMcpPath.Text = mcpPath;

            var exists = File.Exists(mcpPath);
            textBlockAutomationStatus.Text = exists
                ? Lang.T("状态：已就绪，AI 工具可直接连接")
                : Lang.T("状态：未找到 videotools-mcp.exe，请确认 automation 文件夹与主程序同目录");
            textBlockAutomationStatus.Foreground = exists ? Brushes.SeaGreen : Brushes.IndianRed;

            textBoxAutomationCommands.Text =
                ".\\videotools.exe probe \"D:\\video.mp4\"\r\n" +
                ".\\videotools.exe compress \"D:\\video.mp4\" --crf 28 --output-dir \"D:\\out\"\r\n" +
                ".\\videotools.exe trim \"D:\\video.mp4\" --start 00:00:05 --duration 10\r\n" +
                ".\\videotools.exe batch --files a.mp4,b.mp4 --operation compress --params '{\\\"crf\\\":30}'\r\n" +
                ".\\videotools.exe setup mcp --client claude --install";
        }

        private void BtnAutomationCopyCursor_Click(object sender, RoutedEventArgs e)
        {
            CopyAutomationConfig("cursor");
        }

        private void BtnAutomationCopyClaude_Click(object sender, RoutedEventArgs e)
        {
            CopyAutomationConfig("claude");
        }

        private void BtnAutomationCopyCodex_Click(object sender, RoutedEventArgs e)
        {
            CopyAutomationConfig("codex");
        }

        /* 用 Win32 直接写剪贴板：被其他程序占用时 OpenClipboard 立即返回失败，
           不会像 WPF Clipboard 那样在界面线程里长时间重试导致卡死 */
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private static bool TrySetClipboardOnce(string text)
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            var handle = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard()) return false;
                var bytes = (uint)((text.Length + 1) * sizeof(char));
                handle = GlobalAlloc(GmemMoveable, new UIntPtr(bytes));
                if (handle == IntPtr.Zero) return false;
                var target = GlobalLock(handle);
                if (target == IntPtr.Zero) return false;
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
                if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero) return false;
                handle = IntPtr.Zero; /* 数据所有权已交给系统 */
                return true;
            }
            finally
            {
                if (handle != IntPtr.Zero) GlobalFree(handle);
                CloseClipboard();
            }
        }

        /* 在后台线程重试并复制，界面保持响应 */
        private void CopyTextToClipboard(string text, string successMessage, string failureMessage)
        {
            textBlockAutomationStatus.Foreground = Brushes.DimGray;
            textBlockAutomationStatus.Text = "正在复制…";
            var thread = new Thread(() =>
            {
                var copied = false;
                for (var attempt = 0; attempt < 20 && !copied; attempt++)
                {
                    copied = TrySetClipboardOnce(text);
                    if (!copied) Thread.Sleep(40);
                }
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        textBlockAutomationStatus.Text = copied ? successMessage : failureMessage;
                        textBlockAutomationStatus.Foreground = copied ? Brushes.SeaGreen : Brushes.IndianRed;
                    }));
                }
                catch (Exception)
                {
                    // 窗口已关闭时忽略
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /* 剪贴板不可用时把配置落盘，AI 可以直接读文件，无需粘贴 */
        private static string SaveSnippetToFile(string client, string snippet)
        {
            var extension = "codex" == client ? ".toml" : ".json";
            var path = IOPath.Combine(VideoTools.Automation.AppPaths.DataDirectory, "mcp-config" + extension);
            File.WriteAllText(path, snippet, new UTF8Encoding(false));
            return path;
        }

        private void CopyAutomationConfig(string client)
        {
            try
            {
                var config = new VideoTools.Automation.IniConfig();
                var ffmpeg = VideoTools.Automation.FfmpegLocator.Resolve(config, sFFmpegFilePath);
                var snippet = "codex" == client
                    ? VideoTools.Automation.Mcp.McpConfig.BuildCodexToml(ffmpeg)
                    : VideoTools.Automation.Mcp.McpConfig.BuildClientJson(ffmpeg);
                var saved = SaveSnippetToFile(client, snippet);
                string doneText = "cursor" == client
                    ? "已复制通用配置（JSON）。粘贴到 Cursor、Trae、WorkBuddy、Cherry Studio 等工具的 mcpServers 中即可。"
                    : "已复制 " + client + " 配置到剪贴板（同时保存到 data\\mcp-config），粘贴到对应客户端的 MCP 设置中即可。";
                CopyTextToClipboard(
                    snippet,
                    doneText,
                    "复制失败：剪贴板被其他程序占用。配置已保存到 " + saved + "，可让 AI 直接读取该文件。");
            }
            catch (Exception ex)
            {
                textBlockAutomationStatus.Text = "复制失败：" + ex.Message;
                textBlockAutomationStatus.Foreground = Brushes.IndianRed;
            }
        }

        private void BtnAutomationOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(VideoTools.Automation.AppPaths.FindMcpExecutable());
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if (!string.IsNullOrEmpty(directory)) Process.Start("explorer.exe", directory);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message);
            }
        }

        private void BtnAutomationCopyCli_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CopyTextToClipboard(
                    VideoTools.Automation.AppPaths.FindCliExecutable(),
                    "已复制 CLI 路径到剪贴板。",
                    "复制失败：剪贴板被其他程序占用，请稍后重试。");
            }
            catch (Exception ex)
            {
                textBlockAutomationStatus.Text = "复制失败：" + ex.Message;
                textBlockAutomationStatus.Foreground = Brushes.IndianRed;
            }
        }

        /* ffmpeg: 判断ffmpeg文件是否存在 */
        private bool isFFmpegExist()
        {

            if (string.IsNullOrEmpty(sFFmpegFilePath))
            {
                return false;
            }

            return File.Exists(sFFmpegFilePath);
        }

        /* ffmpeg: 调用ffmpeg执行命令 */
        public Int32 ffmpegProcess(CancellationToken cancellationToken, string sCmd, bool allowFallback = true)
        {

            if (!isFFmpegExist())
            {
                return -1;
            }

            lock (_ffmpegErrorLock) { _lastFfmpegError = ""; }
            int exitCode = 0;

            var ffmpeg = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = sFFmpegFilePath,
                    Arguments = sCmd,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            try
            {
                // 获取视频总时长
                var durationProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = sFFmpegFilePath,
                        Arguments = $"-i \"{sVideoFilePath}\"",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                durationProcess.Start();
                string output = durationProcess.StandardError.ReadToEnd();
                durationProcess.WaitForExit();

                // 解析视频时长
                var durationMatch = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2})\.(\d{2})");
                double totalSeconds = 0;
                if (durationMatch.Success)
                {
                    totalSeconds = TimeSpan.Parse($"{durationMatch.Groups[1]}:{durationMatch.Groups[2]}:{durationMatch.Groups[3]}.{durationMatch.Groups[4]}").TotalSeconds;
                }

                // 检查是否有时间截取参数
                var ssMatch = System.Text.RegularExpressions.Regex.Match(sCmd, @"-ss (\d{2}:\d{2}:\d{2})");
                var tMatch = System.Text.RegularExpressions.Regex.Match(sCmd, @"-t (\d+)");

                if (ssMatch.Success && tMatch.Success)
                {
                    var startTime = TimeSpan.Parse(ssMatch.Groups[1].Value);
                    var duration = int.Parse(tMatch.Groups[1].Value);
                    totalSeconds = duration; // 使用截取的时长作为总时长
                }

                // 解析总帧数（用于GIF转换）
                var frameMatch = System.Text.RegularExpressions.Regex.Match(output, @"Stream.*Video:.*,\s*(\d+)\s*fps");
                double totalFrames = 0;
                if (frameMatch.Success)
                {
                    double fps = double.Parse(frameMatch.Groups[1].Value);
                    totalFrames = fps * totalSeconds;
                }

                ffmpeg.Start();
                // 设置进程优先级
                ffmpeg.PriorityClass = ProcessPriorityClass.AboveNormal;

                // 注册取消事件
                cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!ffmpeg.HasExited)
                        {
                            ffmpeg.Kill();
                        }
                    }
                    catch { }
                });

                // 异步读取错误输出
                ffmpeg.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        CollectFfmpegError(args.Data);
                        bool isGifConversion = sCmd.Contains("palettegen") || sCmd.Contains("paletteuse");

                        if (isGifConversion)
                        {
                            var _frameMatch = System.Text.RegularExpressions.Regex.Match(args.Data, @"frame=\s*(\d+)");
                            if (_frameMatch.Success && totalFrames > 0)
                            {
                                int currentFrame = int.Parse(_frameMatch.Groups[1].Value);
                                double progress = (currentFrame / totalFrames) * 100;

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    progressBarOverlayRunning.Value = Math.Min(progress, 100);
                                });
                            }
                        }
                        else
                        {
                            var timeMatch = System.Text.RegularExpressions.Regex.Match(args.Data, @"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
                            if (timeMatch.Success && totalSeconds > 0)
                            {
                                var currentTime = TimeSpan.Parse($"{timeMatch.Groups[1]}:{timeMatch.Groups[2]}:{timeMatch.Groups[3]}.{timeMatch.Groups[4]}");
                                double progress = (currentTime.TotalSeconds / totalSeconds) * 100;

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    progressBarOverlayRunning.Value = Math.Min(progress, 100);
                                });
                            }
                        }

                        // 输出处理速度信息
                        var speedMatch = System.Text.RegularExpressions.Regex.Match(args.Data, @"speed=\s*(\d+\.?\d*)x");
                        if (speedMatch.Success)
                        {
                            string speed = speedMatch.Groups[1].Value;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                textBoxOverlayRunning.Text = $"处理中，速度 {speed}x···";
                            });
                        }
                        else
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (isGifConversion && (true == checkBox_Gif_EnablePalettegen.IsChecked))
                                {
                                    textBoxOverlayRunning.Text = $"启用着色器, 耗时较久请耐心等待！\n正在准备开始处理任务···";
                                }
                                else
                                {
                                    textBoxOverlayRunning.Text = $"正在准备开始处理任务···";
                                }
                            });
                        }
                    }
                };

                ffmpeg.BeginErrorReadLine();
                ffmpeg.WaitForExit();
                exitCode = ffmpeg.HasExited ? ffmpeg.ExitCode : -1;
            }
            finally
            {
                ffmpeg.Dispose();
            }

            /* 用户主动取消：保持原样，由调用方清理半成品 */
            if (cancellationToken.IsCancellationRequested)
            {
                return exitCode;
            }

            if (exitCode != 0)
            {
                /* 硬件编码失败（驱动/设备不支持）时自动回退 CPU 编码重试一次，避免只生成 0KB 文件 */
                string cpuCmd = allowFallback ? BuildCpuFallbackCommand(sCmd) : null;
                if (!string.IsNullOrEmpty(cpuCmd))
                {
                    WriteFfmpegErrorLog(sCmd, exitCode, "hardware-fallback");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        textBoxOverlayRunning.Text = "硬件编码不可用，已自动改用 CPU 重试···";
                    });
                    return ffmpegProcess(cancellationToken, cpuCmd, false);
                }

                /* 失败时把 ffmpeg 的真实原因返回给界面，而不是静默结束 */
                string detail = GetFfmpegErrorTail(1200);
                WriteFfmpegErrorLog(sCmd, exitCode, "failed");
                throw new Exception($"ffmpeg 处理失败（退出码 {exitCode}）" + (string.IsNullOrEmpty(detail) ? "" : "\n\n" + detail));
            }

            return exitCode;
        }

        /* 记录 ffmpeg 错误输出（只保留尾部，避免无限增长） */
        private void CollectFfmpegError(string line)
        {
            lock (_ffmpegErrorLock)
            {
                _lastFfmpegError += line + "\n";
                if (_lastFfmpegError.Length > 8000)
                {
                    _lastFfmpegError = _lastFfmpegError.Substring(_lastFfmpegError.Length - 8000);
                }
            }
        }

        private string GetFfmpegErrorTail(int maxLength)
        {
            string text;
            lock (_ffmpegErrorLock) { text = _lastFfmpegError; }
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            if (text.Length > maxLength)
            {
                text = text.Substring(text.Length - maxLength);
            }
            return text.Trim();
        }

        /* 失败详情落盘，方便用户反馈问题时直接附上日志 */
        private void WriteFfmpegErrorLog(string cmd, int exitCode, string stage)
        {
            try
            {
                string dir = IOPath.Combine(VideoTools.Automation.AppPaths.DataDirectory, "logs");
                Directory.CreateDirectory(dir);
                string file = IOPath.Combine(dir, $"ffmpeg-error-{DateTime.Now.ToString("yyMMdd-HHmmss")}.log");
                var sb = new StringBuilder();
                sb.AppendLine("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("stage: " + stage);
                sb.AppendLine("exitCode: " + exitCode.ToString());
                sb.AppendLine("ffmpeg: " + sFFmpegFilePath);
                sb.AppendLine("cmd: " + cmd);
                sb.AppendLine();
                sb.AppendLine(GetFfmpegErrorTail(8000));
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /* 硬件编码器对 preset 的取值支持不同：AMF/QSV 传 medium 会直接报错，改用编码器默认值 */
        private string BuildPresetArgument()
        {
            string code = sOutVideoCode ?? "";
            if (code.IndexOf("_amf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                code.IndexOf("_qsv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return " ";
            }
            return " -preset medium ";
        }

        /* 把硬件编码命令改写为 CPU 编码命令；不是硬件编码时返回 null */
        private string BuildCpuFallbackCommand(string cmd)
        {
            string result = cmd;
            bool replaced = false;
            var map = new Dictionary<string, string>
            {
                { "h264_nvenc", "libx264" }, { "h264_qsv", "libx264" }, { "h264_amf", "libx264" },
                { "hevc_nvenc", "libx265" }, { "hevc_qsv", "libx265" }, { "hevc_amf", "libx265" },
                { "av1_nvenc", "libsvtav1" }, { "av1_qsv", "libsvtav1" }, { "av1_amf", "libsvtav1" }
            };
            foreach (var kv in map)
            {
                if (result.Contains(kv.Key))
                {
                    result = result.Replace(kv.Key, kv.Value);
                    replaced = true;
                }
            }
            if (!replaced)
            {
                return null;
            }

            /* 硬件用的固定码率参数对 libx* 不合适，换成 CRF 25 */
            result = System.Text.RegularExpressions.Regex.Replace(result, @"-b:v\s+[\d.]+M", "-crf 25");

            /* 首次失败可能已留下同名半成品，重试时强制覆盖，避免 ffmpeg 等待确认而卡住 */
            return "-y " + result;
        }

        /* ffmpeg: 根据不同事件组合不同的ffmpeg命令行参数 */
        private string makeFfmpegCmd(out string sOutDir, out string sOutFilePath)
        {
            string sCmd = "", suffix = "", extension = "", newFileName = "", destinationPath = "";
            string formattedTime = DateTime.Now.ToString("yyMMddHHmmss");
            string fileName = IOPath.GetFileNameWithoutExtension(sVideoFilePath);
            string targetFolder = textBoxVideoDir.Text;
            /* 目录不在则新建 */
            Directory.CreateDirectory(targetFolder);

            parseCompressVideoCode();
            parseConvertVideoCode();

            /* 解码端不强制硬件加速，避免部分视频或驱动不兼容导致失败。
             * 保留（下方）编码端的 GPU 选择以获取性能。*/

            if (true == checkBox_Setting_CpuMax.IsChecked)
            {
                sCmd += " -threads 0 ";
            }
            else
            {
                sCmd += " -threads ";
                sCmd += (maxThreads / 2).ToString();
                sCmd += " ";
            }

            sCmd += $"-i ";
            sCmd += $"\"{sVideoFilePath}\"";

            switch (sToolChoose)
            {
                case "compress":
                    suffix = "_compress_" + formattedTime;
                    extension = IOPath.GetExtension(sVideoFilePath);
                    break;
                case "convert":
                    suffix = "_format_" + formattedTime;
                    extension = sVideoFormat;
                    break;
                case "gif":
                    if (sAnimatedImageFormat == ".webp")
                    {
                        suffix = "_webp_" + formattedTime;
                        extension = ".webp";
                    }
                    else
                    {
                        suffix = "_gif_" + formattedTime;
                        extension = ".gif";
                    }
                    break;
                case "cut":
                    suffix = "_cut_" + formattedTime;
                    extension = IOPath.GetExtension(sVideoFilePath);
                    break;
                case "resize":
                    suffix = "_resize_" + textBox_Size_Width.Text.ToString() + "x" + textBox_Size_Height.Text.ToString() + "_" + formattedTime;
                    extension = IOPath.GetExtension(sVideoFilePath);
                    break;
                case "multiple":
                    suffix = "_multiple_x" + textBoxMultiple.Text.ToString() + "_" + formattedTime;
                    extension = IOPath.GetExtension(sVideoFilePath);
                    break;
                case "voice":
                    if (true == radiobtnVoiceExtract.IsChecked)
                    {
                        suffix = "_voice_" + formattedTime;
                        extension = ".aac";
                    }
                    else if (true == radiobtnVoiceDelete.IsChecked)
                    {
                        suffix = "_mute_" + formattedTime;
                        extension = IOPath.GetExtension(sVideoFilePath);
                    }
                    else if (true == radiobtnVoiceReplace.IsChecked)
                    {
                        suffix = "_replaceaudio_" + formattedTime;
                        extension = IOPath.GetExtension(sVideoFilePath);
                    }
                    break;
                default:
                    sOutDir = "";
                    sOutFilePath = "";
                    return "";
            }

            /* 组合文件名 */
            newFileName = $"{fileName}{suffix}{extension}";
            /* 组合文件路径 */
            destinationPath = IOPath.Combine(targetFolder, newFileName);

            /* 根据是否实际使用硬件编码设置码率：硬件用固定码率，CPU用CRF */
            bool isHardwareEncoder = !string.IsNullOrEmpty(sOutVideoCode) &&
                (sOutVideoCode.Contains("_nvenc") || sOutVideoCode.Contains("_qsv") || sOutVideoCode.Contains("_amf"));
            if (true == checkBox_Setting_GpuUse.IsChecked && isHardwareEncoder)
            {
                sCompressFormat = $" -b:v ";
                /* 未能获取视频尺寸时（预览失败）按 1080p 基准回退，避免 0M 导致编码失败 */
                double targetBitrate = baseBitrate > 0
                    ? baseBitrate * Math.Pow(2, (25 - double.Parse(sCrf)) / 6.0) * 0.4
                    : VideoTools.Automation.BitratePolicy.TargetMbps(1920, 1080, int.Parse(sCrf));
                /* 与自动化保持一致：目标码率不超过源码率，避免“压缩后反而变大” */
                if (!string.IsNullOrEmpty(sVideoFilePath) && File.Exists(sVideoFilePath) && mediaPlayer.NaturalDuration.HasTimeSpan)
                {
                    var seconds = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                    if (seconds > 0)
                    {
                        var sourceKbps = (int)(new FileInfo(sVideoFilePath).Length * 8.0 / seconds / 1000.0);
                        targetBitrate = VideoTools.Automation.BitratePolicy.CapToSource(targetBitrate, sourceKbps);
                    }
                }
                sCompressFormat += targetBitrate.ToString("F2");
                sCompressFormat += "M";
            }
            else
            {
                sCompressFormat = $" -crf ";
                sCompressFormat += sCrf;
            }

            /* 如果是压缩任务，完善输出文件名后缀，便于比较压缩效果 */
            if (sToolChoose == "compress")
            {
                string codecLabel = "video";
                string lowerCode = sOutVideoCode.ToLower();
                if (lowerCode.Contains("h264") || lowerCode.Contains("libx264"))
                {
                    codecLabel = "h264";
                }
                else if (lowerCode.Contains("hevc") || lowerCode.Contains("libx265") || lowerCode.Contains("x265"))
                {
                    codecLabel = "h265";
                }
                else if (lowerCode.Contains("av1"))
                {
                    codecLabel = "av1";
                }
                else if (lowerCode.Contains("mpeg4"))
                {
                    codecLabel = "mpeg4";
                }

                string hwLabel = (true == checkBox_Setting_GpuUse.IsChecked) ?
                    (lowerCode.Contains("nvenc") ? "nvenc" : (lowerCode.Contains("qsv") ? "qsv" : (lowerCode.Contains("amf") ? "amf" : "gpu")))
                    : "cpu";

                string paramLabel;
                if (sCompressFormat.StartsWith(" -b:v "))
                {
                    string bv = sCompressFormat.Replace(" -b:v ", string.Empty).Trim(); // 形如 "4.50M"
                    paramLabel = $"bv{bv}";
                }
                else
                {
                    paramLabel = $"crf{sCrf}";
                }

                suffix = $"_compress_{codecLabel}_{hwLabel}_{paramLabel}_" + formattedTime;
                newFileName = $"{fileName}{suffix}{extension}";
                destinationPath = IOPath.Combine(targetFolder, newFileName);
            }

            switch (sToolChoose)
            {
                case "compress":
                    sCmd += sOutVideoCode;
                    sCmd += sCompressFormat; /* crf or bv */
                    sCmd += BuildPresetArgument() + " -c:a copy ";
                    break;
                case "convert":
                    sCmd += sConvertVideoCode;
                    //sCmd += $" -crf 25 "; /* 转码不启用crf压缩 */
                    break;
                case "gif":

                    int timeRangeInSeconds = GetTimeRangeInSeconds();
                    if (timeRangeInSeconds > 0)
                    {
                        sCmd += $" -ss ";
                        sCmd += txtStartTime.Text.ToString();
                        sCmd += $" -t ";
                        sCmd += timeRangeInSeconds.ToString();
                    }
                    if (sAnimatedImageFormat == ".webp")
                    {
                        sCmd += " -vf \"fps=";
                        sCmd += textBox_Gif_Fps.Text.ToString();
                        sCmd += ",scale=";
                        sCmd += textBox_Gif_Width.Text.ToString();
                        sCmd += ":-1:flags=lanczos\" -an ";
                        sCmd += " -vcodec libwebp -lossless 0 -compression_level 6 -q:v 65 -loop ";
                        sCmd += (true == checkBox_Gif_Loop.IsChecked) ? "0" : "1";
                    }
                    else
                    {
                        sCmd += " -vf \"fps=";
                        sCmd += textBox_Gif_Fps.Text.ToString();
                        sCmd += ",scale=";
                        sCmd += textBox_Gif_Width.Text.ToString();
                        if (true == checkBox_Gif_EnablePalettegen.IsChecked)
                        {
                            sCmd += ":-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse=dither=none\" -an -loop ";
                        }
                        else
                        {
                            sCmd += ":-1:flags=lanczos\" -an -loop ";
                        }
                        sCmd += (true == checkBox_Gif_Loop.IsChecked) ? "0" : "1";
                    }
                    break;
                case "cut":
                    int cutRangeSeconds = GetCutTimeRangeInSeconds();
                    if (cutRangeSeconds <= 0)
                    {
                        sOutDir = "";
                        sOutFilePath = "";
                        return ""; /* 无效区间 */
                    }
                    sCmd += " -ss ";
                    sCmd += cut_start_time.Text.ToString();
                    sCmd += " -t ";
                    sCmd += cutRangeSeconds.ToString();
                    sCmd += " -c copy ";
                    break;
                case "resize":
                    sCmd += $" -vf \"scale=";
                    sCmd += textBox_Size_Width.Text.ToString();
                    sCmd += ":";
                    sCmd += textBox_Size_Height.Text.ToString();
                    sCmd += "\" ";
                    break;
                case "multiple":
                    if (double.TryParse(textBoxMultiple.Text, out double value) && value > 0)
                    {
                        double ptsFactor = 1.0 / value;
                        if (true == checkBox_Multiple_AccelerateAudio.IsChecked)
                        {
                            // 构建 atempo 链，确保每段在 [0.5, 2.0] 范围内
                            var atempos = new System.Collections.Generic.List<double>();
                            double remaining = value;
                            const double EPS = 1e-6;
                            if (remaining >= 1.0)
                            {
                                while (remaining > 2.0 + EPS)
                                {
                                    atempos.Add(2.0);
                                    remaining /= 2.0;
                                }
                                atempos.Add(remaining);
                            }
                            else
                            {
                                while (remaining < 0.5 - EPS)
                                {
                                    atempos.Add(0.5);
                                    remaining /= 0.5;
                                }
                                atempos.Add(remaining);
                            }
                            string audioChain = string.Join(",", atempos.Select(v => $"atempo={v:0.###}"));
                            sCmd += $" -filter_complex \"[0:v]setpts={ptsFactor:0.##}*PTS[v];[0:a]{audioChain}[a]\" -map \"[v]\" -map \"[a]\" ";
                            // 指定视频/音频编码与压缩参数，避免默认编码体积暴涨
                            sCmd += sOutVideoCode;
                            sCmd += sCompressFormat; /* crf 或 bv */
                            sCmd += BuildPresetArgument();
                            // 根据容器选择合适的音频编码
                            string extLower = extension.ToLower();
                            if (extLower == ".webm")
                            {
                                sCmd += " -c:a libopus ";
                            }
                            else if (extLower == ".ogv")
                            {
                                sCmd += " -c:a libvorbis ";
                            }
                            else if (extLower == ".avi" || extLower == ".mpg" || extLower == ".mpeg")
                            {
                                sCmd += " -c:a libmp3lame ";
                            }
                            else
                            {
                                sCmd += " -c:a aac ";
                            }
                        }
                        else
                        {
                            // 仅加速视频，关闭音频
                            sCmd += $" -vf \"setpts={ptsFactor:0.##}*PTS\" -an ";
                            sCmd += sOutVideoCode;
                            sCmd += sCompressFormat; /* crf 或 bv */
                            sCmd += BuildPresetArgument();
                        }
                    }
                    else
                    {
                        // 非法或缺省倍速值时的兜底
                        if (true == checkBox_Multiple_AccelerateAudio.IsChecked)
                        {
                            sCmd += " -filter_complex \"[0:v]setpts=PTS[v];[0:a]atempo=1.0[a]\" -map \"[v]\" -map \"[a]\" ";
                            sCmd += sOutVideoCode;
                            sCmd += sCompressFormat; /* crf 或 bv */
                            sCmd += BuildPresetArgument();
                            string extLower2 = extension.ToLower();
                            if (extLower2 == ".webm")
                            {
                                sCmd += " -c:a libopus ";
                            }
                            else if (extLower2 == ".ogv")
                            {
                                sCmd += " -c:a libvorbis ";
                            }
                            else if (extLower2 == ".avi" || extLower2 == ".mpg" || extLower2 == ".mpeg")
                            {
                                sCmd += " -c:a libmp3lame ";
                            }
                            else
                            {
                                sCmd += " -c:a aac ";
                            }
                        }
                        else
                        {
                            sCmd += " -vf \"setpts=PTS\" -an ";
                            sCmd += sOutVideoCode;
                            sCmd += sCompressFormat; /* crf 或 bv */
                            sCmd += BuildPresetArgument();
                        }
                    }
                    break;
                case "voice":
                    if (true == radiobtnVoiceExtract.IsChecked)
                    {
                        sCmd += $" -vn -acodec copy ";
                    }
                    else if (true == radiobtnVoiceDelete.IsChecked)
                    {
                        sCmd += $" -an -c:v copy ";
                    }
                    else if (true == radiobtnVoiceReplace.IsChecked)
                    {
                        // 校验外部音频路径
                        string audioPath = textBoxVoiceReplacePath.Text?.Trim() ?? "";
                        if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                        {
                            MessageBox.Show("请选择有效的音频文件以进行替换！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                            sOutDir = ""; sOutFilePath = ""; return "";
                        }
                        // 添加第二路输入（音频）
                        sCmd += " -i \"" + audioPath + "\"";
                        // 选择合适的音频编码（与容器兼容）
                        string extLower = extension.ToLower();
                        string audioCodec = " -c:a aac ";
                        if (extLower == ".webm")
                        {
                            audioCodec = " -c:a libopus ";
                        }
                        else if (extLower == ".ogv")
                        {
                            audioCodec = " -c:a libvorbis ";
                        }
                        else if (extLower == ".avi" || extLower == ".mpg" || extLower == ".mpeg")
                        {
                            audioCodec = " -c:a libmp3lame ";
                        }
                        else
                        {
                            audioCodec = " -c:a aac ";
                        }

                        // 构建：视频原样拷贝，音频替换；使用 apad 补足音频长度，-shortest 保持视频时长并在音频更长时裁切
                        sCmd += " -filter_complex \"[1:a]apad[a]\" -map 0:v:0 -map \"[a]\" -shortest ";
                        sCmd += " -c:v copy ";
                        sCmd += audioCodec;
                    }
                    break;
            }

            if (true == checkBox_Setting_CpuMax.IsChecked)
            {
                sCmd += " -threads 0 ";
            }
            else
            {
                sCmd += " -threads ";
                sCmd += (maxThreads / 2).ToString();
                sCmd += " ";
            }

            /* 输出文件路径（需加引号以支持含空格路径） */
            sCmd += $" \"{destinationPath}\"";

            sOutDir = targetFolder;
            sOutFilePath = destinationPath;

            return sCmd;
        }

        /* 压缩: 压缩中进度条事件 */
        private void CompressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isInited)
            {
                sCrf = Math.Round(sliderCompressCrf.Value).ToString(CultureInfo.InvariantCulture);
                if (textBoxCompressCrf != null)
                {
                    textBoxCompressCrf.Text = sCrf;
                }
            }
        }

        /* 滚轮微调：每格 ±1，避免拖拽对不准 */
        private void CompressSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!isInited)
            {
                return;
            }
            double step = e.Delta > 0 ? 1 : -1;
            double next = Math.Round(sliderCompressCrf.Value) + step;
            if (next < sliderCompressCrf.Minimum) next = sliderCompressCrf.Minimum;
            if (next > sliderCompressCrf.Maximum) next = sliderCompressCrf.Maximum;
            sliderCompressCrf.Value = next;
            e.Handled = true;
        }

        private void CompressCrfBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyCompressCrfFromText();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                textBoxCompressCrf.Text = sCrf;
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void CompressCrfBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyCompressCrfFromText();
        }

        /* 直接输入压缩率：越界自动夹紧，非数字还原 */
        private void ApplyCompressCrfFromText()
        {
            int value;
            if (!int.TryParse(textBoxCompressCrf.Text.Trim(), out value))
            {
                textBoxCompressCrf.Text = sCrf;
                return;
            }
            if (value < sliderCompressCrf.Minimum) value = (int)sliderCompressCrf.Minimum;
            if (value > sliderCompressCrf.Maximum) value = (int)sliderCompressCrf.Maximum;
            sliderCompressCrf.Value = value;
            sCrf = value.ToString(CultureInfo.InvariantCulture);
            textBoxCompressCrf.Text = sCrf;
        }

        private void parseCompressVideoCode() {
            string sCodeGpuSelect = "qsv";

            switch (comboBox_Setting_GpuSelect.SelectedIndex)
            {
                case 0:
                    sCodeGpuSelect = "qsv";
                    break;
                case 1:
                    sCodeGpuSelect = "nvenc";
                    break;
                case 2:
                    sCodeGpuSelect = "amf";
                    break;
                default:
                    break;
            }

            if (true == checkBox_Setting_GpuUse.IsChecked)
            {
                switch (videoCodeChoose)
                {
                    case "radiobtn_compress_264":
                        sOutVideoCode = $" -c:v h264_{sCodeGpuSelect}";
                        break;
                    case "radiobtn_compress_265":
                        sOutVideoCode = $" -c:v hevc_{sCodeGpuSelect}";
                        break;
                    case "radiobtn_compress_av1":
                        sOutVideoCode = $" -c:v av1_{sCodeGpuSelect}";
                        break;
                    default:
                        sOutVideoCode = $" -c:v h264_{sCodeGpuSelect}";
                        break;
                }

                /* 兼容性检查：如果所选硬件编码器不可用或设备不支持，则安全回退 */
                string desiredEncoder = sOutVideoCode.Replace("-c:v", string.Empty).Trim();
                if (!FfmpegHasEncoder(desiredEncoder))
                {
                    // NVENC 的 AV1 在多数旧卡（如 GTX1650）不支持，优先回退到 H.264 NVENC
                    if (sCodeGpuSelect == "nvenc" && videoCodeChoose == "radiobtn_compress_av1")
                    {
                        sOutVideoCode = " -c:v h264_nvenc";
                        // 若 h264_nvenc 也不可用，则彻底回退到 CPU 编码
                        if (!FfmpegHasEncoder("h264_nvenc"))
                        {
                            sOutVideoCode = " -c:v libx264";
                        }
                    }
                    else
                    {
                        // 其它硬件编码器不可用时，回退到对应的 CPU 编码
                        switch (videoCodeChoose)
                        {
                            case "radiobtn_compress_264":
                                sOutVideoCode = " -c:v libx264";
                                break;
                            case "radiobtn_compress_265":
                                sOutVideoCode = " -c:v libx265";
                                break;
                            case "radiobtn_compress_av1":
                                // CPU 下暂不启用 libaom-av1，保持兼容与速度；使用 mpeg4 兜底
                                sOutVideoCode = " -c:v mpeg4";
                                break;
                            default:
                                sOutVideoCode = " -c:v libx264";
                                break;
                        }
                    }
                }
            }
            else
            {
                switch (videoCodeChoose)
                {
                    case "radiobtn_compress_264":
                        sOutVideoCode = $" -c:v libx264";
                        break;
                    case "radiobtn_compress_265":
                        sOutVideoCode = $" -c:v libx265";
                        break;
                    case "radiobtn_compress_av1":
                        sOutVideoCode = $" -c:v mpeg4";
                        break;
                    default:
                        sOutVideoCode = $" -c:v libx264";
                        break;
                }
            }
        }

        /* 压缩: 压缩中选择不同选项 */
        private void RadioBtn_CompressChoose_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as RadioButton;
            videoCodeChoose = button.Tag.ToString();

            parseCompressVideoCode();
        }

        /* 音频: 选择替换音频文件 */
        private void BtnVoiceChooseAudio_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Title = "选择音频文件";
            dlg.Filter = "音频文件|*.mp3;*.aac;*.wav;*.flac;*.ogg;*.opus;*.m4a;*.wma|所有文件|*.*";
            dlg.CheckFileExists = true;
            dlg.Multiselect = false;
            if (dlg.ShowDialog() == true)
            {
                textBoxVoiceReplacePath.Text = dlg.FileName;
            }
        }

        /* 音频：三个功能互斥，且根据选择显示/隐藏替换音频的文件选择控件 */
        private void RadioBtn_VoiceAction_Click(object sender, RoutedEventArgs e)
        {
            bool isReplace = radiobtnVoiceReplace.IsChecked == true;
            textBoxVoiceReplacePath.Visibility = isReplace ? Visibility.Visible : Visibility.Collapsed;
            btnVoiceChooseAudio.Visibility = isReplace ? Visibility.Visible : Visibility.Collapsed;
        }

        private void parseConvertVideoCode() {
            string sCodeGpuSelect = "qsv";

            switch (comboBox_Setting_GpuSelect.SelectedIndex)
            {
                case 0:
                    sCodeGpuSelect = "qsv";
                    break;
                case 1:
                    sCodeGpuSelect = "nvenc";
                    break;
                case 2:
                    sCodeGpuSelect = "amf";
                    break;
                default:
                    break;
            }

            if (true == checkBox_Setting_GpuUse.IsChecked)
            {
                switch (sVideoFormat)
                {
                    case ".mp4":
                        sConvertVideoCode = $" -c:v h264_{sCodeGpuSelect} -c:a aac ";
                        break;
                    case ".avi":
                        /* AVI容器更常见的是MPEG-4 + MP3，避免使用可能不可用的AV1硬件编码器 */
                        sConvertVideoCode = $" -c:v mpeg4 -c:a libmp3lame ";
                        break;
                    case ".mkv":
                        sConvertVideoCode = $" -c:v h264_{sCodeGpuSelect} -c:a copy ";
                        break;
                    case ".mov":
                        sConvertVideoCode = $" -c:v libx264 -preset medium -c:a aac ";
                        break;
                    case ".webm":
                        sConvertVideoCode = $" -c:v libvpx-vp9 -c:a libopus ";
                        break;
                    default:
                        sConvertVideoCode = $" -c:v h264_{sCodeGpuSelect} -c:a aac ";
                        break;
                }

                // 如果选择了 NVENC/QSV/AMF，但 ffmpeg 缺少相应编码器，安全回退到 CPU
                string encoderToken = sConvertVideoCode.ToLower().Contains("h264_") ? $"h264_{sCodeGpuSelect}" :
                                      (sConvertVideoCode.ToLower().Contains("hevc_") ? $"hevc_{sCodeGpuSelect}" :
                                       (sConvertVideoCode.ToLower().Contains("av1_") ? $"av1_{sCodeGpuSelect}" : ""));
                if (!string.IsNullOrEmpty(encoderToken) && !FfmpegHasEncoder(encoderToken))
                {
                    // 回退为 libx264 + 合适音频编码
                    sConvertVideoCode = " -c:v libx264 -c:a aac ";
                }
            }
            else
            {
                switch (sVideoFormat)
                {
                    case ".mp4":
                        sConvertVideoCode = $" -c:v libx264 -c:a aac ";
                        break;
                    case ".avi":
                        sConvertVideoCode = $" -c:v mpeg4 -c:a libmp3lame ";
                        break;
                    case ".mkv":
                        sConvertVideoCode = $" -c:v libx264 -c:a copy ";
                        break;
                    case ".mov":
                        sConvertVideoCode = $" -c:v libx264 -preset medium -c:a aac ";
                        break;
                    case ".webm":
                        sConvertVideoCode = $" -c:v libvpx-vp9 -c:a libopus ";
                        break;
                    default:
                        sConvertVideoCode = $" -c:v libx264 -c:a aac ";
                        break;
                }
            }
        }

        /* 检测 ffmpeg 是否包含指定编码器（如 h264_nvenc、hevc_qsv 等） */
        private bool FfmpegHasEncoder(string encoderName)
        {
            try
            {
                if (string.IsNullOrEmpty(sFFmpegFilePath) || !File.Exists(sFFmpegFilePath))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = sFFmpegFilePath,
                    Arguments = " -hide_banner -v 0 -encoders",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = new Process { StartInfo = psi })
                {
                    p.Start();
                    string output = p.StandardOutput.ReadToEnd();
                    // 避免阻塞过久
                    p.WaitForExit(3000);
                    return !string.IsNullOrEmpty(output) && output.IndexOf(encoderName, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /* 裁剪：裁剪按钮事件 */
        private void BtnCut_Click(object sender, RoutedEventArgs e)
        {
            /* 统一走全局处理流程，但明确设置工具类型为 cut */
            sToolChoose = "cut";
            BtnProcess_Click(sender, e);
        }

        /* 裁剪：开始时间输入框事件 */
        private void StartPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isInited && mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                double totalSeconds = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                double currentSeconds = (startPositionSlider.Value / 100.0) * totalSeconds;
                TimeSpan currentPosition = TimeSpan.FromSeconds(currentSeconds);

                // 确保开始时间不会超过结束时间
                if (currentPosition.TotalSeconds < TimeSpan.Parse(cut_end_time.Text).TotalSeconds)
                {
                    cut_start_time.Text = currentPosition.ToString(@"hh\:mm\:ss");
                }
                else
                {
                    startPositionSlider.Value = e.OldValue;
                }
            }
        }

        /* 裁剪：结束时间输入框事件 */
        private void EndPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isInited && mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                double totalSeconds = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                double currentSeconds = (endPositionSlider.Value / 100.0) * totalSeconds;
                TimeSpan currentPosition = TimeSpan.FromSeconds(currentSeconds);

                // 确保结束时间不会小于开始时间
                if (currentPosition.TotalSeconds > TimeSpan.Parse(cut_start_time.Text).TotalSeconds)
                {
                    cut_end_time.Text = currentPosition.ToString(@"hh\:mm\:ss");
                }
                else
                {
                    endPositionSlider.Value = e.OldValue;
                }
            }
        }

        /* 转换: 处理目标视频格式 */
        private void RadioBtn_ConvertChoose_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as RadioButton;
            string videoFormatChoose = button.Tag.ToString();

            if ("mp4" == videoFormatChoose)
            {
                sVideoFormat = $".mp4";
            }
            else if ("avi" == videoFormatChoose)
            {
                sVideoFormat = $".avi";
            }
            else if ("mkv" == videoFormatChoose)
            {
                sVideoFormat = $".mkv";
            }
            else
            {
                sVideoFormat = $".mp4";
            }

            parseConvertVideoCode();
        }

        /* 动图：格式选择（GIF/WebP） */
        private void RadioBtn_GifFormatChoose_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as RadioButton;
            string formatTag = button?.Tag?.ToString()?.ToLower() ?? ".gif";
            if (formatTag == ".webp")
            {
                sAnimatedImageFormat = ".webp";
            }
            else
            {
                sAnimatedImageFormat = ".gif";
            }
        }

        /* GIF: 一些判断处理 */
        private void TimeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // 只允许输入数字和冒号
            e.Handled = !IsTimeInputValid(e.Text);
        }
        private bool IsTimeInputValid(string text)
        {
            return text.All(c => char.IsDigit(c) || c == ':');
        }
        private void ValidateTimeRange()
        {
            /* GIF 文本框校验 */
            if (txtStartTime != null && txtEndTime != null)
            {
                TimeSpan startTime, endTime;
                if (TimeSpan.TryParse(txtStartTime.Text, out startTime) &&
                    TimeSpan.TryParse(txtEndTime.Text, out endTime))
                {
                    if (endTime < startTime)
                    {
                        txtEndTime.Text = txtStartTime.Text;
                    }
                }
            }

            /* 裁剪文本框校验 */
            ValidateCutTimeRange();
        }

        private void ValidateCutTimeRange()
        {
            if (!isInited) return;
            if (cut_start_time == null || cut_end_time == null) return;
            if (mediaPlayer == null) return;

            TimeSpan startTime, endTime;
            if (TimeSpan.TryParse(cut_start_time.Text, out startTime) &&
                TimeSpan.TryParse(cut_end_time.Text, out endTime))
            {
                if (mediaPlayer.NaturalDuration.HasTimeSpan)
                {
                    var duration = mediaPlayer.NaturalDuration.TimeSpan;
                    if (startTime > duration) startTime = duration;
                    if (endTime > duration) endTime = duration;
                }

                if (endTime < startTime)
                {
                    cut_end_time.Text = cut_start_time.Text;
                }
            }
        }

        /* GIF: gif起始位置处理 */
        private void TimeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            // 格式化时间文本
            string text = textBox.Text.Replace(":", "");
            if (text.Length > 6) text = text.Substring(0, 6);

            while (text.Length < 6) text = "0" + text;

            textBox.Text = $"{text.Substring(0, 2)}:{text.Substring(2, 2)}:{text.Substring(4, 2)}";
            textBox.CaretIndex = textBox.Text.Length;

            // 验证开始时间和结束时间
            ValidateTimeRange();

            // 如果是裁剪文本框，联动更新区间滑块
            if (textBox == cut_start_time || textBox == cut_end_time)
            {
                SyncCutSlidersFromTextBoxes();
            }
        }

        /* GIF: 获取起始位置和结束位置相差秒数 */
        private int GetTimeRangeInSeconds()
        {
            TimeSpan startTime, endTime;
            if (TimeSpan.TryParse(txtStartTime.Text, out startTime) &&
                TimeSpan.TryParse(txtEndTime.Text, out endTime))
            {
                return (int)(endTime - startTime).TotalSeconds;
            }
            return 0;
        }

        /* GIF: 时间+1s */
        private void TimeSpinButton_Up_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as RepeatButton;
            var grid = button.Parent as StackPanel;
            var parentGrid = grid.Parent as Grid;
            var textBox = parentGrid.Children[0] as TextBox;

            // 解析当前时间
            TimeSpan currentTime;
            if (TimeSpan.TryParse(textBox.Text, out currentTime))
            {
                currentTime = currentTime.Add(TimeSpan.FromSeconds(1));

                /* 确保时间不会超过视频时长 */
                if (mediaPlayer.NaturalDuration.HasTimeSpan)
                {
                    if (currentTime > mediaPlayer.NaturalDuration.TimeSpan)
                    {
                        currentTime = mediaPlayer.NaturalDuration.TimeSpan;
                    }
                }

                // 确保时间不为负
                if (currentTime.TotalSeconds < 0)
                    currentTime = TimeSpan.Zero;

                // 如果是开始时间，确保不大于结束时间
                if (textBox == txtStartTime || textBox == cut_start_time)
                {
                    TimeSpan endTime;
                    var endText = (textBox == txtStartTime) ? txtEndTime.Text : cut_end_time.Text;
                    if (TimeSpan.TryParse(endText, out endTime) && currentTime > endTime)
                    {
                        currentTime = endTime;
                    }
                }
                // 如果是结束时间，确保不小于开始时间
                else if (textBox == txtEndTime || textBox == cut_end_time)
                {
                    TimeSpan startTime;
                    var startText = (textBox == txtEndTime) ? txtStartTime.Text : cut_start_time.Text;
                    if (TimeSpan.TryParse(startText, out startTime) && currentTime < startTime)
                    {
                        currentTime = startTime;
                    }
                }

                textBox.Text = currentTime.ToString(@"hh\:mm\:ss");

                // 裁剪文本框变更后联动滑块
                if (textBox == cut_start_time || textBox == cut_end_time)
                {
                    SyncCutSlidersFromTextBoxes();
                }
            }
        }

        /* GIF: 时间-1s */
        private void TimeSpinButton_Down_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as RepeatButton;
            var grid = button.Parent as StackPanel;
            var parentGrid = grid.Parent as Grid;
            var textBox = parentGrid.Children[0] as TextBox;

            // 解析当前时间
            TimeSpan currentTime;
            if (TimeSpan.TryParse(textBox.Text, out currentTime))
            {
                // 增加或减少一秒
                currentTime = currentTime.Subtract(TimeSpan.FromSeconds(1));

                // 确保时间不为负
                if (currentTime.TotalSeconds < 0)
                    currentTime = TimeSpan.Zero;

                textBox.Text = currentTime.ToString(@"hh\:mm\:ss");

                // 裁剪文本框变更后联动滑块
                if (textBox == cut_start_time || textBox == cut_end_time)
                {
                    SyncCutSlidersFromTextBoxes();
                }
            }
        }

        /* 裁剪: 根据文本框同步区间滑块 */
        private void SyncCutSlidersFromTextBoxes()
        {
            if (!isInited || !mediaPlayer.NaturalDuration.HasTimeSpan) return;

            TimeSpan startTime, endTime;
            if (TimeSpan.TryParse(cut_start_time.Text, out startTime) &&
                TimeSpan.TryParse(cut_end_time.Text, out endTime))
            {
                var duration = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                var startSeconds = Math.Max(0, Math.Min(duration, startTime.TotalSeconds));
                var endSeconds = Math.Max(0, Math.Min(duration, endTime.TotalSeconds));

                // 保证单调
                if (endSeconds < startSeconds)
                {
                    endSeconds = startSeconds;
                    cut_end_time.Text = TimeSpan.FromSeconds(endSeconds).ToString(@"hh\:mm\:ss");
                }

                startPositionSlider.Value = (startSeconds / duration) * 100.0;
                endPositionSlider.Value = (endSeconds / duration) * 100.0;
            }
        }

        /* 裁剪: 获取区间秒数 */
        private int GetCutTimeRangeInSeconds()
        {
            TimeSpan startTime, endTime;
            if (TimeSpan.TryParse(cut_start_time.Text, out startTime) &&
                TimeSpan.TryParse(cut_end_time.Text, out endTime))
            {
                var seconds = (int)(endTime - startTime).TotalSeconds;
                return Math.Max(0, seconds);
            }
            return 0;
        }

        /* GIF: gif处理界面的进度条事件 */
        private void GifSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isInited)
            {
                /* 动图帧率不能写到压缩用的 sCrf，否则后续压缩会误用该值 */
                textBox_Gif_Fps.Text = Math.Round(sliderGifFps.Value).ToString(CultureInfo.InvariantCulture);
            }
        }

        /* GIF: 重设gif大小 */
        private void BtnGifResize_Click(object sender, RoutedEventArgs e)
        {
            if (!isFileOpened) {
                return;
            }

            // 获取视频的宽度和高度
            int _originalWidth = mediaPlayer.NaturalVideoWidth;
            int _originalHeight = mediaPlayer.NaturalVideoHeight;
            int targetHeight = 0;

            // 获取触发事件的按钮
            if (sender is Button button)
            {
                /* 根据按钮的 Tag 属性区分 */
                var tag = button.Tag?.ToString();
                switch (tag)
                {
                    case "320":
                        targetHeight = 320;
                        break;
                    case "480":
                        targetHeight = 480;
                        break;
                    case "720":
                        targetHeight = 720;
                        break;
                    case "1080":
                        targetHeight = 1080;
                        break;
                    case "Orig":
                        targetHeight = _originalHeight;
                        break;
                    default:
                        targetHeight = _originalHeight;
                        break;
                }

                /* 判断原始高度是否大于目标高度 */
                if (_originalHeight < targetHeight && tag != "Original") {
                    return;
                }

                /* 计算等比例宽度 */
                int newWidth = (int)(_originalWidth * (targetHeight / (double)_originalHeight));

                /* 更新界面 */
                textBox_Gif_Width.Text = newWidth.ToString();
                textBox_Gif_Height.Text = targetHeight.ToString();
            }
        }

        /* 加速: 视频加速界面进度条事件 */
        private void SliderMultiple_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isInited)
            {
                /* 倍率不能写到压缩用的 sCrf，否则后续压缩会误用该值 */
                textBoxMultiple.Text = sliderMultiple.Value.ToString("F1", CultureInfo.InvariantCulture);
            }
        }

        /* 尺寸: 重设视频大小 */
        private void BtnSizeResize_Click(object sender, RoutedEventArgs e)
        {
            if (!isFileOpened)
            {
                return;
            }

            // 获取视频的宽度和高度
            int _originalWidth = mediaPlayer.NaturalVideoWidth;
            int _originalHeight = mediaPlayer.NaturalVideoHeight;
            int targetHeight = 0;

            // 获取触发事件的按钮
            if (sender is Button button)
            {
                /* 根据按钮的 Tag 属性区分 */
                var tag = button.Tag?.ToString();
                switch (tag)
                {
                    case "320":
                        targetHeight = 320;
                        break;
                    case "480":
                        targetHeight = 480;
                        break;
                    case "720":
                        targetHeight = 720;
                        break;
                    case "1080":
                        targetHeight = 1080;
                        break;
                    case "Orig":
                        targetHeight = _originalHeight;
                        break;
                    default:
                        targetHeight = _originalHeight;
                        break;
                }

                /* 判断原始高度是否大于目标高度 */
                if (_originalHeight < targetHeight && tag != "Original")
                {
                    return;
                }

                /* 计算等比例宽度 */
                int newWidth = (int)(_originalWidth * (targetHeight / (double)_originalHeight));

                /* 更新界面 */
                textBox_Size_Width.Text = newWidth.ToString();
                textBox_Size_Height.Text = targetHeight.ToString();
            }
        }

        /* 尺寸: 重设视频大小处理函数 */
        private async void BtnSizeConvert_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(sVideoFilePath))
            {
                MessageBox.Show("请先导入视频！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string sCmd = "";
            string formattedTime = DateTime.Now.ToString("yyMMddHHmmss");
            string suffix = "_resize_" + textBox_Size_Width.Text.ToString() + "x" + textBox_Size_Height.Text.ToString() + "_" + formattedTime;
            string fileName = IOPath.GetFileNameWithoutExtension(sVideoFilePath);
            string extension = IOPath.GetExtension(sVideoFilePath); ;
            string newFileName = $"{fileName}{suffix}{extension}";
            string targetFolder = textBoxVideoDir.Text;

            Directory.CreateDirectory(targetFolder);

            string destinationPath = IOPath.Combine(targetFolder, newFileName);
            int timeRangeInSeconds = GetTimeRangeInSeconds();

            sCmd += $"-i ";
            sCmd += sVideoFilePath;
            sCmd += $" -vf \"scale=";
            sCmd += textBox_Size_Width.Text.ToString();
            sCmd += ":";
            sCmd += textBox_Size_Height.Text.ToString();
            sCmd += "\" ";
            sCmd += destinationPath;

            try
            {
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                /* 显示处理遮罩 */
                ProcessingOverlay.Visibility = Visibility.Visible;
                progressBarOverlayRunning.Value = 1;
                _cancellationTokenSource = new CancellationTokenSource();

                /* 异步执行任务 */
                await Task.Run(() =>
                {
                    //MessageBox.Show(sCmd, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    ffmpegProcess(_cancellationTokenSource.Token, sCmd);

                    /* 只有在没有取消的情况下才显示完成消息 */
                    if (!_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        if (File.Exists(destinationPath))
                        {
                            /* 启动资源管理器进程 */
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = targetFolder,
                                UseShellExecute = false
                            });
                        }
                    }
                    else
                    {
                        /* 如果用户取消，删除生成的文件 */
                        if (File.Exists(destinationPath))
                        {
                            File.Delete(destinationPath);
                        }
                    }
                }
                );
            }
            catch (OperationCanceledException)
            {
                /* 用户取消了操作 */
                ProcessingOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"处理出错：{ex.Message}");
            }
            finally
            {
                ProcessingOverlay.Visibility = Visibility.Collapsed;
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
            }
        }

        /* 设置: 设置界面中选择ffmpeg程序路径的按钮事件 */
        private void BtnSettingChoose_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "ffmpeg可执行程序|*.exe|所有文件|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                sFFmpegFilePath = openFileDialog.FileName;
                textBoxSettingFmmpegPath.Text = openFileDialog.FileName;

                //var iniConfig = new IniConfig();
                //iniConfig.Write("Program", "FFmpegPath", sFFmpegFilePath);
            }
        }

        /* 设置: 设置界面中在线下载ffmpeg程序路径的按钮事件 */
        private async void BtnSettingDownload_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                FileName = "选择保存目录",
                Title = "选择ffmpeg程序的保存路径",
                CheckFileExists = false,
                CheckPathExists = true,
                ValidateNames = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string sFFmpegSaveFolder = IOPath.GetDirectoryName(openFileDialog.FileName);
                string fileUrl = "https://gitee.com/is-zhou/ffmpeg/releases/download/7.1.1/ffmpeg.exe";
                string fileName = IOPath.GetFileName(fileUrl);
                string filePath = IOPath.Combine(sFFmpegSaveFolder, fileName);

                /* 目录下存在相同文件，不下载 */
                if (File.Exists(filePath))
                {
                    sFFmpegFilePath = filePath;
                    textBoxSettingFmmpegPath.Text = filePath;

                    var iniConfig = new IniConfig();
                    iniConfig.Write("Program", "FFmpegPath", filePath);

                    MessageBox.Show("已经存在FFmpeg，跳过下载！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                try
                {
                    if (_cancellationTokenSource != null)
                    {
                        _cancellationTokenSource.Dispose();
                    }
                    _cancellationTokenSource = new CancellationTokenSource();

                    ProcessingOverlay.Visibility = Visibility.Visible;
                    progressBarOverlayRunning.Value = 1;
                    textBoxOverlayRunning.Text = $"下载中，请稍等···";

                    using (var httpClient = new HttpClient())
                    {
                        using (var response = await httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();

                            long? totalBytes = response.Content.Headers.ContentLength;
                            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            {
                                byte[] buffer = new byte[8192];
                                long totalBytesRead = 0;
                                int bytesRead;

                                while ((bytesRead = await contentStream.ReadAsync(
                                    buffer, 0, buffer.Length, _cancellationTokenSource.Token)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;

                                    if (totalBytes.HasValue)
                                    {
                                        double progress = (double)totalBytesRead / totalBytes.Value * 100;
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            progressBarOverlayRunning.Value = progress;
                                        });
                                    }
                                }
                            }
                        }
                    }

                    // 下载成功后更新配置
                    sFFmpegFilePath = filePath;
                    textBoxSettingFmmpegPath.Text = filePath;

                    var iniConfig = new IniConfig();
                    iniConfig.Write("Program", "FFmpegPath", filePath);

                    MessageBox.Show("FFmpeg下载成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (OperationCanceledException)
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"下载出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    ProcessingOverlay.Visibility = Visibility.Collapsed;
                    textBoxOverlayRunning.Text = $"处理中，请稍等···";
                    if (_cancellationTokenSource != null)
                    {
                        _cancellationTokenSource.Dispose();
                        _cancellationTokenSource = null;
                    }
                }
            }
        }

        /* 设置: 设置界面中选择视频保存目录的按钮事件 */
        private void BtnSettingChooseDir_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                // 设置为只选择目录
                Filter = "文件夹|*.none",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "选择文件夹"
            };

            // 显示对话框并检查用户是否点击了“确定”
            if (openFileDialog.ShowDialog() == true)
            {
                // 获取用户选择的目录路径
                sVideoSaveFolder = System.IO.Path.GetDirectoryName(openFileDialog.FileName);

                // 更新 UI 显示选择的目录
                textBoxVideoDir.Text = sVideoSaveFolder;
            }
        }

        /* 设置: 设置界面中gpu启用事件 */
        private void CheckBoxGpu_Click(object sender, RoutedEventArgs e)
        {
            var checkBox = sender as CheckBox;
            if (checkBox != null)
            {
                if (checkBox.IsChecked == true)
                {
                    comboBox_Setting_GpuSelect.Visibility = Visibility.Visible;
                }
                else
                {
                    comboBox_Setting_GpuSelect.Visibility = Visibility.Collapsed;
                }
            }
        }

        /* 设置: 设置中保存的按钮事件 */
        private void BtnSettingSave_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(sFFmpegFilePath) && isFFmpegExist())
            {
                var iniConfig = new IniConfig();
                iniConfig.Write("Program", "FFmpegPath", sFFmpegFilePath);
                labelSettingInfo.Foreground = Brushes.Green;

                if (sVideoSaveFolder != Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
                {
                    iniConfig.Write("Program", "VideoSaveFloder", sVideoSaveFolder);
                    labelSettingInfo.Foreground = Brushes.Green;
                }

                iniConfig.Write("Program", "GpuUse", (true == checkBox_Setting_GpuUse.IsChecked) ? "enable" : "disable");
                iniConfig.Write("Program", "GpuSelect", comboBox_Setting_GpuSelect.SelectedIndex.ToString());
                iniConfig.Write("Program", "CpuMax", (true == checkBox_Setting_CpuMax.IsChecked) ? "enable" : "disable");

                labelSettingInfo.Content = $"保存成功！";

                labelClearTimer.Stop(); // 停止之前的计时器(如果在运行)
                labelClearTimer.Start(); // 开始新的2秒计时
            }
            else
            {
                MessageBox.Show($"请选择FFmpeg程序路径！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /* 全局: 所有的处理任务按钮事件 */
        private async void BtnProcess_Click(object sender, RoutedEventArgs e)
        {
            if (!isBatchMode)
            {
                if (string.IsNullOrEmpty(sVideoFilePath))
                {
                    MessageBox.Show("请先导入视频！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                if (batchFiles.Count == 0)
                {
                    MessageBox.Show("请先添加要批量处理的文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            string targetFolder = "", destinationPath = "";
            string sCmd = "";
            if (!isBatchMode)
            {
                sCmd = makeFfmpegCmd(out targetFolder, out destinationPath);

                if (string.IsNullOrEmpty(sCmd))
                {
                    MessageBox.Show("创建任务失败！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            //MessageBox.Show($"{sToolChoose} = {sCmd}", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            //return;

            try
            {
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                /* 显示处理遮罩 */
                ProcessingOverlay.Visibility = Visibility.Visible;
                progressBarOverlayRunning.Value = 1;
                _cancellationTokenSource = new CancellationTokenSource();

                /* 异步执行任务 */
                await Task.Run(() =>
                {
                    if (!isBatchMode)
                    {
                        ffmpegProcess(_cancellationTokenSource.Token, sCmd);

                        if (!_cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            if (File.Exists(destinationPath))
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "explorer.exe",
                                    Arguments = targetFolder,
                                    UseShellExecute = false
                                });
                            }
                        }
                        else
                        {
                            if (File.Exists(destinationPath))
                            {
                                File.Delete(destinationPath);
                            }
                        }
                    }
                    else
                    {
                        int total = batchFiles.Count;
                        string lastFolder = "";
                        for (int i = 0; i < total; i++)
                        {
                            if (_cancellationTokenSource.Token.IsCancellationRequested) break;
                            var item = batchFiles[i];

                            string cmdLocal = null;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                // 在 UI 线程更新界面并生成命令，避免跨线程访问控件
                                sVideoFilePath = item.FilePath;
                                textBoxOverlayRunning.Text = $"正在处理 {i + 1}/{total}：{item.FileName}";
                                progressBarOverlayRunning.Value = 1;

                                string tf2 = "", dp2 = "";
                                cmdLocal = makeFfmpegCmd(out tf2, out dp2);
                                lastFolder = tf2;
                            });

                            // 在后台线程执行 ffmpeg 处理
                            ffmpegProcess(_cancellationTokenSource.Token, cmdLocal);
                        }

                        if (!_cancellationTokenSource.Token.IsCancellationRequested && !string.IsNullOrEmpty(lastFolder))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = lastFolder,
                                UseShellExecute = false
                            });
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
                /* 用户取消了操作 */
                ProcessingOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"处理出错：{ex.Message}");
            }
            finally
            {
                ProcessingOverlay.Visibility = Visibility.Collapsed;
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
            }
        }

        /* 全局: 任务处理中遮罩上的取消正在处理的任务按钮事件 */
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // 忽略已释放的异常
            }
        }

        /* 批量模式：列表拖拽导入事件 */
        private void BatchListView_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void BatchListView_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                AddBatchFiles(files);
            }
        }

        /* 批量模式：选择多个文件 */
        private void BtnBatchOpenFiles_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.avi;*.wmv;*.mov;*.mkv;*.webm;*.flv;*.ts;*.m2ts;*.mts;*.mpg;*.mpeg;*.m4v;*.3gp;*.3g2;*.ogv;*.vob;*.asf;*.mxf;*.rmvb;*.rm;*.y4m|所有文件|*.*",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                AddBatchFiles(openFileDialog.FileNames);
            }
        }

        /* 批量模式：清空当前列表 */
        private void BtnBatchClearFiles_Click(object sender, RoutedEventArgs e)
        {
            batchFiles.Clear();
        }

        /* 批量模式：右键移除某个文件 */
        private void MenuItemRemoveFromBatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi)
            {
                var item = mi.CommandParameter as BatchFileItem;
                if (item != null)
                {
                    batchFiles.Remove(item);
                }
            }
        }

        /* 关于: 关于按钮事件 */
        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }
    }
}
