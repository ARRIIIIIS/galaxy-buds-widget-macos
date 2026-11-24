using System;
using Avalonia.Controls;
using FluentIcons.Common;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Interface.Pages;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Platform.Posture;

namespace GalaxyBudsClient.Interface.ViewModels.Pages;

public partial class PosturePageViewModel : MainPageViewModelBase
{
    public override Control CreateView() => new PosturePage { DataContext = this };
    public override string TitleKey => Keys.PagePosture;
    public override Symbol IconKey => Symbol.Accessibility;
    public override bool ShowsInFooter => false;

    [Reactive] private string _statusText = string.Empty;
    [Reactive] private bool _isTracking;
    [Reactive] private bool _isBadPosture;
    [Reactive] private double _pitchDegrees;
    [Reactive] private double _deltaDegrees;
    [Reactive] private double _severity;

    public PosturePageViewModel()
    {
        PostureMonitorService.Instance.Updated += OnServiceUpdated;
        Settings.MainSettingsPropertyChanged += OnSettingsChanged;
        Refresh();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsData.PostureMonitorEnabled):
            case nameof(SettingsData.HasPostureBaseline):
            case nameof(SettingsData.PostureBaselinePitch):
                Refresh();
                break;
        }
    }

    private void OnServiceUpdated(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var service = PostureMonitorService.Instance;

        IsTracking = service.IsTracking;
        IsBadPosture = service.IsBadPosture;
        PitchDegrees = service.PitchDegrees;
        DeltaDegrees = service.DeltaDegrees;
        Severity = service.Severity;

        StatusText = service.State switch
        {
            PostureMonitorState.Stopped => Strings.PostureStatusOff,
            PostureMonitorState.WaitingForDevice => Strings.PostureStatusWaiting,
            PostureMonitorState.Unsupported => Strings.PostureStatusUnsupported,
            _ => !Settings.Data.HasPostureBaseline
                ? Strings.PostureStatusNeedCalibration
                : service.IsBadPosture ? Strings.PostureStatusBad : Strings.PostureStatusTracking
        };
    }
}
