﻿using System;
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

using IOPath = System.IO.Path;
using System.Threading;
using System.Windows.Controls.Primitives;

namespace VideoTools
{
    /* 软件配置文件解析 */
    public class IniConfig
    {
        private readonly string _filePath;

        public IniConfig()
        {
            /* 获取用户目录路径，C:\Users\xxxxx\AppData\Roaming */
            var userDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            /* 定义配置文件的路径 */
            var appName = ".VideoTools_250405"; /* 使用这种特殊的目录避免程序名和其他程序冲突 */
            var appDataPath = IOPath.Combine(userDir, appName);
            _filePath = IOPath.Combine(appDataPath, "config.ini");

            /* 如果目录不存在，则创建 */
            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }

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
        private string sToolChoose = "compress";
        private CancellationTokenSource _cancellationTokenSource; /* 全局的取消事件 */
        private double baseBitrate = 0.0;
        private int maxThreads = 0;

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
            string ext = IOPath.GetExtension(filePath).ToLower();
            return new[] { ".mp4", ".avi", ".wmv", ".mov", ".mkv" }.Contains(ext);
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

        /* 视频窗口: 拖放文件处理 */
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    PlayVideo(files[0]);
                    sVideoFilePath = files[0];
                }
            }
        }

        /* 视频窗口: 按钮点击打开文件 */
        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.avi;*.wmv;*.mov;*.mkv|所有文件|*.*"
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

        /* 视频窗口: 计算基准比特率 */
        private double CalculateBaseBitrate(double height)
        {
            // 根据高度计算基准比特率
            if (height <= 320) return 0.75; // 320p 或更低
            if (height <= 480) return 1.5;  // 480p
            if (height <= 720) return 3.0;  // 720p
            if (height <= 1080) return 5.0; // 1080p
            if (height <= 1440) return 10.0; // 1440p
            if (height <= 2160) return 20.0; // 2160p（4K）
            return 20.0; // 高于 4K
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
                baseBitrate = CalculateBaseBitrate(videoHeight);
                progressSlider.Maximum = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                uiTimer.Start();
            }
        }

        /* 视频窗口: 视频加载失败 */
        private void MediaElement_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            MessageBox.Show($"视频加载失败: {e.ErrorException.Message}");
            // 恢复叠加层显示
            overlayGrid.Visibility = Visibility.Visible;
            sVideoFilePath = "";
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
            string panelName = button.Tag.ToString() + "Panel";
            sToolChoose = button.Tag.ToString();

            if ("setting" != sToolChoose)
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
        public Int32 ffmpegProcess(CancellationToken cancellationToken, string sCmd)
        {

            if (!isFFmpegExist())
            {
                return -1;
            }

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
            }
            finally
            {
                ffmpeg.Dispose();
            }

            return 0;
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

            if (true == checkBox_Setting_GpuUse.IsChecked) {
                switch (comboBox_Setting_GpuSelect.SelectedIndex) {
                    case 0:
                        sCmd += " -hwaccel qsv ";
                        break;
                    case 1:
                        sCmd += " -hwaccel cuda ";
                        break;
                    case 2:
                        sCmd += " -hwaccel amf ";
                        break;
                    default:
                        sCmd += " -hwaccel qsv ";
                        break;
                }
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

            sCmd += $"-i ";
            sCmd += sVideoFilePath;

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
                    suffix = "_gif_" + formattedTime;
                    extension = $".gif";
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

            /* 根据是否启用GPU加速设置码率，设置为推荐码率的40% */
            if (true == checkBox_Setting_GpuUse.IsChecked)
            {
                sCompressFormat = $" -b:v ";
                double targetBitrate = baseBitrate * Math.Pow(2, (25 - double.Parse(sCrf)) / 6.0) * 0.4;
                sCompressFormat += targetBitrate.ToString("F2");
                sCompressFormat += "M";
            }
            else
            {
                sCompressFormat = $" -crf ";
                sCompressFormat += sCrf;
            }

            switch (sToolChoose)
            {
                case "compress":
                    sCmd += sOutVideoCode;
                    sCmd += sCompressFormat; /* crf or bv */
                    sCmd += " -preset medium -c:a copy ";
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
                    sCmd += $" -vf \"fps=";
                    sCmd += textBox_Gif_Fps.Text.ToString();
                    sCmd += $",scale=";
                    sCmd += textBox_Gif_Width.Text.ToString();
                    if (true == checkBox_Gif_EnablePalettegen.IsChecked)
                    {
                        sCmd += $":-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse=dither=none\" -an -loop ";
                    }
                    else
                    {
                        sCmd += $":-1:flags=lanczos\" -an -loop ";
                    }
                    sCmd += (true == checkBox_Gif_Loop.IsChecked) ? "0" : "1";
                    break;
                case "resize":
                    sCmd += $" -vf \"scale=";
                    sCmd += textBox_Size_Width.Text.ToString();
                    sCmd += ":";
                    sCmd += textBox_Size_Height.Text.ToString();
                    sCmd += "\" ";
                    break;
                case "multiple":
                    sCmd += $" -vf \"setpts=PTS";
                    if (double.TryParse(textBoxMultiple.Text, out double value))
                    {
                        // 检查范围并生成对应的操作符和数值
                        string result = "";
                        if (value >= 0.1 && value <= 1)
                        {
                            double multiplier = 1 / value; // 计算乘法因子
                            result = $"*{multiplier:F1}"; // 保留两位小数
                        }
                        else if (value >= 1.1 && value <= 10)
                        {
                            result = $"/{value:F1}"; // 保留两位小数
                        }
                        else
                        {
                            result = "*1";
                        }

                        sCmd += result;
                    }
                    sCmd += "\" -an ";
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

            /* 输出文件路径 */
            sCmd += $" ";
            sCmd += destinationPath;

            sOutDir = targetFolder;
            sOutFilePath = destinationPath;

            return sCmd;
        }

        /* 压缩: 压缩中进度条事件 */
        private void CompressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isInited)
            {
                sCrf = Math.Round(sliderCompressCrf.Value).ToString();
                labelCompressCrf.Content = sCrf;
            }
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
                        sConvertVideoCode = $" -c:v av1_{sCodeGpuSelect} -c:a libmp3lame ";
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
            if (txtStartTime == null || txtEndTime == null) return;

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
                if (textBox == txtStartTime)
                {
                    TimeSpan endTime;
                    if (TimeSpan.TryParse(txtEndTime.Text, out endTime) && currentTime > endTime)
                    {
                        currentTime = endTime;
                    }
                }
                // 如果是结束时间，确保不小于开始时间
                else if (textBox == txtEndTime)
                {
                    TimeSpan startTime;
                    if (TimeSpan.TryParse(txtStartTime.Text, out startTime) && currentTime < startTime)
                    {
                        currentTime = startTime;
                    }
                }

                textBox.Text = currentTime.ToString(@"hh\:mm\:ss");
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
            }
        }

        /* GIF: gif处理界面的进度条事件 */
        private void GifSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isInited)
            {
                sCrf = Math.Round(sliderGifFps.Value).ToString();
                textBox_Gif_Fps.Text = sCrf;
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
                sCrf = sliderMultiple.Value.ToString("F1");
                textBoxMultiple.Text = sCrf;
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
            if (string.IsNullOrEmpty(sVideoFilePath))
            {
                MessageBox.Show("请先导入视频！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetFolder = "", destinationPath = "";
            string sCmd = makeFfmpegCmd(out targetFolder, out destinationPath);

            if (string.IsNullOrEmpty(sCmd))
            {
                MessageBox.Show("创建任务失败！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
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

        /* 关于: 关于按钮事件 */
        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }
    }
}
