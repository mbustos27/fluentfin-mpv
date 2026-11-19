using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0 (the UX review's plan item 1): a film page's "Part of: Copper and Glass": the collections the film is in
/// (MovieViewModel.PartOf, Jellyfin 10.11's /Items/{id}/Collections), each a link on the Link role that opens its Collection
/// page. Nothing is drawn when the film is in none (or the server cannot say). Shared by every theme's movie page:
/// <c>&lt;lc:PartOfLink Collections="{x:Bind ViewModel.PartOf, Mode=OneWay}" /&gt;</c>.
/// </summary>
public sealed partial class PartOfLink : UserControl
{
	public static readonly DependencyProperty CollectionsProperty =
		DependencyProperty.Register(nameof(Collections), typeof(IReadOnlyList<BaseItemDto>), typeof(PartOfLink), new PropertyMetadata(null, (d, _) => ((PartOfLink)d).Build()));

	/// <summary>The collections the film is part of.</summary>
	public IReadOnlyList<BaseItemDto>? Collections
	{
		get => (IReadOnlyList<BaseItemDto>?)GetValue(CollectionsProperty);
		set => SetValue(CollectionsProperty, value);
	}

	private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

	public PartOfLink()
	{
		IsTabStop = false;
		Content = _row;
		Build();
	}

	/// <summary>The links shown (tests and the harness).</summary>
	internal IEnumerable<HyperlinkButton> Links => _row.Children.OfType<HyperlinkButton>();

	private void Build()
	{
		_row.Children.Clear();
		var collections = (Collections ?? []).Where(c => c.Id is not null && !string.IsNullOrWhiteSpace(c.Name)).ToList();
		Visibility = collections.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
		if (collections.Count == 0)
		{
			return;
		}

		_row.Children.Add(new TextBlock
		{
			Text = "Part of:",
			VerticalAlignment = VerticalAlignment.Center,
			Style = Application.Current.Resources["GlassMetaTextBlockStyle"] as Style,
			FontSize = 14,
		});
		for (var i = 0; i < collections.Count; i++)
		{
			var collection = collections[i];
			var link = new HyperlinkButton
			{
				Content = collection.Name!.Trim(),
				VerticalAlignment = VerticalAlignment.Center,
				Style = Application.Current.Resources["NixieLinkButtonStyle"] as Style,
			};
			AutomationProperties.SetName(link, ItemMeta.PartOfName(collection));
			ToolTipService.SetToolTip(link, $"Open the collection {collection.Name!.Trim()}");
			link.Click += (_, _) => App.Commands.DisplayDto(collection);
			_row.Children.Add(link);
			if (i < collections.Count - 1)
			{
				_row.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Style = Application.Current.Resources["GlassMetaTextBlockStyle"] as Style });
			}
		}
	}
}
