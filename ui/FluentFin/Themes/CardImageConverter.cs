using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Data;

namespace FluentFin.Themes;

/// <summary>
/// Nixie 1.3.0: a card's image for a {Binding} in a theme dictionary's DataTemplates (which have no code-behind, so no x:Bind
/// to <see cref="BaseItemDtoConverters"/>): the item's <see cref="ImageType"/> image at <see cref="Height"/> pixels, or its
/// blur hash when <see cref="BlurHash"/> is set. The value is a BaseItemViewModel or a BaseItemDto.
/// </summary>
public sealed partial class CardImageConverter : IValueConverter
{
	public ImageType ImageType { get; set; } = ImageType.Primary;

	public double Height { get; set; } = 302;

	public bool BlurHash { get; set; }

	public object? Convert(object value, Type targetType, object parameter, string language)
	{
		var dto = value switch
		{
			BaseItemViewModel model => model.Dto,
			BaseItemDto item => item,
			_ => null,
		};

		return BlurHash ? BaseItemDtoConverters.GetBlurHash(dto, ImageType, Height) : BaseItemDtoConverters.GetImage(dto, ImageType, Height);
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
