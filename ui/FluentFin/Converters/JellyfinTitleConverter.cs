using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Data;
namespace FluentFin.Converters;

public partial class JellyfinTitleConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		if (value is not BaseItemDto bid)
		{
			return "";
		}

		if (bid.Type == BaseItemDto_Type.Episode)
		{
			return bid.SeriesName ?? "";
		}

		return FluentFin.Core.ViewModels.CollectionNames.Display(bid); // 1.6 collections: a box set's tidy name (the server's for anything else)
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotSupportedException();
	}
}

public partial class JellyfinSubtitleConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		if (value is not BaseItemDto bid)
		{
			return "";
		}

		// 1.2.0: the card's line, as BaseItemDtoConverters.GetCardSubtitle writes it (one episode format, en-dash years)
		return BaseItemDtoConverters.GetCardSubtitle(bid);
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotSupportedException();
	}
}
