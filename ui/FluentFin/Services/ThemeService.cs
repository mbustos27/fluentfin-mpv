using System.Reflection;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Themes;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.0: which layout theme is shown. The themes are found in the app's assembly (<see cref="LayoutThemes.Discover"/>);
/// the choice is a per-PC setting (<see cref="ThemeChoice.SettingKey"/>), decided once on the first start of 1.3
/// (<see cref="ThemeChoice.FirstChoice"/>), and <c>NIXIE_LAYOUT</c> (DriveIn, Cinematic, Editorial, Classic) overrides it
/// for a run without saving anything. A theme that is missing falls back to the next one, ending at Classic
/// (<see cref="ThemeChoice.Resolve"/>). No XAML here: <see cref="ThemeResources"/> follows <see cref="ActiveChanged"/>.
/// </summary>
public sealed class ThemeService
{
	private readonly ILocalSettingsService _settings;

	public ThemeService(ILocalSettingsService settings, IReadOnlyList<ILayoutTheme> themes, string? layoutOverride)
	{
		_settings = settings;
		Themes = themes;

		if (ThemeChoice.Parse(ThemeChoice.ReadString(settings.ReadSettingRaw(ThemeChoice.SettingKey))) is { } saved)
		{
			Chosen = saved;
		}
		else
		{
			// the first start of 1.3 on this PC: an install that has used 1.2.x keeps its layout (Classic), a new one gets the default
			IsFirstStartAfterUpgrade = ThemeChoice.IsUpgrade(settings.ReadSettingRaw(ThemeChoice.ServersKey));
			Chosen = ThemeChoice.FirstChoice(IsFirstStartAfterUpgrade);
			settings.SaveSetting(ThemeChoice.SettingKey, Chosen.ToString());
		}

		Override = ThemeChoice.Parse(layoutOverride);
		Active = ThemeChoice.Resolve(Override ?? Chosen, themes);
	}

	/// <summary>Every theme the app has, in <see cref="LayoutThemeId"/> order.</summary>
	public IReadOnlyList<ILayoutTheme> Themes { get; }

	/// <summary>The saved choice.</summary>
	public LayoutThemeId Chosen { get; private set; }

	/// <summary><c>NIXIE_LAYOUT</c>'s theme for this run, null when it is not set (or names no theme).</summary>
	public LayoutThemeId? Override { get; }

	/// <summary>True when this start decided the choice for an install upgraded from 1.2.x (it starts on Classic).</summary>
	public bool IsFirstStartAfterUpgrade { get; }

	/// <summary>The theme shown: the override's or the choice's, else the next one there is; null only with no theme at all (the shared pages).</summary>
	public ILayoutTheme? Active { get; private set; }

	public LayoutThemeId? ActiveId => Active?.Id;

	/// <summary>Raised after <see cref="Active"/> changed (on the thread that chose it).</summary>
	public event EventHandler? ActiveChanged;

	/// <summary>Saves <paramref name="id"/> as this PC's choice and shows it (or the next theme there is). True when the shown theme changed.</summary>
	public bool Choose(LayoutThemeId id)
	{
		Chosen = id;
		_settings.SaveSetting(ThemeChoice.SettingKey, id.ToString());
		var next = ThemeChoice.Resolve(id, Themes);
		if (ReferenceEquals(next, Active))
		{
			return false;
		}

		Active = next;
		ActiveChanged?.Invoke(this, EventArgs.Empty);
		return true;
	}

	/// <summary>
	/// The active theme's page for a view model, or null for the shared page: for a view model that is not themable
	/// (<see cref="ThemeContract.IsThemable"/>), a theme with no view for it, or a view that is not a Page.
	/// </summary>
	public Type? ViewFor(Type viewModelType) => ViewFor(Active, viewModelType);

