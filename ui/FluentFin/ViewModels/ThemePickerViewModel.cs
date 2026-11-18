using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Services;
using FluentFin.Themes;

namespace FluentFin.ViewModels;

/// <summary>
/// Nixie 1.3.0: the layout theme picker (Settings &gt; Appearance, and the one-time "Choose a look" step): one option per
/// theme the app has, in <see cref="LayoutThemeId"/> order, the one in use marked, and the active theme's own options block
/// (<see cref="ILayoutTheme.CreateOptions"/>). In Settings a choice applies at once (<c>apply</c>); in "Choose a look" it is
/// only selected until the step's button.
/// </summary>
public sealed partial class ThemePickerViewModel : ObservableObject
{
	private readonly ThemeService _themes;
	private readonly Func<LayoutThemeId, Task>? _apply;
	private bool _syncing;

	public ThemePickerViewModel(ThemeService themes, Func<LayoutThemeId, Task>? apply, LayoutThemeId? preselect = null)
	{
		_themes = themes;
		_apply = apply;
		Options = [.. themes.Themes.Select(t => new ThemeOption(t.Id, t.DisplayName, ThemeCatalog.Description(t.Id)))];
		themes.ActiveChanged += OnActiveChanged;
		Refresh(preselect ?? themes.ActiveId);
	}

	public IReadOnlyList<ThemeOption> Options { get; }

	/// <summary>Whether there is a choice to make (more than one theme in this build): the picker is hidden otherwise.</summary>
	public bool HasChoice => Options.Count > 1;

	[ObservableProperty]
	public partial ThemeOption? Selected { get; set; }

	/// <summary>The active theme's own block for Settings, null for none.</summary>
	[ObservableProperty]
	public partial object? OptionsBlock { get; set; }

	/// <summary>"Drive-in options".</summary>
	[ObservableProperty]
	public partial string OptionsHeader { get; set; } = "";

	public bool HasOptions => OptionsBlock is not null;

	partial void OnOptionsBlockChanged(object? value) => OnPropertyChanged(nameof(HasOptions));

	partial void OnSelectedChanged(ThemeOption? value)
	{
		if (_syncing || value is null || _apply is null)
		{
			return;
		}

		_ = _apply(value.Id);
	}

	private void OnActiveChanged(object? sender, EventArgs e) => Refresh(_apply is null ? Selected?.Id : _themes.ActiveId);

	private void Refresh(LayoutThemeId? select)
	{
		var active = _themes.ActiveId;
		foreach (var option in Options)
		{
			option.IsActive = option.Id == active;
		}

		_syncing = true;
		try
		{
			// a choice of a theme this build does not have shows the one in use
			Selected = Options.FirstOrDefault(o => o.Id == select) ?? Options.FirstOrDefault(o => o.IsActive);
		}
		finally
		{
			_syncing = false;
		}

		OptionsBlock = _apply is null ? null : _themes.Active?.CreateOptions();
		OptionsHeader = _themes.Active is { } theme ? $"{theme.DisplayName} options" : "";
	}

	/// <summary>Stops following the theme (the page or the step went).</summary>
	public void Detach() => _themes.ActiveChanged -= OnActiveChanged;
}

/// <summary>One theme in the picker.</summary>
public sealed partial class ThemeOption(LayoutThemeId id, string name, string description) : ObservableObject
{
	public LayoutThemeId Id { get; } = id;

	public string Name { get; } = name;

	public string Description { get; } = description;

	/// <summary>The theme shown now ("In use").</summary>
	[ObservableProperty]
	public partial bool IsActive { get; set; }

	/// <summary>What Narrator reads for the option: its name, then what it is.</summary>
	public string AutomationName => $"{Name}. {Description}";

	public override string ToString() => Name;
}

/// <summary>Nixie 1.3.0: what each theme is, in a line (the picker's text under each preview).</summary>
public static class ThemeCatalog
{
	public static string Description(LayoutThemeId id) => id switch
	{
		LayoutThemeId.DriveIn => "A drive-in at dusk: marquee rows, nixie numbers.",
		LayoutThemeId.Cinematic => "Big artwork, floating glass, one shelf at a time.",
		LayoutThemeId.Editorial => "Dense and fast: big type, details open in place.",
		LayoutThemeId.Classic => "The 1.2 layout: a glass bar, a big hero and rows of cards.",
		_ => "",
	};
}
