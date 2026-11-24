using Avalonia.Interactivity;
using GalaxyBudsClient.Interface.ViewModels.Pages;
using GalaxyBudsClient.Platform.Posture;

namespace GalaxyBudsClient.Interface.Pages;

public partial class PosturePage : BasePage<PosturePageViewModel>
{
    public PosturePage()
    {
        InitializeComponent();
    }

    private void OnCalibrateClicked(object? sender, RoutedEventArgs e)
    {
        PostureMonitorService.Instance.Calibrate();
    }
}
