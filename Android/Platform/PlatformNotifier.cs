#if __ANDROID__
using Android.App;
using Android.Content;
using Android.OS;
#endif

namespace BMSMonitor.Services;

/// <summary>Android replacement for Windows toast notifications (BMS alerts).</summary>
internal static class PlatformNotifier
{
#if __ANDROID__
    private const string ChannelAlerts   = "bms_alerts";
    private const string ChannelWarnings = "bms_warnings";
    private static int _nextId = 1;

    private static Context Ctx => Android.App.Application.Context;
    private static NotificationManager? Manager =>
        Ctx.GetSystemService(Context.NotificationService) as NotificationManager;

    public static void Register()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && Manager is { } m)
        {
            m.CreateNotificationChannel(new NotificationChannel(ChannelAlerts, "BMS alerts", NotificationImportance.High));
            m.CreateNotificationChannel(new NotificationChannel(ChannelWarnings, "BMS warnings", NotificationImportance.Default));
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            _ = BMSMonitor.Droid.MainActivity.Current?
                .RequestPermissionsAsync([Android.Manifest.Permission.PostNotifications]);
        }
    }

    public static void Show(string title, string body, bool critical)
    {
        try
        {
            var m = Manager;
            if (m is null) return;

            var launch = Ctx.PackageManager?.GetLaunchIntentForPackage(Ctx.PackageName ?? "");
            PendingIntent? pi = launch is null ? null
                : PendingIntent.GetActivity(Ctx, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

#pragma warning disable CA1422
            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(Ctx, critical ? ChannelAlerts : ChannelWarnings)
                : new Notification.Builder(Ctx);
#pragma warning restore CA1422

            builder.SetContentTitle(title)
                   .SetContentText(body)
                   .SetStyle(new Notification.BigTextStyle().BigText(body))
                   .SetSmallIcon(Android.Resource.Drawable.StatSysWarning)
                   .SetAutoCancel(true);
            if (pi is not null) builder.SetContentIntent(pi);

            m.Notify(Interlocked.Increment(ref _nextId), builder.Build());
        }
        catch
        {
            // Notification failed — will retry on next cycle
        }
    }
#else
    public static void Register() { }
    public static void Show(string title, string body, bool critical) { }
#endif
}
