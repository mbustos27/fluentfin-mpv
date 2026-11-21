using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Controls;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.Cinematic.Views;

/// <summary>
/// The first screen of a Cinematic item page: the stage is the window's height (never under 640), and a window under
/// 1400 px takes the narrow sizes. The page passes what else changes with the width.
/// </summary>
internal sealed class CinematicItemPageLayout
{
	public const double NarrowBelow = 1400;
	public const double MinStage = 640;

	private readonly ScrollViewer _scroller;
	private readonly CinematicItemStage _stage;
	private readonly Action<bool>? _narrowChanged;
	private bool? _narrow;

	public CinematicItemPageLayout(Page page, ScrollViewer scroller, CinematicItemStage stage, Action<bool>? narrowChanged = null)
	{
		_scroller = scroller;
		_stage = stage;
		_narrowChanged = narrowChanged;
		scroller.SizeChanged += (_, _) => Update();
		page.Loaded += (_, _) => Update();
	}

	public bool IsNarrow => _narrow == true;

	private void Update()
	{
		var width = _scroller.ActualWidth;
		var height = _scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : _scroller.ActualHeight;
		if (width <= 0 || height <= 0)
		{
			return;
		}

		_stage.StageHeight = Math.Max(MinStage, height);
		var narrow = width < NarrowBelow;
		if (_narrow == narrow)
		{
			return;
		}

		_narrow = narrow;
		_stage.IsNarrow = narrow;
		_narrowChanged?.Invoke(narrow);
	}
}

/// <summary>x:Bind helpers of the series and season views.</summary>
public static class CinematicSeriesSupport
{
	/// <summary>A season's episodes as the stills' item models (the shelf's items).</summary>
	public static List<BaseItemViewModel> Wrap(List<BaseItemDto>? episodes) => episodes is null ? [] : [.. episodes.Select(BaseItemViewModel.FromDto)];

	public static string Summary(List<BaseItemDto>? episodes) => CinematicLabels.SeasonSummary(episodes);

	/// <summary>Nixie 1.5: the episode stills' DecodeWidth in the series and season pages' EpisodeLarge and EpisodeSmall (StillsAhead).</summary>
	public const int LargeStillWidth = 600, SmallStillWidth = 520;
}
