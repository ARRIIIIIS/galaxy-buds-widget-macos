using System;
using System.Runtime.InteropServices;
using Serilog;

namespace GalaxyBudsClient.Platform.Posture;

/*
 * Managed wrapper around libPostureOverlay.dylib (GalaxyBudsClient.Platform.OSX/Native/src/PostureOverlay.mm).
 *
 * The native side owns one click-through, borderless window per screen and blurs whatever is behind
 * it. Nothing here throws: if the library or the private CoreGraphics blur entry points are missing,
 * the calls degrade into no-ops so posture detection itself keeps working.
 */
internal static class PostureOverlay
{
    private const string Dso = "libPostureOverlay.dylib";

    private static bool _unavailable;
    private static bool _shown;
    private static int _lastRadius = -1;
    private static bool _hasNativeBlurLogged;

    /// <summary>
    /// True when the blur overlay can be used at all. Returns false on non-macOS platforms and
    /// after the first failed native call.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_unavailable || !OperatingSystem.IsMacOS())
                return false;

            try
            {
                // Even without the private CoreGraphics entry points the native side can fall back to
                // fading its material in, so loading the library is enough to consider it available.
                if (!_hasNativeBlurLogged)
                {
                    _hasNativeBlurLogged = true;
                    if (PostureOverlayHasNativeBlur() == 0)
                        Log.Warning("PostureOverlay: private blur API not found, falling back to material fade");
                }

                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Warning("PostureOverlay: native library unavailable ({Msg})", ex.Message);
                _unavailable = true;
                return false;
            }
        }
    }

    public static void Show()
    {
        if (!IsAvailable || _shown)
            return;

        try
        {
            PostureOverlayShow();
            _shown = true;
            _lastRadius = -1;
        }
        catch (Exception ex)
        {
            Log.Warning("PostureOverlay: show failed ({Msg})", ex.Message);
            _unavailable = true;
        }
    }

    public static void Hide()
    {
        if (!_shown)
            return;

        try
        {
            PostureOverlaySetRadius(0);
            PostureOverlayHide();
        }
        catch (Exception ex)
        {
            Log.Warning("PostureOverlay: hide failed ({Msg})", ex.Message);
        }
        finally
        {
            _shown = false;
            _lastRadius = -1;
        }
    }

    /// <summary>
    /// Sets the blur radius in points (0 = fully transparent, ~64 = heavy blur).
    /// Repeated values are skipped so the native side is not hammered every frame.
    /// </summary>
    public static void SetRadius(int radius)
    {
        if (!_shown || radius == _lastRadius)
            return;

        try
        {
            PostureOverlaySetRadius(radius);
            _lastRadius = radius;
        }
        catch (Exception ex)
        {
            Log.Warning("PostureOverlay: set radius failed ({Msg})", ex.Message);
            _unavailable = true;
        }
    }

    public static void RefreshScreens()
    {
        if (!_shown)
            return;

        try
        {
            PostureOverlayRefreshScreens();
            _lastRadius = -1;
        }
        catch (Exception ex)
        {
            Log.Warning("PostureOverlay: refresh failed ({Msg})", ex.Message);
        }
    }

    [DllImport(Dso)]
    private static extern void PostureOverlayShow();

    [DllImport(Dso)]
    private static extern void PostureOverlayHide();

    [DllImport(Dso)]
    private static extern void PostureOverlaySetRadius(int radius);

    [DllImport(Dso)]
    private static extern void PostureOverlayRefreshScreens();

    [DllImport(Dso)]
    private static extern int PostureOverlayHasNativeBlur();
}
