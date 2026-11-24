using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentIcons.Common;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Platform;
using GalaxyBudsClient.Platform.Widget;
using Serilog;

namespace GalaxyBudsClient.Utils.Interface;

internal class TrayManager
{
    private bool _allowUpdate = true;
    private bool _missedUpdate;

    private TrayManager()
    {
        (Application.Current as App)!.TrayMenu.Opening += (_, _) => _allowUpdate = false;
        (Application.Current as App)!.TrayMenu.Closed += (_, _) =>
        {
            _allowUpdate = true;
            if (_missedUpdate)
                _ = RebuildAsync();
        };
        (Application.Current as App)!.TrayMenu.NeedsUpdate += (_, _) => _ = RebuildAsync();

        if (Application.Current?.PlatformSettings is { } platformSettings)
            platformSettings.ColorValuesChanged += (_, _) => _ = RebuildAsync();
    }

    private static Bitmap? Icon(Symbol symbol) => PlatformUtils.IsOSX ? TrayMenuIcons.Glyph(symbol) : null;

    private void OnTrayMenuCommand(object? type)
    {
        if (type is not TrayItemTypes e)
        {
            Log.Error("TrayManager.OnTrayMenuCommand: Unknown item type: {Type}", type);
            return;
        }

        switch (e)
        {
            case TrayItemTypes.Open:
                WebSettingsServer.Instance.OpenInBrowser();
                break;
            case TrayItemTypes.Restart:
                Log.Information("TrayManager: Restart requested by user");
                try
                {
                    var appPath = "/Applications/Galaxy Buds Manager.app";
                    if (System.IO.Directory.Exists(appPath))
                        Process.Start(new ProcessStartInfo("open", $"-n \"{appPath}\"") { UseShellExecute = false });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "TrayManager: failed to spawn restart process");
                }
                ShutdownApp();
                break;
            case TrayItemTypes.Quit:
                Log.Information("TrayManager: Exit requested by user");
                ShutdownApp();
                break;
        }
    }

    private static void ShutdownApp()
    {
        if (Application.Current?.ApplicationLifetime is IControlledApplicationLifetime lifetime)
            lifetime.Shutdown();
        else
            Environment.Exit(0);
    }

    public async Task RebuildAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_allowUpdate)
            {
                _missedUpdate = true;
                return;
            }
            _missedUpdate = false;

            var items = new List<NativeMenuItemBase>
            {
                new NativeMenuItem("设置向导...")
                {
                    Icon = Icon(Symbol.Globe),
                    Command = new MiniCommand(OnTrayMenuCommand),
                    CommandParameter = TrayItemTypes.Open
                },
                new NativeMenuItemSeparator(),
                new NativeMenuItem("重启")
                {
                    Icon = Icon(Symbol.ArrowSync),
                    Command = new MiniCommand(OnTrayMenuCommand),
                    CommandParameter = TrayItemTypes.Restart
                },
                new NativeMenuItem(Strings.TrayQuit)
                {
                    Icon = Icon(Symbol.Power),
                    Command = new MiniCommand(OnTrayMenuCommand),
                    CommandParameter = TrayItemTypes.Quit
                }
            };

            (Application.Current as App)?.TrayMenu.Items.Clear();
            foreach (var item in items)
                (Application.Current as App)?.TrayMenu.Items.Add(item);
        }, DispatcherPriority.Normal);
    }

    #region Singleton
    private static readonly object Padlock = new();
    private static TrayManager? _instance;
    public static TrayManager Instance
    {
        get
        {
            lock (Padlock)
            {
                return _instance ??= new TrayManager();
            }
        }
    }

    public static void Init()
    {
        lock (Padlock)
        {
            _instance ??= new TrayManager();
        }
    }
    #endregion
}
