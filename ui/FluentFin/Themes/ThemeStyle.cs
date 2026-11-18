using FluentFin.Services;
using Microsoft.UI.Xaml;

namespace FluentFin.Themes;

/// <summary>
/// Nixie 1.3.0: a shared element the active layout theme restyles in place, without the page being rebuilt (the player's
/// overlay: it re-templates during playback). <c>themes:ThemeStyle.Key="PlayerBottomBarStyle"</c> on an element applies the
/// theme's Style of that key over the element's own style (its setters win; what it leaves out stays the element's), and
/// again on every switch; a theme without the key leaves the element's own style.
/// </summary>
public static class ThemeStyle
{
	public static readonly DependencyProperty KeyProperty =
		DependencyProperty.RegisterAttached("Key", typeof(string), typeof(ThemeStyle), new PropertyMetadata(null, OnKeyChanged));

	public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

	public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

	/// <summary>The element's own style, taken the first time it is themed.</summary>
	private static readonly DependencyProperty BaseStyleProperty =
		DependencyProperty.RegisterAttached("BaseStyle", typeof(Style), typeof(ThemeStyle), new PropertyMetadata(null));

	private static readonly DependencyProperty HasBaseProperty =
		DependencyProperty.RegisterAttached("HasBase", typeof(bool), typeof(ThemeStyle), new PropertyMetadata(false));

	private static readonly List<WeakReference<FrameworkElement>> _elements = [];

	static ThemeStyle() => ThemeResources.Applied += (_, _) => ApplyAll();

	private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not FrameworkElement element)
		{
			return;
		}

		lock (_elements)
		{
			_elements.RemoveAll(w => !w.TryGetTarget(out var alive) || ReferenceEquals(alive, element));
			_elements.Add(new WeakReference<FrameworkElement>(element));
		}

		// after the element's own Style is set (attribute order does not matter)
		if (element.IsLoaded)
		{
			Apply(element);
		}
		else
		{
			element.Loaded += OnFirstLoaded;
		}
	}

	private static void OnFirstLoaded(object sender, RoutedEventArgs e)
	{
		var element = (FrameworkElement)sender;
		element.Loaded -= OnFirstLoaded;
		Apply(element);
	}

	private static void ApplyAll()
	{
		List<FrameworkElement> alive;
		lock (_elements)
		{
			_elements.RemoveAll(w => !w.TryGetTarget(out _));
			alive = [.. _elements.Select(w => w.TryGetTarget(out var e) ? e : null).OfType<FrameworkElement>()];
		}

		foreach (var element in alive)
		{
			Apply(element);
		}
	}

	/// <summary>The theme's style for the element's key over the element's own style, or its own style alone.</summary>
	public static void Apply(FrameworkElement element)
	{
		if (!(bool)element.GetValue(HasBaseProperty))
		{
			element.SetValue(BaseStyleProperty, element.Style);
			element.SetValue(HasBaseProperty, true);
		}

		var own = (Style?)element.GetValue(BaseStyleProperty);
		var theme = GetKey(element) is { } key ? ThemeResources.Find(key) as Style : null;
		try
		{
			element.Style = theme is not null && Fits(theme, element) ? Layer(own, theme) : own;
		}
		catch (Exception)
		{
			// a theme style that does not fit the element's own: the element keeps its look
			element.Style = own;
		}
	}

	private static bool Fits(Style style, FrameworkElement element) => style.TargetType is null || style.TargetType.IsInstanceOfType(element);

	/// <summary>A style that is <paramref name="own"/> with <paramref name="theme"/>'s setters (its BasedOn chain's too) over it.</summary>
	public static Style Layer(Style? own, Style theme)
	{
		var layered = new Style(own?.TargetType ?? theme.TargetType ?? typeof(FrameworkElement)) { BasedOn = own };
		var chain = new Stack<Style>();
		for (var s = theme; s is not null; s = s.BasedOn)
		{
			chain.Push(s);
		}

		// the most derived setter of a property wins, as in the theme's own chain
		var values = new Dictionary<DependencyProperty, object?>();
		var order = new List<DependencyProperty>();
		foreach (var style in chain)
		{
			foreach (var setter in style.Setters.OfType<Setter>().Where(s => s.Property is not null))
			{
				if (!values.ContainsKey(setter.Property))
				{
					order.Add(setter.Property);
				}

				values[setter.Property] = setter.Value;
			}
		}

		foreach (var property in order)
		{
			layered.Setters.Add(new Setter(property, values[property]));
		}

		return layered;
	}
}
