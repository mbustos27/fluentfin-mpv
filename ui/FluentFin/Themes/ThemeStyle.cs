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

	/// <summary>
	/// A style that is <paramref name="own"/> with <paramref name="theme"/> over it: the theme's style itself is the base (its
	/// setters, {ThemeResource} values included, stay the theme's, and follow Light, Dark and high contrast), and the
	/// element's own setters for properties the theme leaves out are copied on top. Nixie 1.3.0: copying the theme's setters
	/// instead lost their {ThemeResource} values (Drive-in's console came out with no fill or rim). The element's own
	/// styles take {StaticResource} values only.
	/// </summary>
	public static Style Layer(Style? own, Style theme)
	{
		if (own is null)
		{
			return theme;
		}

		var type = own.TargetType is { } ownType && theme.TargetType is { } themeType && themeType.IsAssignableFrom(ownType) ? ownType : theme.TargetType ?? own.TargetType ?? typeof(FrameworkElement);
		var layered = new Style(type) { BasedOn = theme };
		var themed = Chain(theme).SelectMany(s => s.Setters.OfType<Setter>()).Select(s => s.Property).Where(p => p is not null).ToHashSet();
		foreach (var (property, value) in Kept(Chain(own).Select(s => s.Setters.OfType<Setter>().Where(x => x.Property is not null).Select(x => (x.Property, x.Value))), themed))
		{
			layered.Setters.Add(new Setter(property, value));
		}

		return layered;
	}

	/// <summary>A style's BasedOn chain, the base first.</summary>
	private static IEnumerable<Style> Chain(Style style)
	{
		var chain = new Stack<Style>();
		for (var s = style; s is not null; s = s.BasedOn)
		{
			chain.Push(s);
		}

		return chain;
	}

	/// <summary>
	/// The element's own setters that stay over the theme's style: its chain's (the base first, the most derived setter of a
	/// property winning, in first-set order), less every property the theme sets.
	/// </summary>
	public static List<(TProperty Property, TValue Value)> Kept<TProperty, TValue>(IEnumerable<IEnumerable<(TProperty Property, TValue Value)>> ownChain, ISet<TProperty> themed)
		where TProperty : notnull
	{
		var values = new Dictionary<TProperty, TValue>();
		var order = new List<TProperty>();
		foreach (var style in ownChain)
		{
			foreach (var (property, value) in style)
			{
				if (themed.Contains(property))
				{
					continue;
				}

				if (!values.ContainsKey(property))
				{
					order.Add(property);
				}

				values[property] = value;
			}
		}

		return [.. order.Select(p => (p, values[p]))];
	}
}
