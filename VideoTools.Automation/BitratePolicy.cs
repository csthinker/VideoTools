using System;

namespace VideoTools.Automation
{
    /// <summary>
    /// CRF → 硬件编码目标码率的统一换算规则。
    /// 桌面界面与自动化（CLI / MCP）必须共用同一套规则，否则同一 CRF 会得到不同的成品大小。
    /// </summary>
    public static class BitratePolicy
    {
        /// <summary>
        /// 按画面“短边”取基准码率（Mbps）：竖屏 1080x1920 与横屏 1920x1080 视为同一档。
        /// </summary>
        public static double BaseMbps(int width, int height)
        {
            var shortSide = width > 0 && height > 0 ? Math.Min(width, height) : Math.Max(width, height);
            if (shortSide <= 0) return 5.0;      /* 未知尺寸时按 1080p 处理，避免 0M 无法编码 */
            if (shortSide <= 320) return 0.75;
            if (shortSide <= 480) return 1.5;
            if (shortSide <= 720) return 3.0;
            if (shortSide <= 1080) return 5.0;
            if (shortSide <= 1440) return 10.0;
            if (shortSide <= 2160) return 20.0;
            return 20.0;
        }

        /// <summary>
        /// CRF 换算成目标码率（Mbps），规则与桌面端一致：基准 × 2^((25-CRF)/6) × 0.4。
        /// </summary>
        public static double TargetMbps(int width, int height, int crf)
        {
            if (crf < 0) crf = 0;
            if (crf > 51) crf = 51;
            return BaseMbps(width, height) * Math.Pow(2, (25 - crf) / 6.0) * 0.4;
        }

        /// <summary>
        /// 目标码率不得超过源码率（留 10% 余量），避免“压缩后比原文件还大”。
        /// 源文件码率未知（≤0）时不封顶。
        /// </summary>
        public static double CapToSource(double targetMbps, int sourceBitrateKbps)
        {
            if (sourceBitrateKbps <= 0 || targetMbps <= 0) return targetMbps;
            var sourceMbps = sourceBitrateKbps / 1000.0;
            return Math.Min(targetMbps, sourceMbps * 0.9);
        }
    }
}
