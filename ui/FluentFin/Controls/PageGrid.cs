using FluentFin.Core.Theming;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0 (the UX review's plan item 13): a shared page on the active theme's page grid. <c>lc:PageGrid.Apply="True"</c>
/// on the page's root panel (a Grid, a StackPanel, a Border or a Control) gives it, as its padding, the theme's gutter on both
/// sides (PageGutterNarrow at a 1100 px window to PageGutterWide at 1920, <see cref="PageGridMetrics.Gutter"/>) and, above, the
/// room that puts the page's first line (its title) at the theme's PageTitleY in the window; its bottom padding stays its own.
/// A theme without the keys gets Classic's grid (36 px, y 72). <c>lc:PageGrid.Top="False"</c> keeps the element's own top.
/// </summary>
public static class PageGrid
{
	public static readonly DependencyProperty ApplyProperty =
		DependencyProperty.RegisterAttached("Apply", typeof(bool), typeof(PageGrid), new PropertyMetadata(false, OnApplyChanged));

	public static bool GetApply(DependencyObject element) => (bool)element.GetValue(ApplyProperty);

	public static void SetApply(DependencyObject element, bool value) => element.SetValue(ApplyProperty, value);

	public static readonly DependencyProperty TopProperty =
		DependencyProperty.RegisterAttached("Top", typeof(bool), typeof(PageGrid), new PropertyMetadata(true));

	public static bool GetTop(DependencyObject element) => (bool)element.GetValue(TopProperty);

	public static void SetTop(DependencyObject element, bool value) => element.SetValue(TopProperty, value);

	/// <summary>The active theme's gutter and title y (Classic's when it gives none).</summary>
	public static (double Narrow, double Wide, double TitleY) Tokens() =>
		(Number(PageGridMetrics.GutterNarrowKey) ?? PageGridMetrics.DefaultGutter,
		 Number(PageGridMetrics.GutterWideKey) ?? Number(PageGridMetrics.GutterNarrowKey) ?? PageGridMetrics.DefaultGutter,
		 Number(PageGridMetrics.TitleYKey) ?? PageGridMetrics.DefaultTitleY);

	/// <summary>
	/// A shared page's title as the theme sets its own (PageTitleUppercase: Drive-in's marquee and Editorial's big type are in
	/// capitals); the page keeps the title's own case for Narrator (AutomationProperties.Name).
	/// </summary>
	public static string Title(string? text) =>
		ThemeResources.Find("PageTitleUppercase") is true ? (text ?? "").ToUpper(System.Globalization.CultureInfo.CurrentCulture) : text ?? "";

	/// <summary>The gutter at a page width, from the active theme.</summary>
	public static double GutterAt(double width)
	{
		var (narrow, wide, _) = Tokens();
		return PageGridMetrics.Gutter(narrow, wide, width);
	}

	private static double? Number(string key) => ThemeResources.Find(key) switch
	{
		double d => d,
		int i => i,
		_ => null,
	};

	private static void OnApplyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not FrameworkElement element || e.NewValue is not true)
		{
			return;
		}

		element.SizeChanged += (_, _) => Update(element);
		element.Loaded += (_, _) => Update(element);
	}

	/// <summary>The padding for the element now (its page's width, and where its page starts in the window).</summary>
	public static void Update(FrameworkElement element)
	{
		var page = PageOf(element);
		var width = (page as FrameworkElement)?.ActualWidth is > 0 and var w ? w : element.ActualWidth;
		if (width <= 0)
		{
			return;
		}

		var (narrow, wide, titleY) = Tokens();
		var gutter = PageGridMetrics.Gutter(narrow, wide, width);
		var current = PaddingOf(element);
		if (current is null)
		{
			return;
		}

		var top = GetTop(element)
			? PageGridMetrics.TitleTop(titleY, TopBar.TopInset(barVisible: true, extendsUnder: page is not null && TopBar.GetExtendsUnder(page)))
			: current.Value.Top;
		var padding = new Thickness(gutter, top, gutter, current.Value.Bottom);
		if (!padding.Equals(current.Value))
		{
			SetPadding(element, padding);
		}
	}

	private static Page? PageOf(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is Page page)
			{
				return page;
			}
		}

		return null;
	}

	private static Thickness? PaddingOf(FrameworkElement element) => element switch
	{
		Grid g => g.Padding,
		StackPanel s => s.Padding,
		Border b => b.Padding,
		Control c => c.Padding,
		_ => null,
	};

	private static void SetPadding(FrameworkElement element, Thickness padding)
	{
		switch (element)
		{
			case Grid g: g.Padding = padding; break;
			case StackPanel s: s.Padding = padding; break;
			case Border b: b.Padding = padding; break;
			case Control c: c.Padding = padding; break;
		}
	}
}
