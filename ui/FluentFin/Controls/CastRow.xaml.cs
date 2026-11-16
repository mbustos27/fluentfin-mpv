using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>Seaglass: a row of round cast photos; hidden when <see cref="People"/> is empty (see CastRow.xaml).</summary>
public sealed partial class CastRow : UserControl
{
	public static readonly DependencyProperty PeopleProperty =
		DependencyProperty.Register(nameof(People), typeof(object), typeof(CastRow), new PropertyMetadata(null, (d, _) => ((CastRow)d).Apply()));

	public static readonly DependencyProperty HeadingProperty =
		DependencyProperty.Register(nameof(Heading), typeof(string), typeof(CastRow), new PropertyMetadata("Cast & Crew", (d, _) => ((CastRow)d).Apply()));

	public CastRow()
	{
		InitializeComponent();
		Apply();
	}

	public IEnumerable<BaseItemPerson>? People
	{
		get => GetValue(PeopleProperty) as IEnumerable<BaseItemPerson>;
		set => SetValue(PeopleProperty, value);
	}

	public string Heading
	{
		get => (string)GetValue(HeadingProperty);
		set => SetValue(HeadingProperty, value);
	}

	private void Apply()
	{
		var people = People?.ToList() ?? [];
		HeadingText.Text = Heading;
		Repeater.ItemsSource = people;
		Visibility = people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}
}
