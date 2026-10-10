using Avalonia;
using System;
using Avalonia.Media;

namespace InstrumentControlWorkbench.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter",
                FontFallbacks =
                [
                    new FontFallback
                    {
                        FontFamily = new FontFamily(OperatingSystem.IsMacOS()
                            ? "Hiragino Sans GB"
                            : OperatingSystem.IsWindows() ? "Microsoft YaHei" : "Noto Sans CJK SC")
                    }
                ]
            })
            .LogToTrace();
}
