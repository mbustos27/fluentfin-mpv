using CommunityToolkit.WinUI;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>Nixie 1.4.0: a library's list view row (see LibraryListRow.xaml).</summary>
public sealed partial class LibraryListRow : UserControl
{
	[GeneratedDependencyProperty]
	public partial BaseItemViewModel? Model { get; set; }

	public LibraryListRow()
	{
		InitializeComponent();
		KeyDown += Row_KeyDown;
	}

	partial void OnModelChanged(BaseItemViewModel? newValue) => Bindings.Update();

	/// <summary>"2019 · 1 h 52 min · Movie", "2008–2013 · Series".</summary>
	public static string Facts(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		var parts = new List<string>();
		if (dto.Type == BaseItemDto_Type.Series && Converters.BaseItemDtoConverters.SeriesYears(dto) is { Length: > 0 } years)
		{
			parts.Add(years);
		}
		else if (dto.ProductionYear is > 0 and var year)
		{
			parts.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}

		if (dto.Type != BaseItemDto_Type.Series && Converters.ItemRowConverters.Runtime(dto) is { Length: > 0 } runtime)
		{
			parts.Add(runtime);
		}

		if (!string.IsNullOrWhiteSpace(dto.OfficialRating))
		{
			parts.Add(dto.OfficialRating);
		}

		return string.Join(" · ", parts);
	}

	public static Visibility WatchedVisibility(BaseItemDto? dto) => dto?.UserData?.Played == true ? Visibility.Visible : Visibility.Collapsed;

	internal void Open()
	{
		if (Model?.Dto is { } dto)
		{
			App.Commands.DisplayDto(dto);
		}
	}

	private void Row_Tapped(object sender, TappedRoutedEventArgs e)
	{
		Open();
		e.Handled = true;
	}

	private void Row_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA && ReferenceEquals(e.OriginalSource, this))
		{
			Open();
			e.Handled = true;
		}
	}

	private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) =>
		RowRoot.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];

	private void Row_PointerExited(object sender, PointerRoutedEventArgs e) => RowRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

	protected override AutomationPeer OnCreateAutomationPeer() => new LibraryListRowAutomationPeer(this);
}

internal sealed partial class LibraryListRowAutomationPeer(LibraryListRow owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

	protected override string GetClassNameCore() => nameof(LibraryListRow);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(owner);
		return string.IsNullOrEmpty(name) ? ItemLabels.CardName(owner.Model) : name;
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

	public void Invoke() => owner.Open();
}
