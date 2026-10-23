using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace McServerList.App.Services;

/// <summary>
/// 从皮肤图里裁出头部（启动页头像与"切换账号"窗口共用）。
/// 取 (8,8) 起的 8x8 基础层，并把 (40,8) 起的帽子层叠加上去，得到和游戏里一样的头像
/// （64x64 / 128x128 皮肤都支持）。取不到就返回 null，由调用方保留首字母占位。
/// </summary>
internal static class SkinHead
{
    public static ImageSource FromSkin(byte[] skinPng)
    {
        using var stream = new MemoryStream(skinPng);
        var full = new BitmapImage();
        full.BeginInit();
        full.CacheOption = BitmapCacheOption.OnLoad;
        full.StreamSource = stream;
        full.EndInit();
        full.Freeze();

        var converted = new FormatConvertedBitmap(full, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var unit = Math.Max(1, converted.PixelWidth / 64); // 64x64 → 1，128x128 → 2
        var head = SampleRegion(converted, 8 * unit, 8 * unit, unit);

        // 帽子层（第二层）叠在头部上面；没有这块（老皮肤/尺寸异常）就只用基础层
        if (converted.PixelWidth >= 48 * unit && converted.PixelHeight >= 16 * unit)
        {
            var hat = SampleRegion(converted, 40 * unit, 8 * unit, unit);
            for (var i = 0; i < head.Length; i += 4)
            {
                var alpha = hat[i + 3] / 255.0;
                if (alpha <= 0)
                {
                    continue;
                }

                for (var c = 0; c < 3; c++)
                {
                    head[i + c] = (byte)Math.Round(hat[i + c] * alpha + head[i + c] * (1 - alpha));
                }

                head[i + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(8, 8, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, 8, 8), head, 8 * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>把皮肤图上的一块 8x8 像素（每像素 <paramref name="unit"/>×<paramref name="unit"/>）取出来。</summary>
    private static byte[] SampleRegion(BitmapSource source, int x, int y, int unit)
    {
        var width = 8 * unit;
        var buffer = new byte[width * width * 4];
        source.CopyPixels(new Int32Rect(x, y, width, width), buffer, width * 4, 0);

        if (unit == 1)
        {
            return buffer;
        }

        var result = new byte[8 * 8 * 4];
        for (var py = 0; py < 8; py++)
        {
            for (var px = 0; px < 8; px++)
            {
                var sourceIndex = ((py * unit) * width + px * unit) * 4;
                var targetIndex = (py * 8 + px) * 4;
                Array.Copy(buffer, sourceIndex, result, targetIndex, 4);
            }
        }

        return result;
    }
}
