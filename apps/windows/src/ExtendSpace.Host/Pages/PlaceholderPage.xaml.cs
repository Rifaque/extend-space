using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ExtendSpace.Host.Pages;

/// <summary>What a not-yet-built section will contain.</summary>
public sealed record PlaceholderContent(string Title, string Glyph, string Description);

/// <summary>Empty state for navigation sections that have no functionality yet.</summary>
public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var content = (PlaceholderContent)e.Parameter;
        TitleText.Text = content.Title;
        Icon.Glyph = content.Glyph;
        DescriptionText.Text = content.Description;
    }
}
