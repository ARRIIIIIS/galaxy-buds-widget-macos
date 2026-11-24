using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Message.Encoder;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Platform.Posture;
using Serilog;

namespace GalaxyBudsClient.Platform.Widget;

public sealed class WebSettingsServer
{
    public static WebSettingsServer Instance { get; } = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private static bool? _optimisticSeamless;
    private static bool? _optimisticEqEnabled;
    private static bool? _optimisticEqCustom;
    private static int? _optimisticEqPreset;
    private static int[]? _optimisticEqBands;

    private static readonly (string id, string label, string[] variants)[] SamsungModels =
    [
        ("Buds4Pro", "Galaxy Buds4 Pro", ["Buds4ProBlack", "Buds4ProWhite", "Buds4ProApricot"]),
        ("Buds4", "Galaxy Buds4", ["Buds4Black", "Buds4White"]),
        ("Buds3Pro", "Galaxy Buds3 Pro", ["Buds3ProSilver", "Buds3ProWhite"]),
        ("Buds3", "Galaxy Buds3", ["Buds3Silver", "Buds3White"]),
        ("Buds3Fe", "Galaxy Buds3 FE", ["Buds3FeSilver", "Buds3FeWhite"]),
        ("Buds2Pro", "Galaxy Buds2 Pro", ["Buds2ProGrey", "Buds2ProWhite", "Buds2ProViolet"]),
        ("Buds2", "Galaxy Buds2", ["Buds2Grey", "Buds2Black", "Buds2Green", "Buds2Violet"]),
        ("BudsPro", "Galaxy Buds Pro", ["BudsProBlack", "BudsProSilver", "BudsProViolet"]),
        ("BudsLive", "Galaxy Buds Live", ["BudsLiveBronze", "BudsLiveBlack", "BudsLiveWhite"]),
        ("BudsPlus", "Galaxy Buds+", ["BudsPlusBlack", "BudsPlusWhite", "BudsPlusBlue"]),
        ("Buds", "Galaxy Buds", ["BudsBlack", "BudsWhite", "BudsYellow"])
    ];

    private WebSettingsServer()
    {
    }

    public string Url => $"http://127.0.0.1:{_port}/";

