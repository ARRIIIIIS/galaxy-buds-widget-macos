using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Message.Decoder;
using GalaxyBudsClient.Message.Parameter;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Platform.Posture;
using Serilog;

namespace GalaxyBudsClient.Platform.Widget;

public sealed class BudsWidgetService
{
    public static BudsWidgetService Instance { get; } = new();

    private bool _initialized;
    private NoiseControlModes? _currentNoiseMode;
    private int _prevBattL = -1;
    private int _prevBattR = -1;
    private int _prevBattCase = -1;
    private bool _budsChargedNotified;
    private bool _caseChargedNotified;

    private BudsWidgetService()
    {
    }

    public void Init()
    {
        if (_initialized)
            return;
        _initialized = true;

        if (!BudsWidgetOverlay.IsAvailable)
            return;

        BudsWidgetOverlay.SetActionCallback(OnNativeAction);

        BluetoothImpl.Instance.Connected += (_, _) =>
        {
            _currentNoiseMode = null;
            Refresh();
            Dispatcher.UIThread.Post(() =>
            {
                if (Settings.Data.DesktopWidgetEnabled)
                {
                    var name = BluetoothImpl.Instance.Device.Current?.Name;
                    BudsWidgetOverlay.PlayConnectAnimation(
                        string.IsNullOrWhiteSpace(name) ? "Galaxy Buds" : name,
                        Settings.Data.Locale == Locales.cn);
                }
            });
        };
        BluetoothImpl.Instance.Disconnected += (_, _) =>
        {
            _currentNoiseMode = null;
            _prevBattL = -1;
            _prevBattR = -1;
            _prevBattCase = -1;
            _budsChargedNotified = false;
            _caseChargedNotified = false;
            Refresh();
        };
        SppMessageReceiver.Instance.StatusUpdate += (_, _) => Refresh();
        SppMessageReceiver.Instance.ExtendedStatusUpdate += (_, e) =>
        {
            _currentNoiseMode = ExtractModeFromEsu(e);
            Refresh();
        };
        SppMessageReceiver.Instance.NoiseControlUpdateResponse += (_, mode) =>
        {
            _currentNoiseMode = mode;
            Refresh();
        };
        SppMessageReceiver.Instance.AncEnabledUpdateResponse += (_, enabled) =>
        {
            _currentNoiseMode = enabled ? NoiseControlModes.NoiseReduction : NoiseControlModes.Off;
            Refresh();
        };
        SppMessageReceiver.Instance.AmbientEnabledUpdateResponse += (_, enabled) =>
        {
            _currentNoiseMode = enabled ? NoiseControlModes.AmbientSound : NoiseControlModes.Off;
            Refresh();
        };
        SppMessageReceiver.Instance.AcknowledgementResponse += (_, ack) =>
        {
            if (ack.Id == MsgIds.NOISE_CONTROLS && ack.Parameters is SimpleAckParameter param)
            {
                _currentNoiseMode = (NoiseControlModes)param.Value;
                Refresh();
            }
            else if (ack.Id is MsgIds.SET_NOISE_REDUCTION or MsgIds.SET_AMBIENT_MODE)
            {
                Refresh();
            }
        };
        EventDispatcher.Instance.EventReceived += (ev, _) =>
        {
            if (ev == Event.UpdateTrayIcon)
                Refresh();
        };
        Settings.MainSettingsPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsData.DesktopWidgetEnabled))
                ApplyVisibilityAndState();
            else if (e.PropertyName is nameof(SettingsData.PostureMonitorEnabled) or nameof(SettingsData.Locale))
                Refresh();
        };

        ApplyVisibilityAndState();
        Log.Information("BudsWidgetService: initialized (enabled={Enabled})", Settings.Data.DesktopWidgetEnabled);
    }

    private static NoiseControlModes ExtractModeFromEsu(ExtendedStatusUpdateDecoder e)
    {
        if (BluetoothImpl.Instance.DeviceSpec.Supports(Features.NoiseControl))
            return e.NoiseControlMode;
        if (e.AmbientSoundEnabled)
            return NoiseControlModes.AmbientSound;
        if (e.NoiseCancelling)
            return NoiseControlModes.NoiseReduction;
        return NoiseControlModes.Off;
    }

    private void ApplyVisibilityAndState()
    {
        if (Settings.Data.DesktopWidgetEnabled)
        {
            Refresh();
            BudsWidgetOverlay.Show();
        }
        else
        {
            BudsWidgetOverlay.Hide();
        }
    }

    public void Refresh()
    {
        if (!BudsWidgetOverlay.IsAvailable)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var connected = BluetoothImpl.Instance.IsConnected;
            var deviceName = BluetoothImpl.Instance.Device.Current?.Name;
            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = "Galaxy Buds";

            var bsu = DeviceMessageCache.Instance.BasicStatusUpdate;
            var battL = bsu?.BatteryL ?? 0;
            var battR = bsu?.BatteryR ?? 0;
            var battCase = 0;
            var caseCached = false;

            if (bsu != null && BluetoothImpl.Instance.DeviceSpec.Supports(Features.CaseBattery))
            {
                if (bsu.BatteryCase is > 0 and <= 100)
                {
                    battCase = bsu.BatteryCase;
                }
                else
                {
                    var cachedCase = DeviceMessageCache.Instance.BasicStatusUpdateWithValidCase?.BatteryCase ?? 0;
                    if (cachedCase is > 0 and <= 100)
                    {
                        battCase = cachedCase;
                        caseCached = true;
                    }
                }
            }

            var spec = BluetoothImpl.Instance.DeviceSpec;
            var noiseSupported = spec.Supports(Features.NoiseControl)
                                 || spec.Supports(Features.Anc)
                                 || spec.Supports(Features.AmbientSound);
            var adaptiveSupported = spec.Supports(Features.NoiseControlAdaptive)
                                    || spec.Device is Models.Buds3Pro or Models.Buds4Pro;

            var mode = GetCurrentNoiseMode();
            var modeCode = mode switch
            {
                NoiseControlModes.AmbientSound => 1,
                NoiseControlModes.NoiseReduction => 2,
                NoiseControlModes.Adaptive => 3,
                _ => 0
            };

            var isChinese = Settings.Data.Locale == Locales.cn;
            BudsWidgetOverlay.UpdateState(
                connected,
                deviceName,
                battL,
                battR,
                battCase,
                caseCached,
                modeCode,
                noiseSupported,
                adaptiveSupported,
                Settings.Data.PostureMonitorEnabled,
                isChinese);

            CheckChargedAlerts(connected, battL, battR, battCase, caseCached, isChinese);
        });
    }

    private void CheckChargedAlerts(bool connected, int battL, int battR, int battCase, bool caseCached, bool isChinese)
    {
        if (!connected || !Settings.Data.DesktopWidgetEnabled)
            return;

        if (battL > 0 && battR > 0)
        {
            if (battL < 98 || battR < 98)
                _budsChargedNotified = false;
            else if (battL == 100 && battR == 100 && !_budsChargedNotified &&
                     _prevBattL > 0 && _prevBattR > 0 && (_prevBattL < 100 || _prevBattR < 100))
            {
                _budsChargedNotified = true;
                BudsWidgetOverlay.PlayChargedAlert(isCase: false, isChinese);
            }

            _prevBattL = battL;
            _prevBattR = battR;
        }

        if (!caseCached && battCase > 0)
        {
            if (battCase < 98)
                _caseChargedNotified = false;
            else if (battCase == 100 && !_caseChargedNotified &&
                     _prevBattCase > 0 && _prevBattCase < 100)
            {
                _caseChargedNotified = true;
                BudsWidgetOverlay.PlayChargedAlert(isCase: true, isChinese);
            }

            _prevBattCase = battCase;
        }
    }

    public NoiseControlModes CurrentNoiseMode => GetCurrentNoiseMode();

    private NoiseControlModes GetCurrentNoiseMode()
    {
        if (_currentNoiseMode.HasValue)
            return _currentNoiseMode.Value;

        var esu = DeviceMessageCache.Instance.ExtendedStatusUpdate;
        if (esu != null)
            return ExtractModeFromEsu(esu);

        return NoiseControlModes.Off;
    }

    private void OnNativeAction(int action)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            switch (action)
            {
                case 0:
                    await SetNoiseModeAsync(NoiseControlModes.Off);
                    break;
                case 1:
                    await SetNoiseModeAsync(NoiseControlModes.AmbientSound);
                    break;
                case 2:
                    await SetNoiseModeAsync(NoiseControlModes.NoiseReduction);
                    break;
                case 3:
                    await SetNoiseModeAsync(NoiseControlModes.Adaptive);
                    break;
                case 4:
                    if (!BluetoothImpl.Instance.IsConnected && BluetoothImpl.HasValidDevice)
                        _ = Task.Run(() => BluetoothImpl.Instance.ConnectAsync());
                    break;
                case 5:
                    Settings.Data.DesktopWidgetEnabled = false;
                    break;
                case 6:
                    Settings.Data.PostureMonitorEnabled = !Settings.Data.PostureMonitorEnabled;
                    break;
                case 7:
                    PostureMonitorService.Instance.Calibrate();
                    break;
            }
        });
    }

    public async Task SetNoiseModeAsync(NoiseControlModes mode)
    {
        _currentNoiseMode = mode;
        Refresh();

        if (BluetoothImpl.Instance.DeviceSpec.Supports(Features.NoiseControl))
        {
            await BluetoothImpl.Instance.SendRequestAsync(MsgIds.NOISE_CONTROLS, (byte)mode);
            EventDispatcher.Instance.Dispatch(Event.UpdateTrayIcon);
        }
        else
        {
            EventDispatcher.Instance.Dispatch(Event.SetNoiseControlState, mode);
        }
    }
}
