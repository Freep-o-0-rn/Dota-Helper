using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// All coordinates are physical pixels on one screen. Calibration is user-supplied
/// from a real Dota All Pick screenshot, not hardcoded assumptions about resolution.
/// </summary>
public sealed record ScreenArea(int X, int Y, int Width, int Height)
{
    public bool Valid => Width >= 20 && Height >= 15 && X >= 0 && Y >= 0;
    public Rectangle Rect => new(X, Y, Width, Height);
}

public sealed record DraftScreenCalibration(
    int ScreenWidth, int ScreenHeight,
    ScreenArea RadiantPicks, ScreenArea DirePicks, ScreenArea HeroGrid)
{
    public bool Valid => ScreenWidth > 0 && ScreenHeight > 0 &&
        RadiantPicks.Valid && DirePicks.Valid && HeroGrid.Valid &&
        new[] { RadiantPicks, DirePicks, HeroGrid }.All(r =>
            r.X + r.Width <= ScreenWidth && r.Y + r.Height <= ScreenHeight);
}

public sealed record RecognizedHero(int HeroId, double Confidence);
public sealed record ScreenDraftObservation(
    IReadOnlyList<RecognizedHero?> RadiantPicks,
    IReadOnlyList<RecognizedHero?> DirePicks,
    IReadOnlyList<RecognizedHero> BannedHeroes);

/// <summary>
/// Pure managed template similarity for the 10 on-screen pick rectangles and
/// red-slash ban candidates. No Dota memory access, injection, OCR or external AI.
/// </summary>
public static class PortraitRecognition
{
    private const int Width = 24;
    private const int Height = 14;

