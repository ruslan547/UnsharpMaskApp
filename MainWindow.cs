using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using UnsharpMaskApp.Core;

namespace UnsharpMaskApp;

/// <summary>Главное окно. Все обработчики выполняются в UI-потоке, длительные операции - асинхронно.</summary>
public sealed class MainWindow : Window
{
    private readonly Button _openButton = new() { Content = "Открыть..." };
    private readonly Button _startButton = new() { Content = "Обработать" };
    private readonly Button _cancelButton = new() { Content = "Отмена" };
    private readonly Button _saveButton = new() { Content = "Сохранить..." };

    private readonly NumericUpDown _amount = Num(1.5m, 0.1m, 5m, 0.1m, "0.0");
    private readonly NumericUpDown _sigma = Num(2.0m, 0.5m, 10m, 0.5m, "0.0");
    private readonly NumericUpDown _threshold = Num(0, 0, 64, 1, "0");
    private readonly NumericUpDown _poolSize = Num(Environment.ProcessorCount, 1, 64, 1, "0");
    private readonly NumericUpDown _stripHeight = Num(32, 4, 1024, 4, "0");

    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 18, MinWidth = 300 };
    private readonly TextBlock _progressText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 400 };
    private readonly Image _sourceView = new() { Stretch = Stretch.Uniform };
    private readonly Image _resultView = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 150,
        FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11
    };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4) };

    private ImageBuffer? _source;
    private ImageBuffer? _result;
    private string? _sourcePath;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        Title = "ЛР: асинхронная многопоточная обработка - нерезкое маскирование (вариант 10)";
        Width = 1220;
        Height = 760;
        Content = BuildLayout();

        _openButton.Click += async (_, _) => await OpenAsync();
        _startButton.Click += async (_, _) => await ProcessAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _saveButton.Click += async (_, _) => await SaveAsync();
        Closing += (_, _) => _cts?.Cancel();
        SetBusy(false);
    }

    private static NumericUpDown Num(decimal value, decimal min, decimal max, decimal inc, string fmt) => new()
    {
        Value = value, Minimum = min, Maximum = max, Increment = inc, FormatString = fmt, Width = 112,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Control BuildLayout()
    {
        static TextBlock Label(string s) => new()
            { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8) };
        toolbar.Children.AddRange(new Control[] { _openButton, _startButton, _cancelButton, _saveButton });

        var parameters = new WrapPanel { Margin = new Thickness(0, 0, 8, 8) };
        parameters.Children.AddRange(new Control[]
        {
            Label("Коэффициент k:"), _amount, Label("σ размытия:"), _sigma, Label("Порог:"), _threshold,
            Label("Потоков в пуле:"), _poolSize, Label("Высота полосы:"), _stripHeight
        });

        var progressRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8, 0, 8, 8) };
        progressRow.Children.AddRange(new Control[] { _progress, _progressText });

        var images = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        AddImage(images, 0, "Исходное изображение", _sourceView);
        AddImage(images, 1, "Результат (unsharp mask)", _resultView);

        var root = new DockPanel();
        foreach (var (c, dock) in new (Control, Dock)[]
                 {
                     (toolbar, Dock.Top), (parameters, Dock.Top), (progressRow, Dock.Top),
                     (_status, Dock.Bottom), (_log, Dock.Bottom)
                 })
        {
            DockPanel.SetDock(c, dock);
            root.Children.Add(c);
        }
        root.Children.Add(images);
        return root;
    }

    private static void AddImage(Grid grid, int col, string caption, Image image)
    {
        var text = new TextBlock { Text = caption, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0) };
        var border = new Border
        {
            Child = image, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 4, 8, 8), Background = Brushes.WhiteSmoke
        };
        Grid.SetColumn(text, col);
        Grid.SetColumn(border, col);
        Grid.SetRow(border, 1);
        grid.Children.Add(text);
        grid.Children.Add(border);
    }

    private void SetBusy(bool busy)
    {
        _openButton.IsEnabled = !busy;
        _startButton.IsEnabled = !busy && _source != null;
        _saveButton.IsEnabled = !busy && _result != null;
        _cancelButton.IsEnabled = busy;
        foreach (var n in new[] { _amount, _sigma, _threshold, _poolSize, _stripHeight }) n.IsEnabled = !busy;
    }

    private void Log(string line) => _log.Text += $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}";

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбор изображения",
            FileTypeFilter = new[] { new FilePickerFileType("Изображения") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } } }
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var sw = Stopwatch.StartNew();
            Log($"Асинхронное чтение: {Path.GetFileName(path)}");
            _source = await ImageIo.ReadAsync(path, _cts.Token); // UI-поток не блокируется
            _sourcePath = path;
            _result = null;
            _resultView.Source = null;
            _sourceView.Source = ToBitmap(_source);
            Log($"Загружено {_source.Width}x{_source.Height} за {sw.ElapsedMilliseconds} мс");
            _status.Text = $"Файл: {Path.GetFileName(path)}, {_source.Width}x{_source.Height}";
        }
        catch (OperationCanceledException) { Log("Чтение отменено"); }
        catch (Exception ex) { Log($"Ошибка чтения: {ex.Message}"); }
        finally { FinishOperation(); }
    }

    private async Task ProcessAsync()
    {
        if (_source == null) return;
        _cts = new CancellationTokenSource();
        _result = null;
        _resultView.Source = null;
        SetBusy(true);

        var pipeline = new ProcessingPipeline
        {
            PoolSize = (int)_poolSize.Value!,
            StripHeight = (int)_stripHeight.Value!,
            Params = new UnsharpParams((double)_amount.Value!, (double)_sigma.Value!, (int)_threshold.Value!)
        };
        _progress.Value = 0;
        Log($"Старт: k={pipeline.Params.Amount}, σ={pipeline.Params.Sigma}, порог={pipeline.Params.Threshold}, " +
            $"пул={pipeline.PoolSize}, полоса={pipeline.StripHeight} строк");

        // Progress<T> маршализует уведомления сборщика в UI-поток.
        var progress = new Progress<PipelineProgress>(p =>
        {
            _progress.Value = 100.0 * p.Done / p.Total;
            _progressText.Text = $"Собрано фрагментов: {p.Done} из {p.Total}";
        });

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await pipeline.RunAsync(_source, progress, _cts.Token);
            _result = result;
            _resultView.Source = ToBitmap(result);
            Log($"Готово за {sw.ElapsedMilliseconds} мс. Фрагменты по потокам: {pipeline.LastWorkerStats}");
            _status.Text = $"Обработка завершена за {sw.ElapsedMilliseconds} мс";
        }
        catch (OperationCanceledException)
        {
            _progressText.Text += " - отменено";
            Log($"Обработка отменена через {sw.ElapsedMilliseconds} мс, частичный результат отброшен");
            _status.Text = "Отменено";
        }
        catch (Exception ex) { Log($"Ошибка: {ex.GetBaseException().Message}"); }
        finally
        {
            Log($"Рабочих потоков после завершения: {FixedThreadPool.AliveThreads}");
            FinishOperation();
        }
    }

    private async Task SaveAsync()
    {
        if (_result == null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранение результата",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_sourcePath) + "_sharp.png",
            DefaultExtension = "png"
        });
        if (file?.TryGetLocalPath() is { } path) await SaveToAsync(path);
    }

    private async Task SaveToAsync(string path)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var sw = Stopwatch.StartNew();
            await ImageIo.WriteAsync(_result!, path, _cts.Token);
            Log($"Асинхронная запись завершена: {Path.GetFileName(path)} ({sw.ElapsedMilliseconds} мс)");
        }
        catch (OperationCanceledException) { Log("Запись отменена, временный файл удалён"); }
        catch (Exception ex) { Log($"Ошибка записи: {ex.Message}"); }
        finally { FinishOperation(); }
    }

    private void FinishOperation()
    {
        _cts?.Dispose();
        _cts = null;
        SetBusy(false);
        _log.CaretIndex = _log.Text?.Length ?? 0;
    }

    private static WriteableBitmap ToBitmap(ImageBuffer img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int y = 0; y < img.Height; y++)
            Marshal.Copy(img.Pixels, y * img.Stride, fb.Address + y * fb.RowBytes, img.Stride);
        return bmp;
    }
}
