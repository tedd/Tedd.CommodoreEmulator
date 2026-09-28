using Microsoft.Extensions.Logging;
using Tedd.Maui;

namespace Tedd.Commodore.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            // The emulator picture: a GPU-backed Skia surface that takes a new 384 x 272 frame 50 times a
            // second and magnifies it with nearest neighbour sampling. Works on every MAUI head as it stands.
            .UseTeddWriteableBitmap();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
