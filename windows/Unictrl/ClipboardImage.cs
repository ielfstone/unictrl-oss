using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace Unictrl;

/// Bounded PNG and common 24/32-bit desktop DIB conversion, on the clipboard STA only.
internal static class ClipboardImage
{
    public static Bitmap Decode(byte[] png)
    {
        var size = ClipboardReplica.PngSize(png) ?? throw new InvalidDataException("PNG dimensions or size");
        using var stream = new MemoryStream(png, false);
        using var image = Image.FromStream(stream, false, true);
        if (image.Width != size.Width || image.Height != size.Height || image.FrameDimensionsList.Any(d => image.GetFrameCount(new FrameDimension(d)) != 1))
            throw new InvalidDataException("Single image required");
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(image, 0, 0);
        return bitmap;
    }
    public static byte[] Encode(Bitmap bitmap)
    {
        if (!ClipboardReplica.ValidSize(bitmap.Width, bitmap.Height)) throw new InvalidDataException("Image dimensions");
        using var output = new MemoryStream(); bitmap.Save(output, ImageFormat.Png);
        if (output.Length > ClipboardReplica.MaxImageBytes) throw new InvalidDataException("PNG exceeds 10 MiB");
        return output.ToArray();
    }
    public static byte[] FromDib(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("DIB header");
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(at));
        var header = I(0); var width = I(4); var signedHeight = I(8);
        var height = signedHeight == int.MinValue ? 0 : Math.Abs(signedHeight);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)); var compression = I(16);
        if (header is not (40 or 108 or 124) || header > dib.Length || !ClipboardReplica.ValidSize(width, height) ||
            BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(12)) != 1 || bits is not (24 or 32) || compression is not (0 or 3) || (compression == 3 && bits != 32) || I(32) != 0)
            throw new InvalidDataException("Unsupported DIB");
        var offset = header + (header == 40 && compression == 3 ? 12 : 0);
        if (offset > dib.Length) throw new InvalidDataException("DIB masks");
        var alpha = false;
        if (compression == 3)
        {
            if (I(40) != 0x00ff0000 || I(44) != 0x0000ff00 || I(48) != 0x000000ff) throw new InvalidDataException("DIB channel masks");
            alpha = header >= 108 && I(52) == unchecked((int)0xff000000);
            if (header >= 108 && I(52) != 0 && !alpha) throw new InvalidDataException("DIB alpha mask");
        }
        if (header == 124 && (I(112) != 0 || I(116) != 0)) throw new InvalidDataException("Embedded DIB profiles unsupported");
        var stride = ((width * bits + 31) / 32) * 4;
        if ((long)offset + (long)stride * height > dib.Length) throw new InvalidDataException("Truncated DIB");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                var at = offset + (signedHeight < 0 ? y : height - 1 - y) * stride;
                for (var x = 0; x < width; x++)
                {
                    var pixel = at + x * (bits / 8); var dest = x * 4;
                    row[dest] = dib[pixel]; row[dest + 1] = dib[pixel + 1]; row[dest + 2] = dib[pixel + 2];
                    row[dest + 3] = alpha ? dib[pixel + 3] : (byte)255;
                }
                Marshal.Copy(row, 0, locked.Scan0 + y * locked.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(locked); }
        return Encode(bitmap);
    }
    public static byte[] ToDib(Bitmap image, bool v5)
    {
        // V5 retains alpha; the legacy 24-bit fallback composites onto white.
        using var flat = new Bitmap(image.Width, image.Height, v5 ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(flat))
        {
            graphics.Clear(v5 ? Color.Transparent : Color.White);
            graphics.DrawImageUnscaled(image, 0, 0);
        }
        var bits = v5 ? 32 : 24; var header = v5 ? 124 : 40; var stride = ((image.Width * bits + 31) / 32) * 4;
        var data = new byte[header + stride * image.Height];
        void I(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at), value);
        I(0, header); I(4, image.Width); I(8, -image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)bits);
        I(16, v5 ? 3 : 0); I(20, stride * image.Height);
        if (v5) { I(40, 0xff0000); I(44, 0xff00); I(48, 0xff); I(52, unchecked((int)0xff000000)); I(56, 0x73524742); }
        var locked = flat.LockBits(new Rectangle(0, 0, flat.Width, flat.Height), ImageLockMode.ReadOnly, flat.PixelFormat);
        try { for (var y = 0; y < flat.Height; y++) Marshal.Copy(locked.Scan0 + y * locked.Stride, data, header + y * stride, image.Width * (bits / 8)); }
        finally { flat.UnlockBits(locked); }
        return data;
    }
}
