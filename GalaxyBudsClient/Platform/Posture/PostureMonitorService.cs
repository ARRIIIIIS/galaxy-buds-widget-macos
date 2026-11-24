using System;
using System.ComponentModel;
using System.Numerics;
using Avalonia.Threading;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Utils.Extensions;
using Serilog;

namespace GalaxyBudsClient.Platform.Posture;

public enum PostureMonitorState
{
    /// <summary>Monitoring is switched off in the settings.</summary>
    Stopped,
    /// <summary>Monitoring is on but the earbuds are not connected.</summary>
    WaitingForDevice,
    /// <summary>The connected earbuds have no motion sensor we can read.</summary>
    Unsupported,
    /// <summary>Orientation packages are arriving.</summary>
    Tracking
}

/*
 * Posture detection driven by the head-tracking IMU of the connected Galaxy Buds.
 *
 * The earbuds stream their orientation as a quaternion while "spatial audio" is attached
 * (see SpatialSensorManager). Looking down lowers the pitch angle, so a forward lean shows up as a
 * negative delta from the calibrated neutral angle. Once the delta passes the trigger angle the
 * screen behind everything gets progressively blurred via libPostureOverlay.
 *
 * The blur ramp runs on its own dispatcher timer instead of reacting to raw samples: samples arrive
 * at an irregular rate (roughly 4/s), which would make the blur visibly stutter.
 */
public sealed class PostureMonitorService : IDisposable
{
    private const double MaxRadius = 64.0;
    // Excess angle past the trigger angle at which the blur is fully opaque (~17°)
    private const double FullSeverityExcessRad = 0.30;
    // Ramp speed in radius units per 33 ms tick (≈60/s up, ≈180/s down)
    private const double RisePerTick = 2.0;
    private const double FallPerTick = 6.0;
    // Number of samples averaged before the neutral angle is captured automatically
    private const int AutoCalibrationSamples = 12;

    public static PostureMonitorService Instance { get; } = new();

    private readonly SpatialSensorManager _sensor = new();
    private readonly DispatcherTimer _timer;
    private readonly object _sync = new();

    private double _pitch, _roll, _yaw;
    private bool _hasSample;
    private double _currentRadius;
    private int _autoCalibrationCounter;
    private bool _initialized;

