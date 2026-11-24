using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Timers;
using GalaxyBudsClient.Message.Decoder;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Platform;
using Serilog;

namespace GalaxyBudsClient.Message;

/*
 * Message receiver for the spatial head-tracking sensor of the earbuds
 *
 * This class is a reverse-engineered implementation of Samsung's client-side head-tracking receiver.
 * It can collect the correct head orientation of the listener as a 4D quaternion in real-time.
 */
public class SpatialSensorManager : IDisposable
{
    public event EventHandler<Quaternion>? NewQuaternionReceived;

    private readonly Timer _keepAliveTimer = new(2000);
    private readonly Timer _retryTimer = new(1000);
    private volatile bool _wantAttached;
    private volatile bool _hasReceivedData;
    private int _retryCount;

    public SpatialSensorManager()
    {
        _keepAliveTimer.Elapsed += KeepAliveOnElapsed;
        _keepAliveTimer.AutoReset = true;

        _retryTimer.Elapsed += RetryOnElapsed;
        _retryTimer.AutoReset = true;

        SppMessageReceiver.Instance.AnyMessageDecoded += OnMessageDecoded;
    }

    private void OnMessageDecoded(object? sender, BaseMessageDecoder? e)
    {
        switch (e)
        {
            case SpatialAudioControlDecoder control:
                switch (control.ResultCode)
                {
                    case SpatialAudioControl.AttachSuccess:
                        _hasReceivedData = true;
                        _retryTimer.Stop();
                        Log.Debug("SpatialSensorManager: Attach successful");
                        break;
                    case SpatialAudioControl.DetachSuccess:
                        Log.Debug("SpatialSensorManager: Detach successful");
                        break;
                }
                break;
            case SpatialAudioDataDecoder data:
                if (data.EventId == SpatialAudioData.BudGrv && data.GrvFloatArray != null)
                {
                    _hasReceivedData = true;
                    _retryTimer.Stop();
                    NewQuaternionReceived?.Invoke(this,
                        new Quaternion(data.GrvFloatArray[0], data.GrvFloatArray[1],
                            data.GrvFloatArray[2], data.GrvFloatArray[3]));
                }
                break;
        }
    }

    public void Dispose()
    {
        SppMessageReceiver.Instance.AnyMessageDecoded -= OnMessageDecoded;
        Detach();
        _keepAliveTimer.Dispose();
        _retryTimer.Dispose();
    }

    public async void Attach()
    {
        _wantAttached = true;
        _hasReceivedData = false;
        _retryCount = 0;

        await SendAttachSequenceAsync();
        if (_wantAttached && !_hasReceivedData)
            _retryTimer.Start();
        _keepAliveTimer.Start();
    }

    public async void Detach()
    {
        _wantAttached = false;
        _hasReceivedData = false;
        _retryTimer.Stop();
        _keepAliveTimer.Stop();

        await BluetoothImpl.Instance.SendRequestAsync(MsgIds.SPATIAL_AUDIO_CONTROL, (byte)SpatialAudioControl.Detach);
        await BluetoothImpl.Instance.SendRequestAsync(MsgIds.SET_SPATIAL_AUDIO, 0);
    }

    private static async Task SendAttachSequenceAsync()
    {
        await BluetoothImpl.Instance.SendRequestAsync(MsgIds.SET_SPATIAL_AUDIO, 1);
        await Task.Delay(80);
        await BluetoothImpl.Instance.SendRequestAsync(MsgIds.SPATIAL_AUDIO_CONTROL, (byte)SpatialAudioControl.Attach);
    }

    private async void RetryOnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_wantAttached || _hasReceivedData || _retryCount >= 4)
        {
            _retryTimer.Stop();
            return;
        }

        _retryCount++;
        Log.Debug("SpatialSensorManager: Retrying attach ({Attempt}/4)...", _retryCount);
        await SendAttachSequenceAsync();
    }

    private static async void KeepAliveOnElapsed(object? sender, ElapsedEventArgs e)
    {
        await BluetoothImpl.Instance.SendRequestAsync(MsgIds.SPATIAL_AUDIO_CONTROL, (byte)SpatialAudioControl.KeepAlive);
    }
}
