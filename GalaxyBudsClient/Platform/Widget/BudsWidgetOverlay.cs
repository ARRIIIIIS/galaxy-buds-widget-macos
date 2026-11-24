using System;
using System.Runtime.InteropServices;
using Serilog;

namespace GalaxyBudsClient.Platform.Widget;

internal static class BudsWidgetOverlay
{
    private const string Dso = "libBudsWidget.dylib";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ActionCallback(int action);

    private static bool _unavailable;
    private static bool _shown;
    private static ActionCallback? _pinnedCallback;

    public static bool IsAvailable => !_unavailable && OperatingSystem.IsMacOS();

    public static void SetActionCallback(Action<int> handler)
    {
        if (!IsAvailable)
            return;

        try
        {
            _pinnedCallback = action => handler(action);
            BudsWidgetSetActionCallback(_pinnedCallback);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning("BudsWidgetOverlay: native library unavailable ({Msg})", ex.Message);
            _unavailable = true;
        }
    }

    public static void Show()
    {
        if (!IsAvailable || _shown)
            return;

        try
        {
            BudsWidgetShow();
            _shown = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning("BudsWidgetOverlay: show failed ({Msg})", ex.Message);
            _unavailable = true;
        }
    }

    public static void Hide()
    {
        if (!_shown)
            return;

        try
        {
            BudsWidgetHide();
            _shown = false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning("BudsWidgetOverlay: hide failed ({Msg})", ex.Message);
            _unavailable = true;
        }
    }

    public static void PlayConnectAnimation(string deviceName, bool isChinese)
    {
        if (!IsAvailable || !_shown)
            return;

        try
        {
            BudsWidgetPlayConnectAnimation(deviceName, isChinese);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning("BudsWidgetOverlay: connect animation failed ({Msg})", ex.Message);
            _unavailable = true;
        }
    }

    public static void UpdateState(
        bool connected,
        string deviceName,
        int battL,
        int battR,
        int battCase,
        bool caseCached,
        int noiseMode,
        bool noiseSupported,
        bool adaptiveSupported,
        bool postureEnabled,
        bool isChinese)
    {
        if (!IsAvailable)
            return;

        try
        {
            BudsWidgetUpdateState(
                connected,
                deviceName,
                battL,
                battR,
                battCase,
                caseCached,
                noiseMode,
                noiseSupported,
                adaptiveSupported,
                postureEnabled,
                isChinese);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning("BudsWidgetOverlay: update failed ({Msg})", ex.Message);
            _unavailable = true;
        }
    }

    [DllImport(Dso, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BudsWidgetSetActionCallback(ActionCallback callback);

    [DllImport(Dso, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BudsWidgetShow();

    [DllImport(Dso, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BudsWidgetHide();

    [DllImport(Dso, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BudsWidgetPlayConnectAnimation(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceName,
        [MarshalAs(UnmanagedType.I1)] bool isChinese);

    [DllImport(Dso, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BudsWidgetUpdateState(
        [MarshalAs(UnmanagedType.I1)] bool connected,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceName,
        int battL,
        int battR,
        int battCase,
        [MarshalAs(UnmanagedType.I1)] bool caseCached,
        int noiseMode,
        [MarshalAs(UnmanagedType.I1)] bool noiseSupported,
        [MarshalAs(UnmanagedType.I1)] bool adaptiveSupported,
        [MarshalAs(UnmanagedType.I1)] bool postureEnabled,
        [MarshalAs(UnmanagedType.I1)] bool isChinese);
}
