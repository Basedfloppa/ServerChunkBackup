namespace VsChunkDump.Core;

/// <summary>Simple RGBA canvas with alpha blending and convex polygon filling.</summary>
public sealed class Canvas
{
    private int Width { get; }
    private int Height { get; }
    public byte[] Pixels { get; }

    public Canvas(int width, int height, (byte R, byte G, byte B, byte A) background)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            Pixels[i * 4 + 0] = background.R;
            Pixels[i * 4 + 1] = background.G;
            Pixels[i * 4 + 2] = background.B;
            Pixels[i * 4 + 3] = background.A;
        }
    }

    public void Blend(int x, int y, (byte R, byte G, byte B, byte A) c)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int o = (y * Width + x) * 4;
        if (c.A == 255)
        {
            Pixels[o] = c.R; Pixels[o + 1] = c.G; Pixels[o + 2] = c.B; Pixels[o + 3] = 255;
            return;
        }
        if (c.A == 0) return;

        float a = c.A / 255f;
        float ia = 1f - a;
        Pixels[o] = (byte)(c.R * a + Pixels[o] * ia);
        Pixels[o + 1] = (byte)(c.G * a + Pixels[o + 1] * ia);
        Pixels[o + 2] = (byte)(c.B * a + Pixels[o + 2] * ia);
        Pixels[o + 3] = (byte)Math.Min(255, c.A + Pixels[o + 3] * ia);
    }

    public void FillSpan(int y, int x0, int x1, (byte R, byte G, byte B, byte A) c)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        x0 = Math.Max(0, x0);
        x1 = Math.Min(Width - 1, x1);
        for (int x = x0; x <= x1; x++) Blend(x, y, c);
    }

    public void FillRect(int x, int y, int w, int h, (byte R, byte G, byte B, byte A) c)
    {
        for (int yy = y; yy < y + h; yy++)
        {
            FillSpan(yy, x, x + w - 1, c);
        }
    }

    /// <summary>Diamond (the top face in isometric view) centered at (cx, cy).</summary>
    public void FillDiamond(int cx, int cy, int halfW, int halfH, (byte R, byte G, byte B, byte A) c)
    {
        if (halfH <= 0) return;
        for (int dy = -halfH; dy <= halfH; dy++)
        {
            float t = 1f - MathF.Abs(dy) / (float)halfH;
            int hw = (int)MathF.Round(halfW * t);
            FillSpan(cy + dy, cx - hw, cx + hw, c);
        }
    }

    /// <summary>Fill a convex quad (scanline).</summary>
    public void FillQuad((float X, float Y) p0, (float X, float Y) p1, (float X, float Y) p2, (float X, float Y) p3,
        (byte R, byte G, byte B, byte A) c)
    {
        Span<(float X, float Y)> pts = [p0, p1, p2, p3];
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in pts) { minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y); }

        int y0 = Math.Max(0, (int)MathF.Floor(minY));
        int y1 = Math.Min(Height - 1, (int)MathF.Ceiling(maxY));
        Span<float> xs = stackalloc float[4];

        for (int y = y0; y <= y1; y++)
        {
            float scanY = y + 0.5f;
            int n = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % 4];
                if ((a.Y <= scanY && b.Y > scanY) || (b.Y <= scanY && a.Y > scanY))
                {
                    float t = (scanY - a.Y) / (b.Y - a.Y);
                    xs[n++] = a.X + t * (b.X - a.X);
                }
            }
            if (n < 2) continue;
            float xMin = xs[0], xMax = xs[0];
            for (int i = 1; i < n; i++) { xMin = MathF.Min(xMin, xs[i]); xMax = MathF.Max(xMax, xs[i]); }
            FillSpan(y, (int)MathF.Round(xMin), (int)MathF.Round(xMax), c);
        }
    }
}