    public static double[] Signature(Bitmap image)
    {
        using var small = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(image, 0, 0, Width, Height);
        }
        var values = new double[Width * Height];
        var sum = 0d;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var c = small.GetPixel(x, y);
            var v = .299 * c.R + .587 * c.G + .114 * c.B;
            values[y * Width + x] = v;
            sum += v;
        }
        var mean = sum / values.Length;
        double norm = 0;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] -= mean;
            norm += values[i] * values[i];
        }
        norm = Math.Sqrt(norm);
        if (norm < .001) return new double[values.Length]; // blank/solid tile
        for (var i = 0; i < values.Length; i++)
            values[i] /= norm;
        return values;
    }

    public static double Similarity(double[] a, double[] b)
    {
        if (a.Length != b.Length) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    public static RecognizedHero? Match(double[] sample,
        IReadOnlyDictionary<int, double[]> templates,
        double minimum = .78, double gap = .075)
    {
        if (sample.All(v => v == 0)) return null;
        var best = (id: 0, score: -1d);
        var second = -1d;
        foreach (var (id, template) in templates)
        {
            var score = Similarity(sample, template);
            if (score > best.score)
            {
                second = best.score;
                best = (id, score);
            }
            else if (score > second) second = score;
        }
        return best.id > 0 && best.score >= minimum &&
               best.score - second >= gap
            ? new(best.id, best.score) : null;
    }

    /// <summary>
    /// Detect isolated red ascending slash components inside a calibrated
    /// Dota hero gallery. Pure shapes are candidates only: final ID requires
    /// confident comparison against an original Valve hero portrait.
    /// </summary>
    public static IReadOnlyList<Rectangle> FindRedBanSlashes(Bitmap bitmap,
        ScreenArea area)
    {
        if (!area.Valid) return [];
        var bounds = Rectangle.Intersect(area.Rect,
            new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (bounds.Width <= 0 || bounds.Height <= 0) return [];
        using var cropped = bitmap.Clone(bounds, PixelFormat.Format24bppRgb);
        var data = cropped.LockBits(new Rectangle(0, 0, cropped.Width, cropped.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[Math.Abs(data.Stride) * cropped.Height];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { cropped.UnlockBits(data); }
        int w = cropped.Width, h = cropped.Height;
        var red = new bool[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = y * data.Stride + x * 3;
            var b = bytes[idx]; var g = bytes[idx+1]; var r = bytes[idx+2];
            red[y*w+x] = r >= 110 && r >= g * 1.5 && r >= b * 1.25 &&
                r-g >= 35;
        }
        var used = new bool[w * h];
        var found = new List<Rectangle>();
        var stack = new Stack<int>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var start = y * w + x;
            if (!red[start] || used[start]) continue;
            stack.Push(start);
            used[start] = true;
            var count = 0; int minX=x,maxX=x,minY=y,maxY=y;
            double xx=0, yy=0, xy=0, xsum=0, ysum=0;
            while (stack.Count > 0)
            {
                var i = stack.Pop(); var cx=i%w; var cy=i/w;
                count++; minX=Math.Min(minX,cx); maxX=Math.Max(maxX,cx);
                minY=Math.Min(minY,cy); maxY=Math.Max(maxY,cy);
                xx+=cx*cx; yy+=cy*cy; xy+=cx*cy; xsum+=cx; ysum+=cy;
                for (var dy=-1;dy<=1;dy++)
                for (var dx=-1;dx<=1;dx++)
                {
                    if (dx==0&&dy==0) continue;
                    var nx=cx+dx; var ny=cy+dy;
                    if (nx<0||nx>=w||ny<0||ny>=h) continue;
                    var ni=ny*w+nx;
                    if (red[ni]&&!used[ni]) { used[ni]=true;stack.Push(ni); }
                }
            }
            var cw=maxX-minX+1; var ch=maxY-minY+1;
            if (count<12||cw<14||ch<7||cw>140||ch>100) continue;
            // A red slash from lower left to upper right has negative slope.
            var covariance=xy-xsum*ysum/count;
            var variance=xx-xsum*xsum/count;
            var slope=variance>1?covariance/variance:0;
            var density=count/(double)(cw*ch);
            if (slope>-.18 || slope< -1.8 || density>.47) continue;
            found.Add(new Rectangle(minX+bounds.X,minY+bounds.Y,cw,ch));
        }
        // Adjacent red bands can be contiguous components of one slash.
        return found;
    }
}

public sealed class ScreenDraftReader : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<int, double[]> _templates = [];
    private readonly string _config = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotaHelper", "screen-calibration.json");

    public DraftScreenCalibration? Calibration { get; private set; }
    public int TemplateCount => _templates.Count;
    public ScreenDraftReader()
    {
        try
        {
            if (File.Exists(_config))
            {
                var calibration = JsonSerializer.Deserialize<DraftScreenCalibration>(
                    File.ReadAllText(_config));
                if (calibration?.Valid == true) Calibration = calibration;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public void SaveCalibration(DraftScreenCalibration calibration)
    {
        if (!calibration.Valid) throw new ArgumentException("Invalid screen calibration");
        Directory.CreateDirectory(Path.GetDirectoryName(_config)!);
        File.WriteAllText(_config, JsonSerializer.Serialize(calibration));
        Calibration = calibration;
    }

    public static Bitmap CapturePrimary()
    {
        var bounds = Screen.PrimaryScreen?.Bounds ??
            throw new InvalidOperationException("Не найден основной экран Windows.");
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        return bmp;
    }

    public async Task<int> LoadPortraitsAsync(IEnumerable<Hero> heroes,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var all = heroes.Where(h=>h.Id>0 && !string.IsNullOrWhiteSpace(h.PortraitUrl)).ToList();
        foreach (var hero in all)
        {
            ct.ThrowIfCancellationRequested();
            if (_templates.ContainsKey(hero.Id)) continue;
            try
            {
                // Hero.PortraitUrl is restricted to the Steam CDN by the model.
                var bytes = await _http.GetByteArrayAsync(hero.PortraitUrl, ct);
                using var stream = new MemoryStream(bytes);
                using var bitmap = new Bitmap(stream);
                _templates[hero.Id] = PortraitRecognition.Signature(bitmap);
                progress?.Report(_templates.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or
                TaskCanceledException or ArgumentException or
                System.Runtime.InteropServices.ExternalException or IOException)
            {
                // Do not invent templates for heroes whose images did not load.
            }
        }
        return _templates.Count;
    }

    public ScreenDraftObservation Analyze(Bitmap screen, DraftScreenCalibration calibration)
    {
        if (!calibration.Valid ||
            screen.Width != calibration.ScreenWidth ||
            screen.Height != calibration.ScreenHeight)
            throw new InvalidOperationException(
                "Изменилось разрешение экрана. Повтори калибровку.");
        if (_templates.Count < 20)
            throw new InvalidOperationException("Ещё не загружены портреты для распознавания.");

        IReadOnlyList<RecognizedHero?> Picks(ScreenArea area)
        {
            var result = new List<RecognizedHero?>();
            for (var i=0;i<5;i++)
            {
                var left=area.X+(int)Math.Round(i*area.Width/5d);
                var right=area.X+(int)Math.Round((i+1)*area.Width/5d);
                // Trim borders and player text, sample actual portrait interior.
                var x=left+Math.Max(1,(right-left)/10);
                var y=area.Y+Math.Max(1,area.Height/9);
                var w=right-left-2*Math.Max(1,(right-left)/10);
                var h=area.Height-2*Math.Max(1,area.Height/9);
                using var cell=screen.Clone(new Rectangle(x,y,w,h),
                    PixelFormat.Format24bppRgb);
                result.Add(PortraitRecognition.Match(
                    PortraitRecognition.Signature(cell),_templates));
            }
            return result;
        }

        var detectedBans=new Dictionary<int,RecognizedHero>();
        foreach(var slash in PortraitRecognition.FindRedBanSlashes(screen,calibration.HeroGrid))
        {
            // Portrait is wider than the red diagonal slash bounds.
            var cx=slash.Left+slash.Width/2;
            var cy=slash.Top+slash.Height/2;
            var width=Math.Clamp((int)(slash.Width*1.15),28,128);
            var height=Math.Clamp((int)(width*9d/16),17,80);
            var rect=new Rectangle(cx-width/2,cy-height/2,width,height);
            rect=Rectangle.Intersect(rect,calibration.HeroGrid.Rect);
            if(rect.Width<20||rect.Height<14)continue;
            using var cell=screen.Clone(rect,PixelFormat.Format24bppRgb);
            // Desaturation and overlays make ban matches weaker than picks,
            // but require a larger margin over the next closest hero.
            var match=PortraitRecognition.Match(
                PortraitRecognition.Signature(cell),_templates,.65,.11);
            if(match is not null&&
                (!detectedBans.TryGetValue(match.HeroId,out var prev)||
                 prev.Confidence<match.Confidence))
                detectedBans[match.HeroId]=match;
        }
        return new(Picks(calibration.RadiantPicks),Picks(calibration.DirePicks),
            detectedBans.Values.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
