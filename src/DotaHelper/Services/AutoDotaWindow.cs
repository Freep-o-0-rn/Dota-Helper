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
        var w=screen.Width; var h=screen.Height;
        if(w<800 || h<480) return null;
        var top=Math.Clamp((int)(h*.072),35,96);
        var y=Math.Clamp((int)(h*.013),3,22);
        var bottom=Math.Min(top,h-1);
        if(bottom-y<20) return null;

        // Horizontal texture+color score at the top, sampled sparsely for
        // fast scanning. Dense portrait rows score higher than HUD background.
        var energy=new double[w];
        for(int x=0;x<w;x+=3)
        {
            double score=0;var prev=screen.GetPixel(x,y+2);
            for(int yy=y+5; yy<bottom; yy+=5)
            {
                var c=screen.GetPixel(x,yy);
                var diff=Math.Abs(c.R-prev.R)+Math.Abs(c.G-prev.G)+Math.Abs(c.B-prev.B);
                score+=Math.Min(240,diff);
                prev=c;
            }
            energy[x]=score;
        }
        // Smooth away thin HUD bars and text.
        var integral=new double[w+1];
        for(int i=0;i<w;i++) integral[i+1]=integral[i]+energy[i];

        (int Start,int Step,int Cell,double Score)? Find(int from, int to)
        {
            var best=(Start:0,Step:0,Cell:0,Score:0d);
            for(double scale=.045;scale<=.072;scale+=.004)
            {
                var step=(int)(w*scale);
                var cell=(int)(step*.86);
                if(step<30 || cell<25) continue;
                for(var x=from;x+4*step+cell<to;x+=Math.Max(3,step/10))
                {
                    double total=0;
                    for(int i=0;i<5;i++)
                    {
                        var l=x+i*step+cell/7;
                        var r=x+i*step+cell-cell/7;
                        total+=(integral[Math.Min(w,r)]-integral[Math.Max(0,l)])
                            /Math.Max(1,r-l);
                    }
                    // favor consistent rows instead of one intense effect.
                    if(total>best.Score)
                        best=(x,step,cell,total);
                }
            }
            return best.Score>125 ? best : null;
        }
        // Don't assume each team's HUD anchors are symmetric or at 5%/95%.
        var left=Find(0,(int)(w*.48));
        var right=Find((int)(w*.47),w);
        if(left is null || right is null) return null;
        var l=left.Value;var r=right.Value;
        var leftArea=new ScreenArea(l.Start,y,4*l.Step+l.Cell,bottom-y);
        var rightArea=new ScreenArea(r.Start,y,4*r.Step+r.Cell,bottom-y);
        // The grid may be in a 4-column attribute view, a 2-column role
        // view, or absent (strategy screen). Do not assume a fixed grid.
        var banArea=new ScreenArea((int)(w*.015),(int)(h*.13),
            (int)(w*.97),(int)(h*.76));
        return (leftArea,rightArea,banArea);
    }
}
