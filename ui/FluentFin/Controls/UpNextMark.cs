using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>An episode in a list that marks the one up next (<see cref="UpNextMark"/>): it shows or drops its mark.</summary>
public interface IUpNextMarked
{
	void UpdateUpNext();
}

/// <summary>
/// Nixie 1.4.0 (the UX review's X12 and C-m10, plan item 9): the season page's list marks "Up next" on the episode its Play
/// starts ("Play S1 E4"), in every theme (Drive-in's showtimes board had its NEXT; the others had nothing linking the two).
/// The page sets <c>EpisodeId</c> on the list (the view model's PlayTarget.EpisodeId); each episode in it
/// (<see cref="IUpNextMarked"/>) asks <see cref="IsUpNext"/> when it shows, and is told again when the id changes.
/// </summary>
public static class UpNextMark
{
	/// <summary>The words of the mark, the same in every theme.</summary>
	public const string Label = "Up next";

	public static readonly DependencyProperty EpisodeIdProperty =
		DependencyProperty.RegisterAttached("EpisodeId", typeof(object), typeof(UpNextMark), new PropertyMetadata(null, OnEpisodeIdChanged));

	public static object? GetEpisodeId(DependencyObject element) => element.GetValue(EpisodeIdProperty);

	public static void SetEpisodeId(DependencyObject element, object? value) => element.SetValue(EpisodeIdProperty, value);

	/// <summary>Whether <paramref name="episodeId"/> is the one the nearest list above <paramref name="element"/> marks.</summary>
	public static bool IsUpNext(DependencyObject element, Guid? episodeId)
	{
		for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node.ReadLocalValue(EpisodeIdProperty) != DependencyProperty.UnsetValue)
			{
				return Marks(GetEpisodeId(node), episodeId);
			}
		}

		return false;
	}

	/// <summary>The list's id (a Guid, or null while there is none up next) marks this episode.</summary>
	public static bool Marks(object? marked, Guid? episodeId) => episodeId is { } id && marked is Guid up && up == id;

	/// <summary>Narrator's name of a marked episode: its own name, then ", up next".</summary>
	public static string Spoken(string name, bool upNext) => upNext && name.Length > 0 ? $"{name}, up next" : name;

	private static void OnEpisodeIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Update(d);

	private static void Update(DependencyObject node)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
		{
			var child = VisualTreeHelper.GetChild(node, i);
			if (child is IUpNextMarked marked)
			{
				marked.UpdateUpNext();
			}

			Update(child);
		}
	}
}
