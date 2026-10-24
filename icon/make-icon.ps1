param(
    [string]$Source = (Join-Path $PSScriptRoot 'artwork.png'),
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'PropriaClick.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Builds the icon from the reference artwork: the picture is trimmed to its visible pixels
// and every size is rasterised from that full resolution crop, so no size is a stretched
// copy of another and nothing is lost to a single hard downscale.
public static class IconPainter
{
    public sealed class Art
    {
        public Bitmap Crop;
        public int Width;
        public int Height;
    }

    public static Art Load(string path)
    {
        Bitmap src = new Bitmap(path);
        Bitmap page = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(page)) g.DrawImageUnscaled(src, 0, 0);
        src.Dispose();

        int w = page.Width;
        int h = page.Height;
        BitmapData data = page.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] px = new byte[data.Stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
        int stride = data.Stride;
        page.UnlockBits(data);
        page.Dispose();

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                if (px[i + 3] == 0) continue;
                int l = (px[i + 2] * 299 + px[i + 1] * 587 + px[i + 0] * 114) / 1000;
                if (l >= 200) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) throw new InvalidOperationException("artwork is empty");
        minX = Math.Max(0, minX - 1);
        minY = Math.Max(0, minY - 1);
        maxX = Math.Min(w - 1, maxX + 1);
        maxY = Math.Min(h - 1, maxY + 1);

        Art art = new Art();
        art.Width = maxX - minX + 1;
        art.Height = maxY - minY + 1;
        art.Crop = new Bitmap(art.Width, art.Height, PixelFormat.Format32bppArgb);
        BitmapData crop = art.Crop.LockBits(new Rectangle(0, 0, art.Width, art.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[] dst = new byte[crop.Stride * art.Height];
        for (int y = 0; y < art.Height; y++)
        {
            Array.Copy(px, (minY + y) * stride + minX * 4, dst, y * crop.Stride, art.Width * 4);
        }
        System.Runtime.InteropServices.Marshal.Copy(dst, 0, crop.Scan0, dst.Length);
        art.Crop.UnlockBits(crop);
        FillCard(art.Crop);
        return art;
    }

    // Paints the inside of the frame white: line art alone disappears on a dark taskbar, while a
    // white card reads on any background. The frame is the row and column set that spans almost
    // the whole artwork, so the rule needs no knowledge of what is drawn; artwork without such a
    // frame is left untouched.
    static void FillCard(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        byte[] px = new byte[data.Stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
        int stride = data.Stride;
        bmp.UnlockBits(data);

        int top = -1, bottom = -1;
        for (int y = 0; y < h; y++)
        {
            int n = 0;
            for (int x = 0; x < w; x++)
            {
                if (px[y * stride + x * 4 + 3] >= 128) n++;
            }
            if (n >= w * 0.8)
            {
                if (top < 0) top = y;
                bottom = y;
            }
        }
        if (top < 0 || bottom - top < 8) return;

        int left = -1, right = -1;
        for (int x = 0; x < w; x++)
        {
            int n = 0;
            for (int y = top; y <= bottom; y++)
            {
                if (px[y * stride + x * 4 + 3] >= 128) n++;
            }
            if (n >= (bottom - top + 1) * 0.8)
            {
                if (left < 0) left = x;
                right = x;
            }
        }
        if (left < 0 || right - left < 8) return;

        bool changed = false;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                int o = y * stride + x * 4;
                int a = px[o + 3];
                if (a == 255) continue;
                px[o + 0] = (byte)((px[o + 0] * a + 255 * (255 - a)) / 255);
                px[o + 1] = (byte)((px[o + 1] * a + 255 * (255 - a)) / 255);
                px[o + 2] = (byte)((px[o + 2] * a + 255 * (255 - a)) / 255);
                px[o + 3] = 255;
                changed = true;
            }
        }
        if (!changed) return;

        BitmapData back = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(px, 0, back.Scan0, px.Length);
        }
        finally
        {
            bmp.UnlockBits(back);
        }
    }


    public static Bitmap Render(Art art, int size)
    {
        // Render at four times the target and average the result down: a plain downscale of
        // the artwork already looks soft at 16 and 20 pixels.
        const int ss = 4;
        int canvas = size * ss;
        double margin = Math.Max(1.0, size * 0.02);
        double box = size - margin * 2;
        double scale = box / Math.Max(art.Width, art.Height);

        Bitmap big = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(big))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            double dw = art.Width * scale * ss;
            double dh = art.Height * scale * ss;
            g.DrawImage(art.Crop, (float)((canvas - dw) / 2), (float)((canvas - dh) / 2), (float)dw, (float)dh);
        }

        BitmapData data = big.LockBits(new Rectangle(0, 0, canvas, canvas), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        BitmapData target = result.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] src = new byte[stride * canvas];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, src, 0, src.Length);

            // Averaging turns the one pixel frame and the letter strokes grey, so the small
            // sizes get their coverage pushed back.
            double alphaGamma = size < 24 ? 0.68 : size < 40 ? 0.82 : 1.0;
            double inkGain = size < 24 ? 1.7 : size < 32 ? 1.5 : size < 40 ? 1.3 : 1.0;

            byte[] dst = new byte[target.Stride * size];
            int n = ss * ss;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double sumA = 0, sumR = 0, sumG = 0, sumB = 0;
                    for (int sy = 0; sy < ss; sy++)
                    {
                        int row = (y * ss + sy) * stride;
                        for (int sx = 0; sx < ss; sx++)
                        {
                            int i = row + (x * ss + sx) * 4;
                            double a = src[i + 3];
                            sumA += a;
                            sumR += src[i + 2] * a;
                            sumG += src[i + 1] * a;
                            sumB += src[i + 0] * a;
                        }
                    }
                    double alpha = sumA / n;
                    double r = alpha > 0 ? sumR / sumA : 0;
                    double g = alpha > 0 ? sumG / sumA : 0;
                    double b = alpha > 0 ? sumB / sumA : 0;

                    if (inkGain > 1.0)
                    {
                        r = Contrast(r, inkGain);
                        g = Contrast(g, inkGain);
                        b = Contrast(b, inkGain);
                    }
                    if (alphaGamma < 1.0 && alpha > 0)
                    {
                        alpha = Math.Pow(alpha / 255.0, alphaGamma) * 255.0;
                    }

                    int o = y * target.Stride + x * 4;
                    dst[o + 0] = (byte)Math.Round(b);
                    dst[o + 1] = (byte)Math.Round(g);
                    dst[o + 2] = (byte)Math.Round(r);
                    dst[o + 3] = (byte)Math.Round(alpha);
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(dst, 0, target.Scan0, dst.Length);
        }
        finally
        {
            big.UnlockBits(data);
            result.UnlockBits(target);
            big.Dispose();
        }
        return result;
    }

    // Push ink towards black while leaving paper alone: the averaged strokes are already a
    // light grey by the time a 16 pixel icon is built.
    static double Contrast(double value, double gain)
    {
        double v = 255.0 - (255.0 - value) * gain;
        return v < 0 ? 0 : v > 255 ? 255 : v;
    }

    public static void SaveIco(Bitmap[] images, int[] sizes, string path)
    {
        byte[][] data = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) data[i] = Dib(images[i]);
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(data[i].Length);
                bw.Write(offset);
                offset += data[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++) bw.Write(data[i]);
        }
    }

    // Icon entries are stored as bottom-up 32bpp DIBs; Windows and System.Drawing both need
    // the bitmap form, PNG compressed entries would be rejected here.
    static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0);
            bw.Write(w * h * 4);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            byte[] row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                IntPtr src = (IntPtr)(data.Scan0.ToInt64() + y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(src, row, 0, w * 4);
                bw.Write(row);
            }
            byte[] zero = new byte[((w + 31) / 32) * 4];
            for (int y = 0; y < h; y++) bw.Write(zero);
            bw.Flush();
            return ms.ToArray();
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing -ErrorAction Stop

$art = [IconPainter]::Load($Source)
Write-Host "artwork: $($art.Width)x$($art.Height)"

$sizes = [int[]](16, 20, 24, 32, 48, 64, 128, 256)
$bitmaps = @()
foreach ($s in $sizes) { $bitmaps += , ([IconPainter]::Render($art, $s)) }

[IconPainter]::SaveIco([Drawing.Bitmap[]]$bitmaps, $sizes, $Out)
Write-Host "wrote $Out ($((Get-Item $Out).Length) bytes, $($sizes.Count) sizes)"

foreach ($b in $bitmaps) { $b.Dispose() }
$art.Crop.Dispose()
