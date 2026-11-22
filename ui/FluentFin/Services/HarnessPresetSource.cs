using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Services;

/// <summary>
/// 1.6 colpage (review Major 4): the off-screen harness's stand-in for the collections stream's presets, so the strip can be
/// seen and driven with many Nixie collections before that stream merges. Only a dev build under the harness
/// (FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE=1, never a release package) with <c>NIXIE_PRESETS=&lt;n&gt;</c> registers it: n presets
/// (at most 12), each every k-th title of the library, answered 1.5 s late (as an index read in the background is).
/// </summary>
public sealed class HarnessPresetSource(int count) : ILibraryPresetSource
{
	public const string CountVariable = "PRESETS";

	private static readonly string[] Names =
	[
		"Heist", "The 1990s", "Franchises", "Highly rated, not watched", "Short films", "The 1980s", "Space", "Road trips",
		"Westerns", "Unfinished series", "Courtroom", "Directed by Kestrel",
	];

	public event EventHandler? Changed
	{
		add { }
		remove { }
	}

	/// <summary>The harness's source, or null when this run asks for none (or isn't a harness run of a dev build).</summary>
	public static HarnessPresetSource? FromEnvironment() =>
		Environment.GetEnvironmentVariable(GlassActivity.AlwaysActiveVariable) == "1" && !AppVersion.IsReleasePackage
		&& int.TryParse(AppIdentity.ReadEnvironment(CountVariable), out var n) && n > 0
			? new HarnessPresetSource(Math.Min(n, Names.Length))
			: null;

	public async Task<IReadOnlyList<LibraryPreset>> PresetsForAsync(BaseItemDto library, IReadOnlyList<BaseItemDto> titles, CancellationToken cancellationToken)
	{
		await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
		return [.. Names.Take(count).Select((name, k) => LibraryPreset.ForCollection(
			$"harness:{k}", name, titles.Where((_, i) => i % (k + 2) == 0).Select(t => t.Id).OfType<Guid>().ToHashSet()))];
	}
}
