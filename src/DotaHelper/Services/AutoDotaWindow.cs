using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DotaHelper.Services;

/// <summary>Window detection is based on the dota2.exe process, not a title
/// string or one fixed monitor/resolution. No game process memory is read.</summary>
public static class AutoDotaWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint pt);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    public static Rectangle? Locate(bool requireForeground = true)
    {
        var foreground = GetForegroundWindow();
        foreach (var process in Process.GetProcessesByName("dota2"))
        {
            using (process)
            {
                try
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
                        continue;
                    if (requireForeground && foreground != hwnd)
                        continue;
                    if (!GetClientRect(hwnd, out var rect))
                        continue;
                    var origin = new NativePoint();
                    if (!ClientToScreen(hwnd, ref origin)) continue;
                    var bounds = new Rectangle(origin.X, origin.Y,
                        rect.Right - rect.Left, rect.Bottom - rect.Top);
                    if (bounds.Width < 800 || bounds.Height < 480)
                        continue;
                    return bounds;
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception) { }
            }
        }
        return null;
    }

    public static Bitmap Capture(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        return bmp;
    }
}

/// <summary>
/// Detect five portraits in each team using top-strip texture and repeated
/// spacing, independently of left/right team HUD offsets or screen resolution.
/// This is a confidence-scored layout hypothesis, not guaranteed recognition.
/// </summary>
public static class AutoDraftLayout
{
    /// <summary>
    /// Detect a dense visible portrait gallery without assuming a specific
    /// hero grouping (attributes, roles, custom layout).
    /// Blocks screenshots of strategy/item/map screens from creating fake bans.
    /// The gallery tends to occupy a substantial part of the screen and
    /// consists of repeated textured, colored cells.
    /// </summary>
    public static bool HasVisibleHeroGrid(Bitmap screen)
    {
        var w=screen.Width;var h=screen.Height;
        if(w<800 || h<480) return false;
        int x0=(int)(w*.03),x1=(int)(w*.97);
        int y0=(int)(h*.14),y1=(int)(h*.78);
        int step=Math.Clamp(w/52,22,34),samples=0,rich=0;
        for(int cy=y0;cy+step<y1;cy+=step)
        for(int cx=x0;cx+step<x1;cx+=step)
        {
            double sum=0,sum2=0;int colored=0,n=0;
            for(int y=cy+2;y<cy+step;y+=4)
            for(int x=cx+2;x<cx+step;x+=4)
            {
                var c=screen.GetPixel(x,y);
                double brightness=Math.Max(c.R,Math.Max(c.G,c.B));
                sum+=brightness;sum2+=brightness*brightness;
                var lo=Math.Min(c.R,Math.Min(c.G,c.B));
                if(brightness>70 && brightness-lo>50) colored++;
                n++;
            }
            if(n==0)continue;
            var variance=Math.Sqrt(Math.Max(0,sum2/n-sum*sum/(n*n)));
            if(variance>37 && colored/(double)n>.13)rich++;
            samples++;
        }
        return samples>60 && rich/(double)samples>.31;
    }

    public static (ScreenArea Radiant, ScreenArea Dire, ScreenArea Bans)? Locate(Bitmap screen)
    {
        int w = screen.Width, h = screen.Height;
        if (w < 800 || h < 480) return null;

        // Crop away HUD labels/player names. The portrait faces live between
        // approximately 1% and 6.6% of the screen height on current All Pick.
        // Both offsets and portrait pitch are detected anew on every frame.
        int y0 = Math.Clamp((int)(h * .008), 3, 20);
        int y1 = Math.Min((int)(h * .066), 84);
        if (y1 - y0 < 25) return null;
        var signal = new double[w];

        for (int x = 2; x < w - 2; x++)
        {
            double saturated = 0, sum = 0, sumSquared = 0;
            int n = 0;
            for (int y = y0; y < y1; y += 3)
            {
                var c = screen.GetPixel(x, y);
                var high = Math.Max(c.R, Math.Max(c.G, c.B));
                var low = Math.Min(c.R, Math.Min(c.G, c.B));
                if (high > 65 && high - low > 42) saturated++;
                var brightness = (c.R + c.G + c.B) / 3.0;
                sum += brightness;
                sumSquared += brightness * brightness;
                n++;
            }
            if (n == 0) continue;
            var variance = Math.Sqrt(Math.Max(0, sumSquared / n -
                (sum / n) * (sum / n)));
            signal[x] = saturated / n + variance / 255d;
        }

        // Smooth per-column scores, then compare textured portrait interiors
        // against the lower-energy gaps. A five-cell periodic pattern is
        // required, preventing isolated UI icons or the timer from matching.
        var prefix = new double[w + 1];
        for (int x = 0; x < w; x++)
        {
            var smoothed = (signal[Math.Max(0, x - 2)] +
                signal[Math.Max(0, x - 1)] + signal[x] +
                signal[Math.Min(w - 1, x + 1)] +
                signal[Math.Min(w - 1, x + 2)]) / 5.0;
            prefix[x + 1] = prefix[x] + smoothed;
        }

        double Mean(int l, int r)
        {
            l = Math.Clamp(l, 0, w);
            r = Math.Clamp(r, 0, w);
            return r > l ? (prefix[r] - prefix[l]) / (r - l) : 0;
        }

        (int X, int Pitch, double Score)? Find(bool radiant)
        {
            int minX = (int)(w * (radiant ? .07 : .53));
            int endX = (int)(w * (radiant ? .47 : .96));
            (int X, int Pitch, double Score)? best = null;

            for (int pitch = (int)(w * .053); pitch <= (int)(w * .075); pitch += 2)
            {
                var cell = (int)(pitch * .86);
                if (pitch < 32 || cell < 26) continue;
                for (int x = minX; x + pitch * 4 + cell < endX; x += 2)
                {
                    double inside = 0, between = 0, smallest = double.MaxValue;
                    int textured = 0;
                    for (int i = 0; i < 5; i++)
                    {
                        var left = x + i * pitch;
                        var rich = Mean(left + 5, left + cell - 4);
                        inside += rich;
                        smallest = Math.Min(smallest, rich);
                        if (rich >= .20) textured++;
                        if (i < 4) between += Mean(left + cell, left + pitch);
                    }
                    // At least three visible portraits are needed to
                    // establish a layout in draft phase. Blank slots do not
                    // by themselves prove pre-picks.
                    if (textured < 3) continue;
                    var score = inside / 5 - .60 * between / 4 + .03 * smallest;
                    if (score > .30 && (best is null || score > best.Value.Score))
                        best = (x, pitch, score);
                }
            }
            return best;
        }

        var left = Find(true);
        var right = Find(false);
        if (left is null || right is null) return null;
        var a = left.Value;
        var b = right.Value;
        var radiantArea = new ScreenArea(a.X, y0, a.Pitch * 5, y1 - y0);
        var direArea = new ScreenArea(b.X, y0, b.Pitch * 5, y1 - y0);

        var gallery = new ScreenArea((int)(w * .015), (int)(h * .13),
            (int)(w * .97), (int)(h * .76));
        return (radiantArea, direArea, gallery);
    }
}
