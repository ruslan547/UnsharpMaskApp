using Avalonia;

namespace UnsharpMaskApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench") return Bench.RunBenchmark(args[1..]).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--stress") return Bench.RunStress(args[1..]).GetAwaiter().GetResult();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
