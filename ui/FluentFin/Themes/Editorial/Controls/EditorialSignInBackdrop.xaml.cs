using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial's sign-in backdrop (see EditorialSignInBackdrop.xaml).</summary>
public sealed partial class EditorialSignInBackdrop : UserControl
{
	public EditorialSignInBackdrop()
	{
		InitializeComponent();
		SizeChanged += (_, e) => Layout(e.NewSize.Width);
	}

	/// <summary>Beside the centred form from 1500 px; a narrower window has the name small in the top corner, clear of the form.</summary>
	private void Layout(double width)
	{
		var wide = width >= 1500;
		Words.VerticalAlignment = wide ? VerticalAlignment.Center : VerticalAlignment.Top;
		Words.Margin = wide ? new Thickness(96, 0, 0, 0) : new Thickness(40, 28, 0, 0);
		NameText.FontSize = wide ? 200 : 88;
		NameText.LineHeight = wide ? 190 : 84;
		Arc.Width = wide ? 460 : 200;
		LineText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
	}
}
