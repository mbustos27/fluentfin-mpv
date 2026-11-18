using System.Globalization;
using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Data;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: a card's artwork for the theme dictionary's card templates (DriveIn.xaml is loaded by its address, so its
/// templates use bindings with these converters, not x:Bind). The parameter is "&lt;ImageType&gt;,&lt;size&gt;", the same
/// image and size the shared pages ask for (BaseItemDtoConverters).
/// </summary>
public sealed partial class DriveInImageConverter : IValueConverter
{
	/// <summary>The blur hash placeholder instead of the image.</summary>
	public bool BlurHash { get; set; }

	public object? Convert(object value, Type targetType, object parameter, string language)
	{
		var dto = value switch
		{
			BaseItemViewModel model => model.Dto,
			BaseItemDto item => item,
			_ => null,
		};
		if (dto is null || Parse(parameter as string) is not { } spec)
		{
			return null;
		}

		return BlurHash ? BaseItemDtoConverters.GetBlurHash(dto, spec.Type, spec.Size) : BaseItemDtoConverters.GetImage(dto, spec.Type, spec.Size);
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();

	/// <summary>"Primary,302" as the image type and size; null when it does not read.</summary>
	public static (ImageType Type, double Size)? Parse(string? parameter)
	{
		var parts = (parameter ?? "").Split(',', StringSplitOptions.TrimEntries);
		return parts.Length == 2 && Enum.TryParse<ImageType>(parts[0], ignoreCase: true, out var type)
			&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0
			? (type, size)
			: null;
	}
}

/// <summary>
/// Drive-in: gives a shared card its item from a binding. DriveIn.xaml's templates are loaded by address (no x:Bind), and a
/// plain binding does not reach the card's Model (its type is not a Windows Runtime one): <c>dc:DriveInCards.Item="{Binding}"</c>
/// sets the card's Model, its artwork (<see cref="ImageProperty"/>: "Primary,302") and its blur hash in code.
/// </summary>
public static class DriveInCards
{
	public static readonly Microsoft.UI.Xaml.DependencyProperty ItemProperty =
		Microsoft.UI.Xaml.DependencyProperty.RegisterAttached("Item", typeof(object), typeof(DriveInCards), new Microsoft.UI.Xaml.PropertyMetadata(null, (d, _) => Apply(d)));

	public static readonly Microsoft.UI.Xaml.DependencyProperty ImageProperty =
		Microsoft.UI.Xaml.DependencyProperty.RegisterAttached("Image", typeof(string), typeof(DriveInCards), new Microsoft.UI.Xaml.PropertyMetadata("", (d, _) => Apply(d)));

	public static object? GetItem(Microsoft.UI.Xaml.DependencyObject d) => d.GetValue(ItemProperty);

	public static void SetItem(Microsoft.UI.Xaml.DependencyObject d, object? value) => d.SetValue(ItemProperty, value);

	public static string GetImage(Microsoft.UI.Xaml.DependencyObject d) => (string)d.GetValue(ImageProperty);

	public static void SetImage(Microsoft.UI.Xaml.DependencyObject d, string value) => d.SetValue(ImageProperty, value);

	private static void Apply(Microsoft.UI.Xaml.DependencyObject d)
	{
		if (d is not FluentFin.Controls.BaseItemDtoCard card)
		{
			return;
		}

		var model = GetItem(d) as BaseItemViewModel;
		card.Model = model;
		if (model is null || DriveInImageConverter.Parse(GetImage(d)) is not { } spec)
		{
			card.ImageSource = null;
			card.BlurHashImageSource = null;
			return;
		}

		card.BlurHashImageSource = BaseItemDtoConverters.GetBlurHash(model.Dto, spec.Type, spec.Size);
		card.ImageSource = BaseItemDtoConverters.GetImage(model.Dto, spec.Type, spec.Size);
	}
}
