using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DotaHelper.Services;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DotaHelper;

public partial class ScreenCalibrationWindow : Window
{
    private readonly ScreenArea?[] _areas = new ScreenArea?[3];
    private int _step;
    private Point? _start;
    private readonly System.Windows.Media.Color[] _colors = [];
    public DraftScreenCalibration? Calibration { get; private set; }

    private readonly string[] _instructions =
    [
        "Шаг 1/3. Обведи ПЯТЬ портретов Radiant в верхней полосе игры — одним прямоугольником, включая все слоты.",
        "Шаг 2/3. Обведи ПЯТЬ портретов Dire в верхней полосе игры — одним прямоугольником.",
        "Шаг 3/3. Обведи СЕТКУ ГЕРОЕВ Dota 2, где автоматически забаненные герои перечёркнуты красной диагональю."
    ];

    public ScreenCalibrationWindow(Bitmap screenshot)
    {
        InitializeComponent();
        Surface.Width = screenshot.Width;
        Surface.Height = screenshot.Height;
        ScreenImage.Width = screenshot.Width;
        ScreenImage.Height = screenshot.Height;
        using var stream = new MemoryStream();
        screenshot.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.StreamSource = stream;
        source.EndInit();
        source.Freeze();
        ScreenImage.Source = source;
        Instruction.Text = _instructions[0];
    }

    private void Surface_Down(object sender, MouseButtonEventArgs e)
    {
        if (_step >= 3) return;
        _start = e.GetPosition(Surface);
        Surface.CaptureMouse();
        Draw(SelectionRectangle, _start.Value, _start.Value);
    }

    private void Surface_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_start is Point start)
            Draw(SelectionRectangle, start, e.GetPosition(Surface));
    }

    private void Surface_Up(object sender, MouseButtonEventArgs e)
    {
        if (_start is not Point start) return;
        var end = e.GetPosition(Surface);
        _start = null;
        Surface.ReleaseMouseCapture();
        SelectionRectangle.Visibility = Visibility.Collapsed;
        var x = (int)Math.Floor(Math.Min(start.X, end.X));
        var y = (int)Math.Floor(Math.Min(start.Y, end.Y));
        var w = (int)Math.Abs(end.X - start.X);
        var h = (int)Math.Abs(end.Y - start.Y);
        var area = new ScreenArea(Math.Max(0, x), Math.Max(0, y), w, h);
        if (!area.Valid || x + w > Surface.Width || y + h > Surface.Height)
        {
            Instruction.Text = "Область слишком мала или выходит за экран. " + _instructions[_step];
            return;
        }
        _areas[_step] = area;
        Draw(_step switch
        {
            0 => RadiantRectangle,
            1 => DireRectangle,
            _ => BansRectangle
        }, start, end);
        _step++;
        Instruction.Text = _step < 3
            ? _instructions[_step]
            : "Все три области заданы. Сохрани. Позже можно повторить калибровку при изменении разрешения.";
        SaveButton.IsEnabled = _step == 3;
    }

    private static void Draw(Rectangle rectangle, Point a, Point b)
    {
        var left = Math.Max(0, Math.Min(a.X, b.X));
        var top = Math.Max(0, Math.Min(a.Y, b.Y));
        rectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        rectangle.Width = Math.Abs(a.X - b.X);
        rectangle.Height = Math.Abs(a.Y - b.Y);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_areas.Any(x => x is null)) return;
        Calibration = new DraftScreenCalibration(
            (int)Surface.Width, (int)Surface.Height,
            _areas[0]!, _areas[1]!, _areas[2]!);
        if (!Calibration.Valid) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
