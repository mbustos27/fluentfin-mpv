using Microsoft.UI.Xaml.Data;

namespace FluentFin.Converters;

/// <summary>
/// Nixie 1.4.0: a button's content as text, or "" when the content is an element. The Tab, section-tab, chip and letter
/// templates keep the bold label's width with a hidden copy of the label (so nothing moves when the selection moves); bound to
/// an element, that copy printed the element's type name ("Microsoft.UI.Xaml.Controls.StackPanel") and widened the button.
/// </summary>
public sealed partial class TextOnlyConverter : IValueConverter
{
	public static string TextOf(object? value) => value as string ?? "";

	public object Convert(object value, Type targetType, object parameter, string language) => TextOf(value);

	public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
