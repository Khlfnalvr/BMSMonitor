using Android.App;
using Android.Content.PM;
using Android.Views;
using AndroidX.Core.View;

namespace BMSMonitor.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    internal static MainActivity? Current { get; private set; }

    private static int _nextRequestCode = 1000;
    private static readonly Dictionary<int, TaskCompletionSource<bool>> _pending = new();

    protected override void OnCreate(Android.OS.Bundle? bundle)
    {
        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);
        base.OnCreate(bundle);
        Current = this;

        // Android 15+ draws apps edge-to-edge: pad the content by the status
        // bar / navigation bar / cutout so the app's top bar and status bar
        // aren't hidden behind the system bars.
        var content = FindViewById(Android.Resource.Id.Content);
        if (content is not null)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarsPadding());
            ViewCompat.RequestApplyInsets(content);
        }
    }

    private sealed class SystemBarsPadding : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(
                WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsetsCompat.Consumed;
        }
    }

    protected override void OnDestroy()
    {
        if (Current == this) Current = null;
        base.OnDestroy();
    }

    /// <summary>Request runtime permissions; resolves true when all are granted.</summary>
    internal Task<bool> RequestPermissionsAsync(string[] permissions)
    {
        var missing = permissions
            .Where(p => CheckSelfPermission(p) != Permission.Granted)
            .ToArray();
        if (missing.Length == 0) return Task.FromResult(true);

        var tcs = new TaskCompletionSource<bool>();
        int code;
        lock (_pending) { code = _nextRequestCode++; _pending[code] = tcs; }
        RequestPermissions(missing, code);
        return tcs.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        TaskCompletionSource<bool>? tcs;
        lock (_pending)
        {
            if (!_pending.Remove(requestCode, out tcs)) return;
        }
        tcs.TrySetResult(grantResults.Length > 0 && grantResults.All(g => g == Permission.Granted));
    }
}
