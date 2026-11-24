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
  @media (max-width: 600px) { .hero { grid-template-columns: 1fr; } }
</style>
</head>
<body>
<div class="bg-glow"></div>
<div class="container">
  <div class="header">
    <div class="hero-badge"><span class="pulse-dot"></span> GALAXY BUDS WIDGET · SETUP</div>
    <h1>GALAXY BUDS 设置向导</h1>
    <p>三星耳机桌面液态玻璃小组件 · 本地配置中心</p>
    <div class="hud-strip">
      <span class="hud-item">状态 <b id="hudConn">OFFLINE</b></span>
      <span class="hud-sep"></span>
      <span class="hud-item">左耳 <b id="hudL">--</b></span>
      <span class="hud-sep"></span>
      <span class="hud-item">右耳 <b id="hudR">--</b></span>
      <span class="hud-sep"></span>
      <span class="hud-item">盒 <b id="hudC">--</b></span>
    </div>
  </div>

  <div class="stepper">
    <div class="step-node active" onclick="goStep(1)" id="st1">
      <span class="step-num">01</span><span class="step-label">设备与渲染</span>
    </div>
    <div class="step-node" onclick="goStep(2)" id="st2">
      <span class="step-num">02</span><span class="step-label">降噪与互联</span>
    </div>
    <div class="step-node" onclick="goStep(3)" id="st3">
      <span class="step-num">03</span><span class="step-label">坐姿与小组件</span>
    </div>
  </div>

  <!-- Step 1 -->
  <div class="step-pane active" id="pane1">
    <div class="panel">
      <div class="panel-head"><span><b>01</b> // 三星设备选择与实物预览</span><span id="connName">--</span></div>
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
            <button class="btn btn-cta" id="btnConn" onclick="connectSelected()">连接耳机</button>
            <button class="btn" id="btnDisc" onclick="post('/api/disconnect')">断开连接</button>
          </div>
        </div>
      </div>
    </div>
  </div>

  <!-- Step 2 -->
  <div class="step-pane" id="pane2">
    <div class="panel">
      <div class="panel-head"><span><b>02A</b> // 主动降噪控制（4 模式）</span></div>
      <div class="seg">
        <button id="m0" onclick="setNoise(0)">关闭</button>
        <button id="m3" onclick="setNoise(3)">自适应</button>
        <button id="m2" onclick="setNoise(2)">降噪</button>
        <button id="m1" onclick="setNoise(1)">环境音</button>
      </div>
    </div>
    <div class="panel">
      <div class="panel-head"><span><b>02B</b> // 无缝切换与双设备互联</span><span class="toast" id="mpToast"></span></div>
      <div class="row">
        <div>
          <div class="row-title">无缝耳机连接（Seamless Connection）</div>
          <div class="row-desc">允许其他三星设备直接接管耳机音频，无需手动断开</div>
        </div>
        <button class="switch" id="swSeamless" onclick="toggleSeamless()"></button>
      </div>
      <div class="row">
        <div>
          <div class="row-title">连接时自动解锁双设备互联（Multipoint）</div>
          <div class="row-desc">自动写入 MDE 标记，支持电脑与手机同时在线</div>
        </div>
        <button class="switch" id="swAutoMp" onclick="toggleAutoMp()"></button>
      </div>
      <div class="row">
        <div>
          <div class="row-title">手动立即应用多点解锁</div>
          <div class="row-desc">写入后耳机会短暂重连一次</div>
        </div>
        <button class="btn" id="btnApplyMp" onclick="applyMultipoint()">立即应用</button>
      </div>
    </div>
  </div>

  <!-- Step 3 -->
  <div class="step-pane" id="pane3">
    <div class="panel">
      <div class="panel-head"><span><b>03A</b> // 坐姿检测调校</span><span class="toast" id="postureStat">PITCH --</span></div>
      <div class="row">
        <div>
          <div class="row-title">启用低头全屏模糊提醒</div>
          <div class="row-desc">也可直接在桌面小组件右上角一键开关与校准</div>
        </div>
        <div style="display:flex;align-items:center;gap:10px;">
          <button class="btn" onclick="post('/api/posture', { calibrate: true })">坐直校准</button>
          <button class="switch" id="swPosture" onclick="togglePosture()"></button>
        </div>
      </div>
      <div class="row">
        <span class="row-title">触发角度：<strong id="valAngle" style="color:var(--neon)">9°</strong></span>
        <input type="range" id="rngAngle" min="2" max="30" value="9" oninput="valAngle.textContent=this.value+'°'" onchange="post('/api/posture', { triggerAngle: +this.value })" />
      </div>
      <div class="row">
        <span class="row-title">模糊强度：<strong id="valBlur" style="color:var(--neon)">50</strong></span>
        <input type="range" id="rngBlur" min="0" max="100" value="50" oninput="valBlur.textContent=this.value" onchange="post('/api/posture', { blurIntensity: +this.value })" />
      </div>
    </div>
    <div class="panel">
      <div class="panel-head"><span><b>03B</b> // 桌面小组件</span></div>
      <div class="row">
        <div>
          <div class="row-title">常驻显示液态玻璃桌面小组件</div>
          <div class="row-desc">344×162 桌面底层小组件，长按顶部区域 0.25s 可拖动位置</div>
        </div>
        <button class="switch" id="swWidget" onclick="toggleWidget()"></button>
      </div>
    </div>
  </div>
</div>

<script>
let state = null;
let initializedModels = false;

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
  document.getElementById('hudConn').textContent = state.connected ? 'CONNECTED' : 'OFFLINE';
  document.getElementById('connName').textContent = state.connected ? state.deviceName : '未连接';
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
    opt.textContent = '未检测到已配对蓝牙设备';
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
