// Desktop (Skia) head — only used for a quick local compile/run check of the
// shared UI (build with -p:BmsDesktopCheck=true). Not shipped.
using Uno.UI.Hosting;

namespace BMSMonitor;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();
        host.Run();
    }
}
