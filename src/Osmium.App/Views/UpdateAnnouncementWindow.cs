using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Osmium.App.Helpers;
using Osmium.App.Services;

namespace Osmium.App.Views;

internal sealed class UpdateAnnouncementWindow : Window
{
    public UpdateAnnouncementWindow(UpdateRelease release, ElementTheme? theme)
    {
        Title = LocalizationService.GetString("Update_WindowTitle");
        var heading = new TextBlock
        {
            Text = LocalizationService.Format("Update_TitleFmt", release.Version),
            FontSize = 23,
            TextWrapping = TextWrapping.Wrap
        };
        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(release.Notes)
                ? LocalizationService.GetString("Update_NoNotes")
                : release.Notes,
            TextWrapping = TextWrapping.Wrap
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        AddDownload(actions, release.GetDownloadUri("installer"), "Update_DownloadInstaller");
        AddDownload(actions, release.GetDownloadUri("portable"), "Update_DownloadPortable");
        var close = new Button { Content = LocalizationService.GetString("Update_Close") };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);

        var body = new Grid
        {
            RequestedTheme = theme ?? ElementTheme.Default,
            Padding = new Thickness(24),
            RowSpacing = 16,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        var scroll = new ScrollViewer { Content = notes };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(actions, 2);
        body.Children.Add(heading);
        body.Children.Add(scroll);
        body.Children.Add(actions);
        Content = body;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }

        WindowManagerHelper.ResizeWithDpi(AppWindow, this, 480, 380);
        WindowManagerHelper.CenterWindowOnScreen(AppWindow, 480, 380);
    }

    private static void AddDownload(Panel actions, Uri? uri, string label)
    {
        if (uri != null)
        {
            actions.Children.Add(new HyperlinkButton
            {
                Content = LocalizationService.GetString(label),
                NavigateUri = uri,
                Padding = new Thickness(0, 4, 0, 4)
            });
        }
    }
}