	/// <summary><paramref name="theme"/>'s page for a view model, by the same rules as <see cref="ViewFor(Type)"/>.</summary>
	public static Type? ViewFor(ILayoutTheme? theme, Type viewModelType)
	{
		if (theme is null || !ThemeContract.IsThemable(viewModelType))
		{
			return null;
		}

		try
		{
			return theme.ViewFor(viewModelType) is { } view && typeof(Page).IsAssignableFrom(view) && !view.IsAbstract ? view : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Whether the one-time "Choose a look" step is still to be offered (after a sign-in).</summary>
	public bool ShouldOfferChooseLook => !_settings.ReadSetting(ThemeChoice.ChooseLookOfferedKey, false);

	/// <summary>The theme "Choose a look" starts on: the saved choice (Classic after an upgrade, else the new installs' default).</summary>
	public LayoutThemeId ChooseLookPreselect => Chosen;

	/// <summary>"Choose a look" was shown (chosen or skipped): it is not offered again on this PC.</summary>
	public void ChooseLookOffered() => _settings.SaveSetting(ThemeChoice.ChooseLookOfferedKey, true);
}

/// <summary>Nixie 1.3.0: the rules of the layout choice, apart from any state (ThemeService, the tests).</summary>
public static class ThemeChoice
{
	/// <summary>The theme a new install starts on (the user, 2026-11-17: Drive-in). Upgrades from 1.2.x start on <see cref="ForUpgrades"/>.</summary>
	public const LayoutThemeId DefaultForNewInstalls = LayoutThemeId.DriveIn;

	/// <summary>The theme an install upgraded from 1.2.x starts on: 1.2.0's own layout, so nothing changes under the user's feet.</summary>
	public const LayoutThemeId ForUpgrades = LayoutThemeId.Classic;

	/// <summary>The per-PC setting with the chosen theme's <see cref="LayoutThemeId"/> name.</summary>
	public const string SettingKey = "LayoutTheme";

	/// <summary>The per-PC setting that says "Choose a look" was offered.</summary>
	public const string ChooseLookOfferedKey = "ChooseLookOffered";

	/// <summary>The saved servers (SettingKeys.Servers): an install with one has been used before 1.3.</summary>
	public const string ServersKey = "Servers";

	/// <summary>The first choice on a PC: Classic for an install upgraded from 1.2.x, else <see cref="DefaultForNewInstalls"/>.</summary>
	public static LayoutThemeId FirstChoice(bool upgraded) => upgraded ? ForUpgrades : DefaultForNewInstalls;

	/// <summary>An install has been used before (1.2.x) when it has a saved server.</summary>
	public static bool IsUpgrade(JsonNode? servers) => servers is JsonArray { Count: > 0 };

	/// <summary>A theme's id from a setting or <c>NIXIE_LAYOUT</c>: its name in any case, with or without a hyphen or space ("drive-in"); null for anything else.</summary>
	public static LayoutThemeId? Parse(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var name = value.Trim().Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
		return Enum.TryParse<LayoutThemeId>(name, ignoreCase: true, out var id) && Enum.IsDefined(id) && !int.TryParse(name, out _) ? id : null;
	}

	internal static string? ReadString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	/// <summary>
	/// The theme shown for <paramref name="requested"/>: it, else the next one in <see cref="LayoutThemeId"/> order that is
	/// there (Classic is last, so the chain ends at Classic); null when none is (the shared pages).
	/// </summary>
	public static ILayoutTheme? Resolve(LayoutThemeId requested, IReadOnlyList<ILayoutTheme> available)
	{
		foreach (var id in Enum.GetValues<LayoutThemeId>().Where(id => id >= requested))
		{
			if (available.FirstOrDefault(t => t.Id == id) is { } theme)
			{
				return theme;
			}
		}

		return null;
	}
}

/// <summary>Nixie 1.3.0: the themes in an assembly.</summary>
public static class LayoutThemes
{
	/// <summary>
	/// One instance of every non-abstract <see cref="ILayoutTheme"/> class with a public parameterless constructor in
	/// <paramref name="assembly"/>, in <see cref="LayoutThemeId"/> order, the first (by type name) for an id two classes claim.
	/// A theme whose constructor throws is left out (its id then falls back).
	/// </summary>
	public static IReadOnlyList<ILayoutTheme> Discover(Assembly assembly)
	{
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			types = [.. ex.Types.OfType<Type>()];
		}

		var themes = new List<ILayoutTheme>();
		foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && typeof(ILayoutTheme).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null).OrderBy(t => t.FullName, StringComparer.Ordinal))
		{
			try
			{
				if (Activator.CreateInstance(type) is ILayoutTheme theme && Enum.IsDefined(theme.Id) && !themes.Any(t => t.Id == theme.Id))
				{
					themes.Add(theme);
				}
			}
			catch (Exception)
			{
				// a broken theme is not shown; its id falls back to the next theme
			}
		}

		return [.. themes.OrderBy(t => t.Id)];
	}
}
