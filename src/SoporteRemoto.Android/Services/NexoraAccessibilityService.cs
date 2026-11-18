using Android.App;
using Android.AccessibilityServices;
using Android.Graphics;
using Android.OS;
using Android.Views.Accessibility;

namespace SoporteRemoto.Android.Services;

[Service(
    Exported = true,
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    Label = "Nexora · control remoto")]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
public sealed class NexoraAccessibilityService : AccessibilityService
{
    private static readonly Handler MainHandler = new(Looper.MainLooper!);

    public static NexoraAccessibilityService? Instance { get; private set; }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Instance = this;
        AndroidRemoteInput.Attach(this);
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
    }

    public override void OnInterrupt()
    {
    }

    public override void OnDestroy()
    {
        AndroidRemoteInput.Detach();
        Instance = null;
        base.OnDestroy();
    }

    public bool Tap(int x, int y) => Dispatch(x, y, x, y, 60);

    public bool LongPress(int x, int y) => Dispatch(x, y, x, y, 800);

    public bool Swipe(int x1, int y1, int x2, int y2, int durationMs) =>
        Dispatch(x1, y1, x2, y2, durationMs);

    private bool Dispatch(int x1, int y1, int x2, int y2, int durationMs)
    {
        try
        {
            var path = new global::Android.Graphics.Path();
            path.MoveTo(x1, y1);
            path.LineTo(x2, y2);

            var stroke = new GestureDescription.StrokeDescription(
                path, 0, Math.Clamp(durationMs, 40, 2000));
            var gesture = new GestureDescription.Builder().AddStroke(stroke).Build();
            if (gesture is null) return false;

            MainHandler.Post(() =>
            {
                try { DispatchGesture(gesture, null, null); } catch { }
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool RunGlobalAction(int action)
    {
        try
        {
            var global = action switch
            {
                1 => GlobalAction.Back,
                2 => GlobalAction.Home,
                3 => GlobalAction.Recents,
                _ => GlobalAction.Back,
            };
            return PerformGlobalAction(global);
        }
        catch
        {
            return false;
        }
    }

    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            var root = RootInActiveWindow;
            if (root is null) return false;

            var focus = root.FindFocus(NodeFocus.Input)
                        ?? root.FindFocus(NodeFocus.Accessibility);
            if (focus is null) return false;

            var existing = focus.Text ?? string.Empty;
            var args = new Bundle();
            args.PutCharSequence(
                AccessibilityNodeInfo.ActionArgumentSetTextCharsequence!,
                existing + text);

            return focus.PerformAction(global::Android.Views.Accessibility.Action.SetText, args);
        }
        catch
        {
            return false;
        }
    }
}
