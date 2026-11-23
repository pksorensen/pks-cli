namespace PKS.Infrastructure.Services.Images;

/// <summary>
/// Reads width and height from a PNG or JPEG header without decoding the image,
/// so callers can match an output size to an input without an imaging library.
/// </summary>
internal static class ImageDimensions
{
    public static bool TryRead(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[24];
            if (fs.Read(head, 0, head.Length) < head.Length)
                return false;

            // PNG: 8-byte signature, then the IHDR chunk carries big-endian width/height.
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            {
                width = ReadBigEndian32(head, 16);
                height = ReadBigEndian32(head, 20);
                return width > 0 && height > 0;
            }

            // JPEG: walk the markers until a start-of-frame segment.
            if (head[0] != 0xFF || head[1] != 0xD8)
                return false;
            fs.Position = 2;
            var buf = new byte[7];
            while (fs.Position < fs.Length)
            {
                int b;
                while ((b = fs.ReadByte()) != 0xFF) { if (b < 0) return false; }
                int marker;
                while ((marker = fs.ReadByte()) == 0xFF) { }
                if (marker < 0) return false;
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                    continue;
                if (fs.Read(buf, 0, 2) < 2) return false;
                var length = (buf[0] << 8) | buf[1];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    if (fs.Read(buf, 0, 5) < 5) return false;
                    height = (buf[1] << 8) | buf[2];
                    width = (buf[3] << 8) | buf[4];
                    return width > 0 && height > 0;
                }
                fs.Position += length - 2;
            }
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static int ReadBigEndian32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
