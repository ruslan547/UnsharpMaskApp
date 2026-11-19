using Avalonia;
using Avalonia.Media.Imaging;

namespace UnsharpMaskApp;

/// <summary>
/// Демонстрационный сценарий для отчёта (запускается при заданной переменной окружения LAB_DEMO_OUT):
/// загрузка, запуск с отменой на середине, полный запуск, сохранение; после этапов - снимки окна.
/// </summary>
public sealed partial class MainWindow
{
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        string? outDir = Environment.GetEnvironmentVariable("LAB_DEMO_OUT");
        string? input = Environment.GetEnvironmentVariable("LAB_DEMO_IN");
        if (outDir == null || input == null) return;

        await LoadAsync(input);

        _poolSize.Value = 2;
        _stripHeight.Value = 16;
        _sigma.Value = 6;
        _progressHook = p => { if (p.Done >= p.Total / 2) _cts?.Cancel(); };
        await ProcessAsync();
        _progressHook = null;
        await Snapshot(Path.Combine(outDir, "gui_cancel.png"));

        _poolSize.Value = 8;
        _stripHeight.Value = 32;
        _sigma.Value = 2;
        await ProcessAsync();
        await SaveToAsync(Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "_sharp.png"));
        await Snapshot(Path.Combine(outDir, "gui_done.png"));
        Close();
    }

    private async Task Snapshot(string path)
    {
        await Task.Delay(500);
        var size = new PixelSize((int)Bounds.Width * 2, (int)Bounds.Height * 2);
        using var rtb = new RenderTargetBitmap(size, new Vector(192, 192));
        rtb.Render(this);
        rtb.Save(path);
    }
}