    private PostureMonitorService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
    }

    /// <summary>Raised on the UI thread after every ramp step so pages can refresh their readout.</summary>
    public event EventHandler? Updated;

    public PostureMonitorState State { get; private set; } = PostureMonitorState.Stopped;
    public bool IsTracking => State == PostureMonitorState.Tracking;

    /// <summary>Smoothed head pitch in degrees. Positive looks up, negative looks down.</summary>
    public double PitchDegrees => _pitch * 180.0 / Math.PI;

    /// <summary>Smoothed head roll in degrees.</summary>
    public double RollDegrees => _roll * 180.0 / Math.PI;

    /// <summary>Smoothed head yaw in degrees.</summary>
    public double YawDegrees => _yaw * 180.0 / Math.PI;

    /// <summary>Angle below the neutral position, in degrees. Only meaningful once calibrated.</summary>
    public double DeltaDegrees =>
        (Settings.Data.HasPostureBaseline ? _pitch - Settings.Data.PostureBaselinePitch : 0) * 180.0 / Math.PI;

    /// <summary>0 = upright, 1 = maximum slouch. Drives the blur strength.</summary>
    public double Severity { get; private set; }

    public bool IsBadPosture { get; private set; }

    /// <summary>Blur radius currently applied (0 - 64).</summary>
    public int CurrentBlurRadius => (int)Math.Round(_currentRadius);

    public bool HasReading
    {
        get { lock (_sync) return _hasSample; }
    }

    /// <summary>
    /// Wires the service to the settings and to the Bluetooth connection state. Safe to call more
    /// than once; only the first call has an effect.
    /// </summary>
    public void Init()
    {
        if (_initialized)
            return;
        _initialized = true;

        MigrateSettings();

        Settings.MainSettingsPropertyChanged += OnSettingsChanged;
        BluetoothImpl.Instance.Connected += OnConnected;
        BluetoothImpl.Instance.Disconnected += OnDisconnected;

        ApplySettings();
    }

    /// <summary>
    /// Old settings.json files carry the first-shot defaults (12° trigger, 70% linear strength),
    /// which made the blur feel late and then all at once. Reset them once to the Dorso-matching
    /// defaults; user-made changes after the migration are respected.
    /// </summary>
    private void MigrateSettings()
    {
        if (Settings.Data.PostureSettingsVersion >= 2)
            return;

        Settings.Data.PostureTriggerAngle = 9;
        Settings.Data.PostureBlurIntensity = 50;
        Settings.Data.PostureSettingsVersion = 2;
        Log.Information("PostureMonitor: migrated settings to v2 (trigger 9°, curve strength 50)");
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsData.PostureMonitorEnabled))
            ApplySettings();
    }

    private void OnConnected(object? sender, EventArgs e) => ApplySettings();

    private void OnDisconnected(object? sender, string reason) => ApplySettings();

    /// <summary>Starts or stops monitoring so that it matches the current settings and connection.</summary>
    private void ApplySettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!Settings.Data.PostureMonitorEnabled)
            {
                StopTracking();
                State = PostureMonitorState.Stopped;
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }

            var connected = BluetoothImpl.Instance.IsConnected &&
                            BluetoothImpl.Instance.DeviceSpec.Supports(Features.SpatialSensor);
            if (!connected)
            {
                StopTracking();
                State = BluetoothImpl.Instance.IsConnected
                    ? PostureMonitorState.Unsupported
                    : PostureMonitorState.WaitingForDevice;
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }

            StartTracking();
        });
    }

    private void StartTracking()
    {
        if (IsTracking)
            return;

        _sensor.NewQuaternionReceived += OnQuaternionReceived;
        _sensor.Attach();

        lock (_sync)
        {
            _hasSample = false;
            _autoCalibrationCounter = 0;
        }

        _currentRadius = 0;
        Severity = 0;
        IsBadPosture = false;

        if (PostureOverlay.IsAvailable)
            PostureOverlay.Show();

        _timer.Start();
        State = PostureMonitorState.Tracking;
        Log.Information("PostureMonitor: tracking started");
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void StopTracking()
    {
        if (!IsTracking)
            return;

        _timer.Stop();
        _sensor.NewQuaternionReceived -= OnQuaternionReceived;
        _sensor.Detach();
        PostureOverlay.Hide();

        _currentRadius = 0;
        Severity = 0;
        IsBadPosture = false;
        lock (_sync)
            _hasSample = false;

        State = PostureMonitorState.WaitingForDevice;
        Log.Information("PostureMonitor: tracking stopped");
    }

    /// <summary>
    /// Uses the current head orientation as the neutral position. Call this while sitting upright.
    /// </summary>
    public bool Calibrate()
    {
        lock (_sync)
        {
            if (!_hasSample)
                return false;

            Settings.Data.PostureBaselinePitch = _pitch;
        }

        Settings.Data.HasPostureBaseline = true;
        Log.Information("PostureMonitor: calibrated at {Pitch:F2}°", PitchDegrees);
        Updated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnQuaternionReceived(object? sender, Quaternion q)
    {
        var (roll, pitch, yaw) = q.ToRollPitchYaw();

        lock (_sync)
        {
            if (!_hasSample)
            {
                _pitch = pitch;
                _roll = roll;
                _yaw = yaw;
                _hasSample = true;
                return;
            }

            // Exponential smoothing; raw sensor output is noticeably jittery.
            const double alpha = 0.50;
            _pitch += (pitch - _pitch) * alpha;
            _roll += (roll - _roll) * alpha;
            _yaw += (yaw - _yaw) * alpha;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double pitch;
        lock (_sync)
        {
            if (!_hasSample)
                return;
            pitch = _pitch;
        }

        // Capture a neutral angle automatically the first time the buds stream data, so the feature
        // does something useful before the user ever opens the page. Explicit calibration overrides it.
        if (!Settings.Data.HasPostureBaseline)
        {
            if (++_autoCalibrationCounter >= AutoCalibrationSamples)
            {
                _autoCalibrationCounter = 0;
                Calibrate();
            }
            return;
        }

        var baseline = Settings.Data.PostureBaselinePitch;
        var diff = pitch - baseline;
        var triggerRad = Math.Max(2, Settings.Data.PostureTriggerAngle) * Math.PI / 180.0;

        IsBadPosture = diff < -triggerRad;
        Severity = IsBadPosture
            ? Math.Min(1.0, (Math.Abs(diff) - triggerRad) / FullSeverityExcessRad)
            : 0.0;

        // Dorso-style intensity curve: pow(severity, 1/intensity). A higher setting bends the curve
        // so the blur ramps up early and smoothly with the angle instead of appearing late and
        // jumping to full strength. 0 -> linear, 50 -> sqrt ("strong" in Dorso), 100 -> cubic root.
        var intensity = 1.0 + Math.Clamp(Settings.Data.PostureBlurIntensity, 0, 100) / 50.0;
        var shaped = Math.Pow(Severity, 1.0 / intensity);

        var target = shaped * MaxRadius;

        _currentRadius = target > _currentRadius
            ? Math.Min(target, _currentRadius + RisePerTick)
            : Math.Max(target, _currentRadius - FallPerTick);

        PostureOverlay.SetRadius((int)Math.Round(_currentRadius));
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        StopTracking();
        _sensor.Dispose();
    }
}
