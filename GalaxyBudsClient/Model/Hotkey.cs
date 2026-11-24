using System;
using System.Collections.Generic;
using GalaxyBudsClient.Platform.Interfaces;
using GalaxyBudsClient.Platform.Model;
using GalaxyBudsClient.Utils.Extensions;
using ReactiveUI;

namespace GalaxyBudsClient.Model;

public partial class Hotkey : ReactiveObject, IHotkey
{
    // Plain auto-properties on purpose: the System.Text.Json source generator runs before
    // IL weaving, so [Reactive]-woven fields would be invisible to it and every hotkey would
    // serialize as {} (upstream bug). Plain properties are visible to the generator.
    public IEnumerable<ModifierKeys> Modifier { set; get; } = Array.Empty<ModifierKeys>();
    public IEnumerable<Keys> Keys { set; get; } = Array.Empty<Keys>();
    public Event Action { set; get; }
    
    internal string ActionName => Action.GetLocalizedDescription();
    internal string HotkeyName => Keys.AsHotkeyString(Modifier);

    public override string ToString()
    {
        return Keys.AsHotkeyString(Modifier) + ": " + Action.GetLocalizedDescription();
    }

    public static Hotkey Empty => new();
}