    public void OpenInBrowser()
    {
        EnsureStarted();
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebSettingsServer: failed to open browser");
        }
    }

    public void EnsureStarted()
    {
        if (_listener != null)
            return;

        _port = FindFreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add(Url);
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(listener, _cts.Token));
        Log.Information("WebSettingsServer: listening on {Url}", Url);
    }

    private static int FindFreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ((IPEndPoint)tcp.LocalEndpoint).Port;
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebSettingsServer: accept error");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers.Add("Cache-Control", "no-store");

        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            if (req.HttpMethod == "GET" && path == "/")
            {
                await WriteResponseAsync(res, 200, "text/html; charset=utf-8", HtmlPage);
                return;
            }

            if (req.HttpMethod == "GET" && path == "/api/render")
            {
                var variant = req.QueryString["variant"] ?? "Buds3ProSilver";
                var side = req.QueryString["side"] == "1" ? "1" : "0";
                await ServeRenderAsync(res, variant, side);
                return;
            }

            if (req.HttpMethod == "GET" && path == "/api/state")
            {
                var json = await BuildStateJsonAsync();
                await WriteResponseAsync(res, 200, "application/json; charset=utf-8", json);
                return;
            }

            if (req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                using var doc = string.IsNullOrWhiteSpace(body)
                    ? JsonDocument.Parse("{}")
                    : JsonDocument.Parse(body);
                var root = doc.RootElement;

                switch (path)
                {
                    case "/api/connect":
                        await HandleConnectAsync(root);
                        break;
                    case "/api/disconnect":
                        await BluetoothImpl.Instance.DisconnectAsync();
                        break;
                    case "/api/noise":
                        if (root.TryGetProperty("mode", out var modeProp) && modeProp.TryGetInt32(out var modeInt))
                        {
                            var mode = modeInt switch
                            {
                                1 => NoiseControlModes.AmbientSound,
                                2 => NoiseControlModes.NoiseReduction,
                                3 => NoiseControlModes.Adaptive,
                                _ => NoiseControlModes.Off
                            };
                            await Dispatcher.UIThread.InvokeAsync(() => BudsWidgetService.Instance.SetNoiseModeAsync(mode));
                        }
                        break;
                    case "/api/seamless":
                        if (root.TryGetProperty("enabled", out var seamProp))
                        {
                            var en = seamProp.GetBoolean();
                            _optimisticSeamless = en;
                            // Samsung protocol: 0 = enabled, 1 = disabled
                            await BluetoothImpl.Instance.SendRequestAsync(MsgIds.SET_SEAMLESS_CONNECTION, !en);
                        }
                        break;
                    case "/api/multipoint":
                        if (root.TryGetProperty("auto", out var autoProp))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                Settings.Data.AutoEnableMultipoint = autoProp.GetBoolean();
                            });
                        }
                        if (root.TryGetProperty("apply", out var applyProp) && applyProp.GetBoolean())
                        {
                            var ok = await MultipointPatcher.ApplyAsync();
                            var msg = ok ? "多点连接已解锁" : "解锁失败，请确认耳机已连接";
                            await WriteResponseAsync(res, 200, "application/json; charset=utf-8",
                                $"{{\"ok\":{(ok ? "true" : "false")},\"message\":\"{msg}\"}}");
                            return;
                        }
                        break;
                    case "/api/posture":
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (root.TryGetProperty("enabled", out var en))
                                Settings.Data.PostureMonitorEnabled = en.GetBoolean();
                            if (root.TryGetProperty("triggerAngle", out var ta) && ta.TryGetInt32(out var taVal))
                                Settings.Data.PostureTriggerAngle = Math.Clamp(taVal, 2, 30);
                            if (root.TryGetProperty("blurIntensity", out var bi) && bi.TryGetInt32(out var biVal))
                                Settings.Data.PostureBlurIntensity = Math.Clamp(biVal, 0, 100);
                            if (root.TryGetProperty("calibrate", out var cal) && cal.GetBoolean())
                                PostureMonitorService.Instance.Calibrate();
                        });
                        break;
                    case "/api/widget":
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (root.TryGetProperty("enabled", out var en))
                                Settings.Data.DesktopWidgetEnabled = en.GetBoolean();
                        });
                        break;
                    case "/api/equalizer":
                        await HandleEqualizerAsync(root);
                        break;
                    case "/api/lang":
                        if (root.TryGetProperty("lang", out var langProp) && langProp.ValueKind == JsonValueKind.String)
                        {
                            var langStr = langProp.GetString();
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                Settings.Data.Locale = langStr == "zh" ? Locales.cn : Locales.en;
                            });
                        }
                        break;
                    default:
                        await WriteResponseAsync(res, 404, "application/json", "{\"error\":\"not_found\"}");
                        return;
                }

                var updated = await BuildStateJsonAsync();
                await WriteResponseAsync(res, 200, "application/json; charset=utf-8", updated);
                return;
            }

            await WriteResponseAsync(res, 404, "text/plain", "Not Found");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebSettingsServer: request failed");
            try
            {
                await WriteResponseAsync(res, 500, "application/json", "{\"error\":\"internal_error\"}");
            }
            catch
            {
                // Ignore secondary write error
            }
        }
    }

    private static async Task ServeRenderAsync(HttpListenerResponse res, string variant, string side)
    {
        // Sanitize to alphanumeric only
        var safeVariant = new string(variant.Where(char.IsLetterOrDigit).ToArray());
        var fileName = $"{safeVariant}-{side}.png";

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Device", "Realistic", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Resources", "Device", "Realistic", fileName))
        };

        var found = candidates.FirstOrDefault(File.Exists);
        if (found == null)
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        var bytes = await File.ReadAllBytesAsync(found);
        res.StatusCode = 200;
        res.ContentType = "image/png";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task HandleEqualizerAsync(JsonElement root)
    {
        var enabled = root.TryGetProperty("enabled", out var en) && en.GetBoolean();
        var isCustom = root.TryGetProperty("isCustom", out var cu) && cu.GetBoolean();
        var preset = root.TryGetProperty("preset", out var pr) && pr.TryGetInt32(out var pv)
            ? Math.Clamp(pv, 0, 4)
            : 0;

        int[]? bands = null;
        if (root.TryGetProperty("bands", out var bArr) && bArr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<int>();
            foreach (var item in bArr.EnumerateArray())
            {
                if (item.TryGetInt32(out var v))
                    list.Add(Math.Clamp(v, -10, 10));
            }
            if (list.Count == 9)
                bands = [.. list];
        }

        _optimisticEqEnabled = enabled || isCustom;
        _optimisticEqCustom = isCustom;
        _optimisticEqPreset = preset;

        if (isCustom)
        {
            var finalBands = bands ?? _optimisticEqBands ?? BluetoothImpl.Instance.Device.Current?.CustomEqualizerBands ?? [0, 0, 0, 0, 0, 0, 0, 0, 0];
            if (finalBands.Length != 9)
                finalBands = [0, 0, 0, 0, 0, 0, 0, 0, 0];
            _optimisticEqBands = finalBands;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dev = BluetoothImpl.Instance.Device.Current;
                if (dev != null)
                {
                    dev.CustomEqualizerBands = finalBands;
                    dev.CustomEqualizerEnabled = true;
                }
            });

            await BluetoothImpl.Instance.SendAsync(new SetCustomEqualizerEncoder
            {
                BandGains = finalBands.Select(x => (sbyte)x).ToArray()
            });
            await BluetoothImpl.Instance.SendAsync(new SetEqualizerEncoder
            {
                IsEnabled = true,
                Preset = 6
            });
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dev = BluetoothImpl.Instance.Device.Current;
                if (dev != null)
                    dev.CustomEqualizerEnabled = false;
            });

            await BluetoothImpl.Instance.SendAsync(new SetEqualizerEncoder
            {
                IsEnabled = enabled,
                Preset = preset
            });
        }
    }

    private static async Task HandleConnectAsync(JsonElement root)
    {
        var requestedModel = Models.Buds3Pro;
        if (root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
        {
            if (Enum.TryParse<Models>(modelProp.GetString(), true, out var parsed) && parsed != Models.NULL)
                requestedModel = parsed;
        }

        if (root.TryGetProperty("mac", out var macProp) && macProp.ValueKind == JsonValueKind.String)
        {
            var mac = macProp.GetString();
            if (!string.IsNullOrWhiteSpace(mac))
            {
                var devices = await BluetoothImpl.Instance.GetDevicesAsync();
                var target = devices.FirstOrDefault(d => string.Equals(d.Address, mac, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    Device? devToConnect = null;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var existing = Settings.Data.Devices.FirstOrDefault(d =>
                            string.Equals(d.MacAddress, target.Address, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            existing = new Device
                            {
                                MacAddress = target.Address,
                                Name = target.Name,
                                Model = requestedModel
                            };
                            Settings.Data.Devices.Add(existing);
                        }
                        else
                        {
                            existing.Model = requestedModel;
                        }
                        Settings.Data.LastDeviceMac = existing.MacAddress;
                        devToConnect = existing;
                    });
                    if (devToConnect != null)
                        await BluetoothImpl.Instance.ConnectAsync(devToConnect);
                    return;
                }
            }
        }

        if (!BluetoothImpl.Instance.IsConnected && BluetoothImpl.HasValidDevice)
            await BluetoothImpl.Instance.ConnectAsync();
    }

    private static async Task<string> BuildStateJsonAsync()
    {
        var paired = (await BluetoothImpl.Instance.GetDevicesAsync())
            .Select(d => new { name = d.Name, mac = d.Address, connected = d.IsConnected })
            .ToArray();

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var connected = BluetoothImpl.Instance.IsConnected;
            var currentDev = BluetoothImpl.Instance.Device.Current;
            var deviceName = currentDev?.Name ?? "Galaxy Buds";
            var mac = currentDev?.MacAddress ?? Settings.Data.LastDeviceMac ?? "";
            var model = currentDev?.Model ?? BluetoothImpl.Instance.DeviceSpec.Device;
            if (model == Models.NULL)
                model = Models.Buds3Pro;

            var bsu = DeviceMessageCache.Instance.BasicStatusUpdate;
            var battL = connected ? (bsu?.BatteryL ?? 0) : 0;
            var battR = connected ? (bsu?.BatteryR ?? 0) : 0;
            var battCase = 0;
            var caseCached = false;
            if (connected && bsu != null && BluetoothImpl.Instance.DeviceSpec.Supports(Features.CaseBattery))
            {
                if (bsu.BatteryCase is > 0 and <= 100)
                {
                    battCase = bsu.BatteryCase;
                }
                else
                {
                    var cached = DeviceMessageCache.Instance.BasicStatusUpdateWithValidCase?.BatteryCase ?? 0;
                    if (cached is > 0 and <= 100)
                    {
                        battCase = cached;
                        caseCached = true;
                    }
                }
            }

            var noiseMode = BudsWidgetService.Instance.CurrentNoiseMode switch
            {
                NoiseControlModes.AmbientSound => 1,
                NoiseControlModes.NoiseReduction => 2,
                NoiseControlModes.Adaptive => 3,
                _ => 0
            };

            var esu = DeviceMessageCache.Instance.ExtendedStatusUpdate;
            var seamlessSupported = BluetoothImpl.Instance.DeviceSpec.Supports(Features.SeamlessConnection);
            var seamlessEnabled = _optimisticSeamless ?? (esu?.SeamlessConnectionEnabled ?? false);
            if (esu != null && _optimisticSeamless.HasValue && esu.SeamlessConnectionEnabled == _optimisticSeamless.Value)
                _optimisticSeamless = null;

            var posture = PostureMonitorService.Instance;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("lang", Settings.Data.Locale == Locales.cn ? "zh" : "en");
                w.WriteBoolean("connected", connected);
                w.WriteString("deviceName", deviceName);
                w.WriteString("mac", mac);
                w.WriteString("model", model.ToString());

                w.WriteStartArray("models");
                foreach (var m in SamsungModels)
                {
                    w.WriteStartObject();
                    w.WriteString("id", m.id);
                    w.WriteString("label", m.label);
                    w.WriteStartArray("variants");
                    foreach (var v in m.variants)
                        w.WriteStringValue(v);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("pairedDevices");
                foreach (var d in paired)
                {
                    w.WriteStartObject();
                    w.WriteString("name", d.name);
                    w.WriteString("mac", d.mac);
                    w.WriteBoolean("connected", d.connected);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartObject("battery");
                w.WriteNumber("l", battL);
                w.WriteNumber("r", battR);
                w.WriteNumber("case", battCase);
                w.WriteBoolean("caseCached", caseCached);
                w.WriteEndObject();

                w.WriteNumber("noiseMode", noiseMode);

                w.WriteStartObject("connectivity");
                w.WriteBoolean("seamlessSupported", seamlessSupported);
                w.WriteBoolean("seamlessEnabled", seamlessEnabled);
                w.WriteBoolean("multipointSupported", MultipointPatcher.IsSupportedByCurrentDevice);
                w.WriteBoolean("autoMultipoint", Settings.Data.AutoEnableMultipoint);
                w.WriteEndObject();

                w.WriteStartObject("posture");
                w.WriteBoolean("enabled", Settings.Data.PostureMonitorEnabled);
                w.WriteBoolean("hasBaseline", Settings.Data.HasPostureBaseline);
                w.WriteNumber("triggerAngle", Settings.Data.PostureTriggerAngle);
                w.WriteNumber("blurIntensity", Settings.Data.PostureBlurIntensity);
                w.WriteNumber("pitchDeg", Math.Round(posture.PitchDegrees, 1));
                w.WriteNumber("deltaDeg", Math.Round(posture.DeltaDegrees, 1));
                w.WriteBoolean("isBad", posture.IsBadPosture);
                w.WriteString("state", posture.State.ToString());
                w.WriteEndObject();

                w.WriteStartObject("widget");
                w.WriteBoolean("enabled", Settings.Data.DesktopWidgetEnabled);
                w.WriteEndObject();

                var eqCustomSupported = BluetoothImpl.Instance.DeviceSpec.Supports(Features.CustomEqualizer);
                var eqBands = _optimisticEqBands ?? currentDev?.CustomEqualizerBands ?? [0, 0, 0, 0, 0, 0, 0, 0, 0];
                if (eqBands.Length != 9)
                    eqBands = [0, 0, 0, 0, 0, 0, 0, 0, 0];

                var esuEqMode = esu?.EqualizerMode ?? 0;
                var esuEqEnabled = esu != null && (BluetoothImpl.Instance.CurrentModel == Models.Buds
                    ? esu.EqualizerEnabled
                    : esuEqMode != 0);
                var esuIsCustom = esuEqMode == 7 || (currentDev?.CustomEqualizerEnabled ?? false);
                var esuPreset = Math.Clamp(esuEqMode > 0 ? esuEqMode - 1 : 0, 0, 4);

                var eqEnabled = _optimisticEqEnabled ?? esuEqEnabled;
                var eqCustom = _optimisticEqCustom ?? esuIsCustom;
                var eqPreset = _optimisticEqPreset ?? esuPreset;

                w.WriteStartObject("equalizer");
                w.WriteBoolean("customSupported", eqCustomSupported);
                w.WriteBoolean("enabled", eqEnabled);
                w.WriteBoolean("isCustom", eqCustom);
                w.WriteNumber("preset", eqPreset);
                w.WriteStartArray("bands");
                foreach (var b in eqBands)
                    w.WriteNumberValue(b);
                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        });
    }

    private static async Task WriteResponseAsync(HttpListenerResponse res, int status, string contentType, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private const string HtmlPage = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>Galaxy Buds · 设置向导</title>
<style>
  :root {
    --bg: #060609;
    --surface: rgba(13, 14, 19, 0.88);
    --surface-solid: #0d0e13;
    --border: rgba(255, 255, 255, 0.12);
    --border-hi: rgba(255, 255, 255, 0.28);
    --grid-line: rgba(255, 255, 255, 0.065);
    --well: rgba(255, 255, 255, 0.03);
    --field: #090a0e;
    --text: #ffffff;
    --text-2: rgba(255, 255, 255, 0.68);
    --text-3: rgba(255, 255, 255, 0.42);
    --neon: #c1ff12;
    --neon-soft: rgba(193, 255, 18, 0.12);
    --cta-fg: #060609;
    --font: -apple-system, BlinkMacSystemFont, "SF Pro Display", "PingFang SC", sans-serif;
    --mono: "SF Mono", ui-monospace, Menlo, monospace;
    --chamfer: polygon(10px 0, 100% 0, 100% calc(100% - 10px), calc(100% - 10px) 100%, 0 100%, 0 10px);
    --chamfer-sm: polygon(6px 0, 100% 0, 100% calc(100% - 6px), calc(100% - 6px) 100%, 0 100%, 0 6px);
  }
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: var(--font);
    background: var(--bg);
    color: var(--text);
    min-height: 100vh;
    display: flex; flex-direction: column; align-items: center;
    -webkit-font-smoothing: antialiased;
    overflow-x: hidden;
    font-variant-numeric: tabular-nums;
  }
  .bg-glow { position: fixed; inset: 0; pointer-events: none; z-index: 0; overflow: hidden; }
  .bg-glow::before {
    content: ""; position: absolute;
    width: 1100px; height: 520px; top: -240px; left: 50%; transform: translateX(-50%);
    background:
      radial-gradient(ellipse at 50% 40%, rgba(124, 58, 237, 0.22) 0%, rgba(236, 72, 153, 0.10) 38%, transparent 70%),
      radial-gradient(ellipse at 50% 60%, rgba(193, 255, 18, 0.08) 0%, transparent 55%);
    filter: blur(20px);
  }
  .bg-glow::after {
    content: ""; position: absolute; inset: 0;
    background-image:
      linear-gradient(to right, var(--grid-line) 1px, transparent 1px),
      linear-gradient(to bottom, var(--grid-line) 1px, transparent 1px);
    background-size: 72px 72px;
    mask-image: radial-gradient(ellipse 85% 75% at 50% 30%, #000 35%, transparent 100%);
    -webkit-mask-image: radial-gradient(ellipse 85% 75% at 50% 30%, #000 35%, transparent 100%);
  }

  .container {
    width: 100%; max-width: 660px;
    min-height: 100vh;
    padding: 40px 24px 48px;
    position: relative; z-index: 1;
    border-left: 1px solid var(--grid-line);
    border-right: 1px solid var(--grid-line);
  }

  .header { text-align: center; margin-bottom: 24px; }
  .hero-badge {
    display: inline-flex; align-items: center; gap: 8px;
    padding: 4px 12px; margin-bottom: 14px;
    background: var(--neon); color: var(--cta-fg);
    clip-path: var(--chamfer-sm);
    font-family: var(--mono); font-size: 10px; font-weight: 700;
    letter-spacing: 0.12em; text-transform: uppercase;
  }
  .pulse-dot { width: 6px; height: 6px; background: var(--cta-fg); animation: pulseBlink 1.8s ease-in-out infinite; }
  @keyframes pulseBlink { 0%, 100% { opacity: 1; } 50% { opacity: 0.25; } }
  .header h1 { font-size: 26px; font-weight: 700; letter-spacing: 0.04em; margin-bottom: 6px; }
  .header p { font-family: var(--mono); font-size: 11.5px; color: var(--text-2); }

  .hud-strip {
    display: inline-flex; align-items: center; gap: 12px; flex-wrap: wrap; justify-content: center;
    margin-top: 12px; padding: 6px 14px;
    background: var(--surface); border: 1px solid var(--border);
    font-family: var(--mono); font-size: 10.5px; color: var(--text-3);
  }
  .hud-item b { color: var(--neon); font-weight: 700; }
  .hud-sep { width: 1px; height: 10px; background: var(--border); }

  /* 3-Step Industrial Stepper */
  .stepper {
    display: grid; grid-template-columns: repeat(3, 1fr);
    border: 1px solid var(--border); background: var(--surface);
    margin-bottom: 16px;
  }
  .step-node {
    display: flex; align-items: center; justify-content: center; gap: 8px;
    cursor: pointer; user-select: none; padding: 11px 10px;
    border-right: 1px solid var(--border); transition: background .18s;
  }
  .step-node:last-child { border-right: none; }
  .step-node:hover { background: var(--well); }
  .step-node.active { background: rgba(193, 255, 18, 0.06); box-shadow: inset 0 -2px 0 var(--neon); }
  .step-num {
    width: 20px; height: 20px; display: flex; align-items: center; justify-content: center;
    font-family: var(--mono); font-size: 10px; font-weight: 700;
    border: 1px solid var(--border); color: var(--text-3);
  }
  .step-node.active .step-num { background: var(--neon); border-color: var(--neon); color: var(--cta-fg); }
  .step-label { font-size: 12px; font-weight: 600; color: var(--text-2); }
  .step-node.active .step-label { color: var(--text); }

  /* Industrial Panel */
  .panel {
    background: var(--surface); border: 1px solid var(--border);
    padding: 20px; margin-bottom: 14px;
    box-shadow: 0 24px 50px -20px rgba(0,0,0,0.85);
  }
  .panel-head {
    display: flex; align-items: center; justify-content: space-between;
    padding-bottom: 12px; margin-bottom: 14px; border-bottom: 1px solid var(--border);
    font-family: var(--mono); font-size: 11px; letter-spacing: 0.08em; color: var(--text-2); text-transform: uppercase;
  }
  .panel-head b { color: var(--neon); }

  .hero { display: grid; grid-template-columns: 186px 1fr; gap: 18px; align-items: center; }
  .render-stage {
    height: 152px; border: 1px solid var(--border); background: var(--field);
    display: flex; align-items: center; justify-content: center; gap: 8px; padding: 12px;
    position: relative;
  }
  .render-stage::before {
    content: ""; position: absolute; inset: 0;
    background: radial-gradient(circle at 50% 50%, rgba(193, 255, 18, 0.12), transparent 68%);
  }
  .render-stage img {
    height: 104px; width: auto; object-fit: contain; position: relative; z-index: 1;
    filter: drop-shadow(0 12px 20px rgba(0,0,0,0.65)); transition: transform .22s ease;
  }
  .render-stage:hover img { transform: translateY(-3px) scale(1.03); }

  .field-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; margin-bottom: 10px; }
  label.lbl { display: block; font-family: var(--mono); font-size: 10px; color: var(--text-3); margin-bottom: 5px; letter-spacing: 0.06em; }
  select {
    width: 100%; background: var(--field); color: var(--text);
    border: 1px solid var(--border); padding: 8px 10px; font-size: 12.5px; outline: none;
  }
  select:focus { border-color: var(--neon); }

  .actions-row { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 12px; flex-wrap: wrap; }
  .btn {
    appearance: none; border: 1px solid var(--border); background: var(--well); color: var(--text);
    padding: 8px 16px; font-size: 12.5px; font-weight: 600; cursor: pointer; transition: all .16s;
  }
  .btn:hover:not(:disabled) { border-color: var(--border-hi); background: rgba(255,255,255,0.08); }
  .btn-cta {
    background: var(--neon); color: var(--cta-fg); border-color: var(--neon);
    clip-path: var(--chamfer); padding: 9px 18px; font-weight: 700;
  }
  .btn-cta:hover:not(:disabled) { background: #d4ff4d; }
  .btn:disabled { opacity: 0.4; cursor: not-allowed; }

  .seg { display: grid; grid-template-columns: repeat(4, 1fr); gap: 6px; }
  .seg button {
    border: 1px solid var(--border); background: var(--field); color: var(--text-2);
    padding: 11px 8px; font-size: 12.5px; font-weight: 600; cursor: pointer; transition: all .15s;
  }
  .seg button:hover:not(:disabled) { border-color: var(--border-hi); color: var(--text); }
  .seg button.active { background: var(--neon-soft); border-color: var(--neon); color: var(--neon); }
  .seg button:disabled { opacity: 0.4; cursor: not-allowed; }

  .row {
    display: flex; align-items: center; justify-content: space-between; gap: 14px;
    padding: 11px 0; border-top: 1px solid var(--border);
  }
  .row:first-of-type { border-top: none; padding-top: 0; }
  .row-title { font-size: 13.5px; font-weight: 500; }
  .row-desc { font-size: 11.5px; color: var(--text-3); margin-top: 2px; }

  .switch {
    width: 42px; height: 22px; background: var(--field); border: 1px solid var(--border);
    position: relative; cursor: pointer; flex-shrink: 0;
  }
  .switch::after {
    content: ""; position: absolute; top: 2px; left: 2px; width: 16px; height: 16px;
    background: var(--text-3); transition: transform .16s, background .16s;
  }
  .switch.on { border-color: var(--neon); background: var(--neon-soft); }
  .switch.on::after { transform: translateX(20px); background: var(--neon); }

  input[type=range] { width: 185px; accent-color: var(--neon); }
  .toast { font-family: var(--mono); font-size: 11px; color: var(--neon); }
  .step-pane { display: none; }
  .step-pane.active { display: block; }
  .lang-switch {
    position: fixed; top: 16px; right: 20px; z-index: 20;
    display: flex; gap: 2px; background: var(--surface-solid);
    border: 1px solid var(--border); padding: 3px;
  }
  .lang-btn {
    appearance: none; border: none; cursor: pointer;
    font-size: 10.5px; font-weight: 700; font-family: var(--mono);
    padding: 4px 8px; color: var(--text-3); background: transparent;
  }
  .lang-btn.active { background: var(--neon); color: var(--cta-fg); }
  @media (max-width: 600px) { .hero { grid-template-columns: 1fr; } }
</style>
</head>
<body>
<div class="lang-switch">
  <button class="lang-btn active" id="langEn" onclick="setLang('en')">EN</button>
  <button class="lang-btn" id="langZh" onclick="setLang('zh')">中文</button>
</div>
<div class="bg-glow"></div>
<div class="container">
  <div class="header">
    <div class="hero-badge"><span class="pulse-dot"></span> GALAXY BUDS WIDGET · SETUP</div>
    <h1 data-i18n="title">GALAXY BUDS SETUP WIZARD</h1>
    <p data-i18n="sub">macOS Liquid Glass Desktop Widget · Local Control Center</p>
    <div class="hud-strip">
      <span class="hud-item"><span data-i18n="hudStatus">STATUS</span> <b id="hudConn">OFFLINE</b></span>
      <span class="hud-sep"></span>
      <span class="hud-item"><span data-i18n="hudL">L</span> <b id="hudL">--</b></span>
      <span class="hud-sep"></span>
      <span class="hud-item"><span data-i18n="hudR">R</span> <b id="hudR">--</b></span>
      <span class="hud-sep"></span>
      <span class="hud-item"><span data-i18n="hudC">CASE</span> <b id="hudC">--</b></span>
    </div>
  </div>

  <div class="stepper">
    <div class="step-node active" onclick="goStep(1)" id="st1">
      <span class="step-num">01</span><span class="step-label" data-i18n="step1">Device & Render</span>
    </div>
    <div class="step-node" onclick="goStep(2)" id="st2">
      <span class="step-num">02</span><span class="step-label" data-i18n="step2">ANC, Link & EQ</span>
    </div>
    <div class="step-node" onclick="goStep(3)" id="st3">
      <span class="step-num">03</span><span class="step-label" data-i18n="step3">Posture & Widget</span>
    </div>
  </div>

  <!-- Step 1 -->
  <div class="step-pane active" id="pane1">
    <div class="panel">
      <div class="panel-head"><span data-i18n="p1Head"><b>01</b> // Samsung Device & Preview</span><span id="connName">--</span></div>
      <div class="hero">
        <div class="render-stage">
          <img id="imgL" src="/api/render?variant=Buds3ProSilver&side=0" alt="Left" />
          <img id="imgR" src="/api/render?variant=Buds3ProSilver&side=1" alt="Right" />
        </div>
        <div>
          <div class="field-grid">
            <div>
              <label class="lbl">SAMSUNG MODEL</label>
              <select id="modelSelect" onchange="onModelChange()"></select>
            </div>
            <div>
              <label class="lbl">FINISH / COLOR</label>
              <select id="variantSelect" onchange="updatePreview()"></select>
            </div>
          </div>
          <div>
            <label class="lbl">PAIRED BLUETOOTH DEVICE</label>
            <select id="devSelect"></select>
          </div>
          <div class="actions-row">
            <button class="btn btn-cta" id="btnConn" data-i18n="btnConn" onclick="connectSelected()">Connect</button>
            <button class="btn" id="btnDisc" data-i18n="btnDisc" onclick="post('/api/disconnect')">Disconnect</button>
          </div>
        </div>
      </div>
    </div>
  </div>

  <!-- Step 2 -->
  <div class="step-pane" id="pane2">
    <div class="panel">
      <div class="panel-head"><span data-i18n="p2aHead"><b>02A</b> // Noise Control (4 Modes)</span></div>
      <div class="seg">
        <button id="m0" data-i18n="m0" onclick="setNoise(0)">Off</button>
        <button id="m3" data-i18n="m3" onclick="setNoise(3)">Adaptive</button>
        <button id="m2" data-i18n="m2" onclick="setNoise(2)">ANC</button>
        <button id="m1" data-i18n="m1" onclick="setNoise(1)">Ambient</button>
      </div>
    </div>
    <div class="panel">
      <div class="panel-head"><span data-i18n="p2bHead"><b>02B</b> // Seamless & Multipoint</span><span class="toast" id="mpToast"></span></div>
      <div class="row">
        <div>
          <div class="row-title" data-i18n="seamTitle">Seamless Earbud Connection</div>
          <div class="row-desc" data-i18n="seamDesc">Allow other Samsung devices to take over audio without disconnecting</div>
        </div>
        <button class="switch" id="swSeamless" onclick="toggleSeamless()"></button>
      </div>
      <div class="row">
        <div>
          <div class="row-title" data-i18n="autoMpTitle">Auto-Unlock Multipoint on Connect</div>
          <div class="row-desc" data-i18n="autoMpDesc">Automatically write MDE flag to stay connected to Mac & phone</div>
        </div>
        <button class="switch" id="swAutoMp" onclick="toggleAutoMp()"></button>
      </div>
      <div class="row">
        <div>
          <div class="row-title" data-i18n="applyMpTitle">Apply Multipoint Unlock Now</div>
          <div class="row-desc" data-i18n="applyMpDesc">Earbuds will briefly reconnect after writing</div>
        </div>
        <button class="btn" id="btnApplyMp" data-i18n="btnApply" onclick="applyMultipoint()">Apply</button>
      </div>
    </div>
    <div class="panel">
      <div class="panel-head">
        <span data-i18n="p2cHead"><b>02C</b> // Equalizer (EQ)</span>
        <button class="btn" id="btnEqReset" data-i18n="btnReset" onclick="resetCustomEq()" style="padding:4px 10px;font-size:11px;">Reset</button>
      </div>
      <div class="seg" style="grid-template-columns:repeat(7,1fr);margin-bottom:14px;">
        <button id="eqOff" data-i18n="eqOff" onclick="setEqPreset(false,false,0)">Off</button>
        <button id="eq0" data-i18n="eq0" onclick="setEqPreset(true,false,0)">Bass</button>
        <button id="eq1" data-i18n="eq1" onclick="setEqPreset(true,false,1)">Soft</button>
        <button id="eq2" data-i18n="eq2" onclick="setEqPreset(true,false,2)">Dynamic</button>
        <button id="eq3" data-i18n="eq3" onclick="setEqPreset(true,false,3)">Clear</button>
        <button id="eq4" data-i18n="eq4" onclick="setEqPreset(true,false,4)">Treble</button>
        <button id="eqCustom" data-i18n="eqCustom" onclick="setEqPreset(true,true,0)">Custom</button>
      </div>
      <div id="eqBandsWrap" style="position:relative;background:var(--field);border:1px solid var(--border);padding:12px 10px 8px;user-select:none;touch-action:none;">
        <svg id="eqSvg" viewBox="0 0 900 140" preserveAspectRatio="none" style="position:absolute;left:10px;right:10px;top:28px;width:calc(100% - 20px);height:140px;pointer-events:none;z-index:1;">
          <defs>
            <linearGradient id="eqFillGrad" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stop-color="#c1ff12" stop-opacity="0.28"/>
              <stop offset="100%" stop-color="#c1ff12" stop-opacity="0.02"/>
            </linearGradient>
          </defs>
          <line x1="0" y1="35" x2="900" y2="35" stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4"/>
          <line x1="0" y1="70" x2="900" y2="70" stroke="rgba(255,255,255,0.16)" stroke-width="1"/>
          <line x1="0" y1="105" x2="900" y2="105" stroke="rgba(255,255,255,0.06)" stroke-dasharray="4 4"/>
          <path id="eqArea" d="" fill="url(#eqFillGrad)"/>
          <path id="eqCurve" d="" fill="none" stroke="#c1ff12" stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round"/>
        </svg>
        <div id="eqCols" style="position:relative;z-index:2;display:grid;grid-template-columns:repeat(9,1fr);gap:6px;"></div>
      </div>
    </div>
  </div>

  <!-- Step 3 -->
  <div class="step-pane" id="pane3">
    <div class="panel">
      <div class="panel-head"><span data-i18n="p3aHead"><b>03A</b> // Posture Monitor</span><span class="toast" id="postureStat">PITCH --</span></div>
      <div class="row">
        <div>
          <div class="row-title" data-i18n="posTitle">Enable Neck-Posture Screen Blur</div>
          <div class="row-desc" data-i18n="posDesc">Also toggleable & calibratable from the widget top-right</div>
        </div>
        <div style="display:flex;align-items:center;gap:10px;">
          <button class="btn" data-i18n="btnCal" onclick="post('/api/posture', { calibrate: true })">Calibrate</button>
          <button class="switch" id="swPosture" onclick="togglePosture()"></button>
        </div>
      </div>
      <div class="row">
        <span class="row-title"><span data-i18n="lblAngle">Trigger Angle:</span> <strong id="valAngle" style="color:var(--neon)">9°</strong></span>
        <input type="range" id="rngAngle" min="2" max="30" value="9" oninput="valAngle.textContent=this.value+'°'" onchange="post('/api/posture', { triggerAngle: +this.value })" />
      </div>
      <div class="row">
        <span class="row-title"><span data-i18n="lblBlur">Blur Intensity:</span> <strong id="valBlur" style="color:var(--neon)">50</strong></span>
        <input type="range" id="rngBlur" min="0" max="100" value="50" oninput="valBlur.textContent=this.value" onchange="post('/api/posture', { blurIntensity: +this.value })" />
      </div>
    </div>
    <div class="panel">
      <div class="panel-head"><span data-i18n="p3bHead"><b>03B</b> // Desktop Widget</span></div>
      <div class="row">
        <div>
          <div class="row-title" data-i18n="widTitle">Show Liquid Glass Desktop Widget</div>
          <div class="row-desc" data-i18n="widDesc">344×162 desktop-level widget; hold top bar 0.25s to drag</div>
        </div>
        <button class="switch" id="swWidget" onclick="toggleWidget()"></button>
      </div>
    </div>
  </div>
</div>

<script>
let state = null;
let initializedModels = false;
let currentLang = 'en';

const I18N = {
  en: {
    title: 'GALAXY BUDS // WORKBENCH',
    sub: 'macOS DESKTOP WIDGET · NOISE CONTROL · EQUALIZER · POSTURE',
    hudStatus: 'STATUS',
    hudL: 'L BUD',
    hudR: 'R BUD',
    hudC: 'CASE',
    step1: 'Bluetooth & Model',
    step2: 'Audio & Multipoint',
    step3: 'Posture & Widget',
    p1Head: '<b>01</b> // BLUETOOTH LINK &amp; MODEL',
    btnConn: 'CONNECT',
    btnDisc: 'DISCONNECT',
    p2aHead: '<b>02</b> // ACTIVE NOISE CONTROL',
    m0: 'Off',
    m3: 'Adaptive',
    m2: 'ANC',
    m1: 'Ambient',
    p2bHead: '<b>02</b> // SEAMLESS CONNECTION &amp; MULTIPOINT',
    seamTitle: 'Seamless Earbud Connection',
    seamDesc: 'Allow switching between paired devices without disconnecting first',
    autoMpTitle: 'Auto Multipoint on Connect',
    autoMpDesc: 'Automatically restore Seamless Connection after Buds connect',
    applyMpTitle: 'Write Multipoint Config Now',
    applyMpDesc: 'Push Seamless + Multipoint state to earbuds immediately',
    btnApply: 'APPLY NOW',
    p2cHead: '<b>02</b> // EQUALIZER (SAMSUNG PRESETS &amp; 9-BAND)',
    btnReset: 'RESET 0dB',
    eqOff: 'Off',
    eq0: 'Bass Boost',
    eq1: 'Soft',
    eq2: 'Dynamic',
    eq3: 'Clear',
    eq4: 'Treble Boost',
    eqCustom: 'Custom',
    p3aHead: '<b>03</b> // NECK POSTURE MONITOR',
    posTitle: 'Enable Neck Posture Monitor',
    posDesc: 'Monitor head pitch via earbud IMU and blur screen on slouch',
    btnCal: 'CALIBRATE',
    lblAngle: 'Trigger Angle',
    lblBlur: 'Blur Intensity',
    p3bHead: '<b>03</b> // DESKTOP WIDGET OVERLAY',
    widTitle: 'Show Desktop Liquid Glass Widget',
    widDesc: 'Floating desktop battery and noise control widget',
    connOnline: 'CONNECTED',
    connOffline: 'OFFLINE',
    notConnected: 'Disconnected',
    noPaired: 'No paired Bluetooth devices found'
  },
  zh: {
    title: 'GALAXY BUDS // 工作台',
    sub: 'macOS 桌面组件 · 降噪 · 均衡器 · 坐姿检测',
    hudStatus: '连接状态',
    hudL: '左耳',
    hudR: '右耳',
    hudC: '充电盒',
    step1: '蓝牙与型号',
    step2: '音频与双设备',
    step3: '坐姿与组件',
    p1Head: '<b>01</b> // 蓝牙连接与型号选择',
    btnConn: '连接',
    btnDisc: '断开',
    p2aHead: '<b>02</b> // 主动降噪控制',
    m0: '关闭',
    m3: '自适应',
    m2: '降噪',
    m1: '环境音',
    p2bHead: '<b>02</b> // 无缝切换与双设备',
    seamTitle: '无缝耳机连接 (Seamless Connection)',
    seamDesc: '允许在已配对设备间不先断开即可切换连接',
    autoMpTitle: '连接后自动启用双设备协同',
    autoMpDesc: '耳机连接成功后自动写入并恢复无缝连接状态',
    applyMpTitle: '立即写入当前双设备配置',
    applyMpDesc: '立刻向耳机下发 Seamless + Multipoint 状态',
    btnApply: '立即写入',
    p2cHead: '<b>02</b> // 均衡器 (三星预设与 9 段自定义)',
    btnReset: '归零',
    eqOff: '关闭',
    eq0: '低音增强',
    eq1: '柔和',
    eq2: '动感',
    eq3: '清晰',
    eq4: '高音增强',
    eqCustom: '自定义',
    p3aHead: '<b>03</b> // 坐姿护颈监测',
    posTitle: '开启坐姿低头监测',
    posDesc: '通过耳机 IMU 传感器实时监测低头角度并在低头时模糊屏幕提醒',
    btnCal: '一键校准',
    lblAngle: '低头触发阈值',
    lblBlur: '模糊提醒强度',
    p3bHead: '<b>03</b> // 桌面悬浮组件',
    widTitle: '显示桌面 Liquid Glass 组件',
    widDesc: '桌面电量与降噪快捷控制悬浮窗',
    connOnline: '已连接',
    connOffline: '未连接',
    notConnected: '未连接',
    noPaired: '未检测到已配对蓝牙设备'
  }
};

const HTML_KEYS = new Set(['p1Head', 'p2aHead', 'p2bHead', 'p2cHead', 'p3aHead', 'p3bHead']);

function applyLang(lang) {
  currentLang = (lang === 'zh') ? 'zh' : 'en';
  const dict = I18N[currentLang];
  document.documentElement.lang = currentLang === 'zh' ? 'zh-CN' : 'en';
  document.getElementById('langEn').classList.toggle('active', currentLang === 'en');
  document.getElementById('langZh').classList.toggle('active', currentLang === 'zh');
  document.querySelectorAll('[data-i18n]').forEach(el => {
    const key = el.getAttribute('data-i18n');
    if (!(key in dict)) return;
    if (HTML_KEYS.has(key)) el.innerHTML = dict[key];
    else el.textContent = dict[key];
  });
}

function setLang(lang) {
  applyLang(lang);
  post('/api/lang', { lang });
}

function goStep(n) {
  for (let i = 1; i <= 3; i++) {
    document.getElementById('st' + i).className = 'step-node' + (i === n ? ' active' : '');
    document.getElementById('pane' + i).className = 'step-pane' + (i === n ? ' active' : '');
  }
}

async function post(url, body = {}) {
  const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const data = await r.json();
  if (data && data.models) { state = data; render(); }
  return data;
}
async function refresh() {
  try {
    const r = await fetch('/api/state');
    state = await r.json();
    render();
  } catch {}
}
function connectSelected() {
  const mac = document.getElementById('devSelect').value;
  const model = document.getElementById('modelSelect').value;
  post('/api/connect', { mac, model });
}
function setNoise(mode) { post('/api/noise', { mode }); }
let localBands = [0, 0, 0, 0, 0, 0, 0, 0, 0];
let draggingEq = false;
const PRESET_CURVES = {
  off: [0, 0, 0, 0, 0, 0, 0, 0, 0],
  0: [6, 5, 3, 1, 0, 0, 0, 0, 0],
  1: [0, 0, 0, 1, 2, 1, -1, -3, -4],
  2: [5, 4, 1, 0, -1, 1, 3, 4, 4],
  3: [-2, -1, 0, 2, 4, 4, 3, 2, 1],
  4: [0, 0, 0, 0, 1, 3, 5, 6, 6]
};

function drawEqChart(bands, interactive) {
  const pts = [];
  for (let i = 0; i < 9; i++) {
    const g = Math.max(-10, Math.min(10, bands[i] || 0));
    const pct = (g + 10) / 20; // 0..1
    const ySvg = 140 - pct * 140;
    const xSvg = 50 + i * 100;
    pts.push([xSvg, ySvg]);

    const valEl = document.getElementById('eqv' + i);
    const barEl = document.getElementById('eqb' + i);
    const dotEl = document.getElementById('eqd' + i);
    if (valEl) valEl.textContent = (g > 0 ? '+' : '') + g;
    if (barEl) {
      const hPct = Math.abs(g) / 20 * 100;
      barEl.style.height = Math.max(2, hPct) + '%';
      barEl.style.bottom = g >= 0 ? '50%' : (50 - hPct) + '%';
      barEl.style.background = interactive
        ? 'linear-gradient(to top, rgba(193,255,18,0.25), rgba(193,255,18,0.75))'
        : 'rgba(193,255,18,0.35)';
    }
    if (dotEl) {
      dotEl.style.bottom = `calc(${pct * 100}% - 5px)`;
      dotEl.style.opacity = interactive ? '1' : '0.65';
    }
  }
  let d = `M ${pts[0][0]} ${pts[0][1]}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const mx = (pts[i][0] + pts[i + 1][0]) / 2;
    d += ` C ${mx} ${pts[i][1]}, ${mx} ${pts[i + 1][1]}, ${pts[i + 1][0]} ${pts[i + 1][1]}`;
  }
  document.getElementById('eqCurve').setAttribute('d', d);
  document.getElementById('eqArea').setAttribute('d', `${d} L ${pts[8][0]} 140 L ${pts[0][0]} 140 Z`);
}

function setEqPreset(enabled, isCustom, preset) {
  post('/api/equalizer', { enabled, isCustom, preset, bands: localBands });
}
function pushCustomBands() {
  post('/api/equalizer', { enabled: true, isCustom: true, preset: 0, bands: localBands });
}
function resetCustomEq() {
  localBands = [0, 0, 0, 0, 0, 0, 0, 0, 0];
  drawEqChart(localBands, true);
  pushCustomBands();
}
function toggleSeamless() { post('/api/seamless', { enabled: !state?.connectivity?.seamlessEnabled }); }
function toggleAutoMp() { post('/api/multipoint', { auto: !state?.connectivity?.autoMultipoint }); }
async function applyMultipoint() {
  const t = document.getElementById('mpToast');
  t.textContent = 'WRITING...';
  const res = await post('/api/multipoint', { apply: true });
  t.textContent = res?.message || '';
  setTimeout(() => { t.textContent = ''; }, 4000);
}
function togglePosture() { post('/api/posture', { enabled: !state?.posture?.enabled }); }
function toggleWidget() { post('/api/widget', { enabled: !state?.widget?.enabled }); }

function onModelChange() {
  const mId = document.getElementById('modelSelect').value;
  const modelObj = (state?.models || []).find(m => m.id === mId);
  const vs = document.getElementById('variantSelect');
  vs.innerHTML = '';
  for (const v of (modelObj?.variants || [])) {
    const opt = document.createElement('option');
    opt.value = v;
    opt.textContent = v.replace(mId, '') || v;
    vs.appendChild(opt);
  }
  updatePreview();
}

function updatePreview() {
  const v = document.getElementById('variantSelect').value || 'Buds3ProSilver';
  document.getElementById('imgL').src = `/api/render?variant=${encodeURIComponent(v)}&side=0`;
  document.getElementById('imgR').src = `/api/render?variant=${encodeURIComponent(v)}&side=1`;
}

function render() {
  if (!state) return;
  applyLang(state.lang || 'en');
  const dict = I18N[currentLang];
  document.getElementById('hudConn').textContent = state.connected ? dict.connOnline : dict.connOffline;
  document.getElementById('connName').textContent = state.connected ? state.deviceName : dict.notConnected;
  document.getElementById('btnConn').disabled = state.connected;
  document.getElementById('btnDisc').disabled = !state.connected;

  const b = state.battery;
  document.getElementById('hudL').textContent = b.l ? b.l + '%' : '--';
  document.getElementById('hudR').textContent = b.r ? b.r + '%' : '--';
  document.getElementById('hudC').textContent = b.case ? b.case + '%' : '--';

  if (!initializedModels && state.models?.length) {
    initializedModels = true;
    const ms = document.getElementById('modelSelect');
    ms.innerHTML = '';
    for (const m of state.models) {
      const opt = document.createElement('option');
      opt.value = m.id;
      opt.textContent = m.label;
      if (m.id === state.model) opt.selected = true;
      ms.appendChild(opt);
    }
    onModelChange();
  }

  const sel = document.getElementById('devSelect');
  const prev = sel.value || state.mac;
  sel.innerHTML = '';
  if ((state.pairedDevices || []).length === 0) {
    const opt = document.createElement('option');
    opt.value = '';
    opt.textContent = dict.noPaired;
    sel.appendChild(opt);
  } else {
    for (const d of state.pairedDevices) {
      const opt = document.createElement('option');
      opt.value = d.mac;
      opt.textContent = `${d.name} (${d.mac})`;
      if (d.mac.toLowerCase() === (prev || '').toLowerCase()) opt.selected = true;
      sel.appendChild(opt);
    }
  }

  for (const id of [0, 1, 2, 3]) {
    const el = document.getElementById('m' + id);
    el.disabled = !state.connected;
    el.className = (state.connected && state.noiseMode === id) ? 'active' : '';
  }

  const c = state.connectivity;
  document.getElementById('swSeamless').className = 'switch' + (c.seamlessEnabled ? ' on' : '');
  document.getElementById('swSeamless').disabled = !state.connected || !c.seamlessSupported;
  document.getElementById('swAutoMp').className = 'switch' + (c.autoMultipoint ? ' on' : '');
  document.getElementById('btnApplyMp').disabled = !state.connected || !c.multipointSupported;

  const eq = state.equalizer;
  if (eq) {
    document.getElementById('eqOff').className = (state.connected && !eq.enabled) ? 'active' : '';
    document.getElementById('eqOff').disabled = !state.connected;
    for (let i = 0; i < 5; i++) {
      const b = document.getElementById('eq' + i);
      b.disabled = !state.connected;
      b.className = (state.connected && eq.enabled && !eq.isCustom && eq.preset === i) ? 'active' : '';
    }
    const cBtn = document.getElementById('eqCustom');
    cBtn.disabled = !state.connected || !eq.customSupported;
    cBtn.className = (state.connected && eq.enabled && eq.isCustom) ? 'active' : '';

    const cols = document.getElementById('eqCols');
    const wrap = document.getElementById('eqBandsWrap');
    const freqs = ['63', '125', '250', '500', '1k', '2k', '4k', '8k', '16k'];
    if (!cols.hasChildNodes()) {
      for (let i = 0; i < 9; i++) {
        const col = document.createElement('div');
        col.style.cssText = 'display:flex;flex-direction:column;align-items:center;gap:4px;cursor:ns-resize;';
        col.innerHTML =
          `<span id="eqv${i}" style="font-family:var(--mono);font-size:10px;color:var(--neon);height:14px;">0</span>` +
          `<div id="eqt${i}" style="position:relative;width:100%;height:140px;display:flex;justify-content:center;">` +
            `<div id="eqb${i}" style="position:absolute;width:14px;background:rgba(193,255,18,0.4);border-radius:2px;transition:height .06s,bottom .06s;"></div>` +
            `<div id="eqd${i}" style="position:absolute;width:10px;height:10px;border-radius:50%;background:var(--neon);box-shadow:0 0 8px rgba(193,255,18,0.8);left:50%;transform:translateX(-50%);bottom:calc(50% - 5px);"></div>` +
          `</div>` +
          `<span style="font-family:var(--mono);font-size:9.5px;color:var(--text-3);">${freqs[i]}</span>`;

        const track = col.querySelector('#eqt' + i);
        const setFromEvent = (ev) => {
          const rect = track.getBoundingClientRect();
          const rel = Math.max(0, Math.min(1, (ev.clientY - rect.top) / rect.height));
          const val = Math.round(10 - rel * 20);
          localBands[i] = val;
          drawEqChart(localBands, true);
        };
        col.addEventListener('pointerdown', (ev) => {
          if (!state?.connected || !state?.equalizer?.customSupported) return;
          draggingEq = true;
          col.setPointerCapture(ev.pointerId);
          setFromEvent(ev);
        });
        col.addEventListener('pointermove', (ev) => {
          if (!draggingEq) return;
          setFromEvent(ev);
        });
        col.addEventListener('pointerup', () => {
          if (!draggingEq) return;
          draggingEq = false;
          pushCustomBands();
        });
        cols.appendChild(col);
      }
    }
    wrap.style.opacity = state.connected ? '1' : '0.4';
    if (!draggingEq) {
      if (eq.isCustom && eq.enabled) {
        localBands = (eq.bands && eq.bands.length === 9) ? [...eq.bands] : [0,0,0,0,0,0,0,0,0];
        drawEqChart(localBands, true);
      } else if (eq.enabled) {
        drawEqChart(PRESET_CURVES[eq.preset] || PRESET_CURVES.off, false);
      } else {
        drawEqChart(PRESET_CURVES.off, false);
      }
    }
  }

  const p = state.posture;
  document.getElementById('swPosture').className = 'switch' + (p.enabled ? ' on' : '');
  document.getElementById('postureStat').textContent = `PITCH ${p.pitchDeg}° · DELTA ${p.deltaDeg}°`;
  if (document.activeElement !== document.getElementById('rngAngle')) {
    document.getElementById('rngAngle').value = p.triggerAngle;
    document.getElementById('valAngle').textContent = p.triggerAngle + '°';
  }
  if (document.activeElement !== document.getElementById('rngBlur')) {
    document.getElementById('rngBlur').value = p.blurIntensity;
    document.getElementById('valBlur').textContent = p.blurIntensity;
  }
  document.getElementById('swWidget').className = 'switch' + (state.widget.enabled ? ' on' : '');
}
refresh();
setInterval(refresh, 1500);
</script>
</body>
</html>
""";
}
