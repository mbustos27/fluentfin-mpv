using System.ComponentModel;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>Nixie 1.4.0: the item page's eyebrow, the library it is in when reached from another section (TrailEyebrow.xaml).</summary>
public sealed partial class TrailEyebrow : UserControl
{
	private readonly TopNavigation _top = App.GetService<TopNavigation>();

	public TrailEyebrow()
	{
		InitializeComponent();
		Loaded += (_, _) =>
		{
			_top.PropertyChanged -= OnTopChanged;
			_top.PropertyChanged += OnTopChanged;
			Show();
		};
		Unloaded += (_, _) => _top.PropertyChanged -= OnTopChanged;
	}

	private void OnTopChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(TopNavigation.Eyebrow))
		{
			DispatcherQueue.TryEnqueue(Show);
		}
	}

	private void Show()
	{
		var library = _top.Eyebrow;
		Label.Text = library?.Title ?? "";
		AutomationProperties.SetName(LibraryButton, library?.Title ?? "");
		Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
	}

	private void OnClick(object sender, RoutedEventArgs e)
	{
		if (_top.Eyebrow is { } library)
		{
			_top.Open(library);
		}
	}
}
