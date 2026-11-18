using Microsoft.UI.Xaml.Data;

namespace FluentFin.Converters;

/// <summary>
/// Nixie 1.2.0 (admin): a nullable number for a NumberBox, whose "empty" is NaN: no value shows an empty box, and an empty
/// box goes back as no value (NullableDoubleToValueOrDefaultConverter showed 0, and a Save sent the 0).
/// </summary>
public partial class NullableDoubleToNaNConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language) => value is double number ? number : double.NaN;

	public object? ConvertBack(object value, Type targetType, object parameter, string language) => value is double number && !double.IsNaN(number) ? number : null;
}
