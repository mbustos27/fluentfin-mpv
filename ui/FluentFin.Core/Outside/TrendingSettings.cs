using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 trending: one of the two trending sources' switch in Settings &gt; Look up lists online (Trakt, TMDb trending): its name
/// (the toggle's Narrator name too), a one-line description, its "What is sent" card, and a status line in Nixie's words.
/// </summary>
public sealed partial class TrendingSourceSwitch : ObservableObject
{
	private readonly TrendingSources _sources;

	public TrendingSourceSwitch(TrendingSources sources, OutsideSourceId id, string name, string description, string whatIsSent, OutsideLink link)
	{
		_sources = sources;
		Id = id;
		Name = name;
		Description = description;
		WhatIsSent = whatIsSent;
		Link = link;
	}

	public OutsideSourceId Id { get; }

	public string Name { get; }

	public string Description { get; }

	/// <summary>The "What is sent" card's text: what leaves the PC and what never does.</summary>
	public string WhatIsSent { get; }

	public OutsideLink Link { get; }

	/// <summary>The "What is sent" card's Narrator name: "What is sent to Trakt".</summary>
	public string WhatIsSentName => $"What is sent to {Link.Text}";

	/// <summary>The link's Narrator name: "Trakt, opens in your browser".</summary>
	public string LinkName => $"{Link.Text}, opens in your browser";

	/// <summary>The status card's Narrator name: "Trending from Trakt status".</summary>
	public string StatusName => $"{Name} status";

	/// <summary>
	/// Whether the switch can be turned on: Trakt only in a build with a Client ID, TMDb with a token (the user's own, or
	/// Nixie's in a release); else "Not available in this build" (TMDb: until the user enters a token).
	/// </summary>
	public bool IsAvailable => Id == OutsideSourceId.Trakt ? _sources.TraktAvailable : _sources.HasTmdbToken;

	public bool IsOn
	{
		get => _sources.Hub.IsOn(Id) && IsAvailable;
		set
		{
			if (!IsAvailable || value == _sources.Hub.IsOn(Id))
			{
				return;
			}

			_sources.Hub.SetOn(Id, value);
			OnPropertyChanged();
			OnPropertyChanged(nameof(Status));
		}
	}

	/// <summary>What the source does now, in Nixie's words (<see cref="TrendingStatus.Describe"/>).</summary>
	public string Status => TrendingStatus.Describe(_sources, Id);

	/// <summary>The sources answered, a switch or the token changed: the status line follows.</summary>
	public void Refresh()
	{
		OnPropertyChanged(nameof(IsAvailable));
		OnPropertyChanged(nameof(IsOn));
		OnPropertyChanged(nameof(Status));
	}
}

/// <summary>
/// 1.7 trending: Settings &gt; Look up lists online, stream T's part (in the panel stream K left for it): Trakt's and TMDb
/// trending's switches with their "What is sent" cards, and the TMDb token field. The token is never shown back: the field
/// takes a new one (Save), and Remove forgets the one kept.
/// </summary>
public sealed partial class TrendingSourcesSettingsViewModel : ObservableObject
{
	private readonly Action<Action> _post;
	private readonly TmdbToken _token;
	private bool _attached;

	/// <summary><paramref name="post"/> runs a status update on the page's thread (the sources answer on others).</summary>
	public TrendingSourcesSettingsViewModel(TrendingSources sources, TmdbToken token, Action<Action>? post = null)
	{
		Sources = sources;
		_token = token;
		_post = post ?? (a => a());
		Trakt = new TrendingSourceSwitch(sources, OutsideSourceId.Trakt,
			"Trending from Trakt",
			sources.TraktAvailable
				? "Trending, popular and most anticipated films and shows, and the US box office. No Trakt account, no sign-in. Off by default."
				: TraktClientId.NotAvailable + ": this copy of Nixie was built without a Trakt app registration.",
			"Sent: requests for Trakt's public lists (the same for everyone), Nixie's name and version, and Nixie's app ID, which names the app, not you. Not sent: the server you use, your account, libraries or anything you watch. Nixie never signs you in to Trakt; it matches the lists on this PC. " + OutsideKeep.Describe(OutsideSourceId.Trakt),
			new OutsideLink("Trakt", new Uri("https://trakt.tv/")));
		TmdbTrending = new TrendingSourceSwitch(sources, OutsideSourceId.TmdbTrending,
			"Trending from TMDb",
			sources.HasAppTmdbToken
				? "Films and shows trending this week. Uses Nixie's own TMDb key. You can enter your own instead. Off by default."
				: "Films and shows trending this week, with your own TMDb token: this copy of Nixie has no TMDb key of its own. Off by default.",
			"Sent: requests for TMDb's public trending lists (the same for everyone), Nixie's name and version, and Nixie's TMDb key (or yours, if you enter one), only in the request's Authorization header. Not sent: the server you use, your account, libraries or anything you watch. No pictures are fetched from TMDb; Nixie matches the lists on this PC. " + OutsideKeep.Describe(OutsideSourceId.TmdbTrending),
			new OutsideLink("TMDb", new Uri("https://www.themoviedb.org/settings/api")));
		Switches = [Trakt, TmdbTrending];
		Attach();
	}

	public TrendingSources Sources { get; }

	public TrendingSourceSwitch Trakt { get; }

	public TrendingSourceSwitch TmdbTrending { get; }

	public IReadOnlyList<TrendingSourceSwitch> Switches { get; }

	/// <summary>What the token field holds (a PasswordBox: never filled from the kept token).</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SaveTokenCommand))]
	public partial string TokenEntry { get; set; } = "";

	/// <summary>Whether a token is kept (the field's description says so, and Remove shows).</summary>
	public bool HasToken => _token.HasToken;

	/// <summary>Under the field: whether one is kept, what Save did, or what's wrong with what was pasted.</summary>
	[ObservableProperty]
	public partial string TokenMessage { get; private set; } = "";

	/// <summary>The token card's description: whose token TMDb trending uses, and what entering one does.</summary>
	public string TokenDescription => HasToken
		? Sources.HasAppTmdbToken && !Sources.AppTmdbTokenRefused
			? "Your own token is kept on this PC, encrypted for your Windows account, and used instead of Nixie's key. Paste a new one to replace it, or Remove it to use Nixie's key."
			: "Your own token is kept on this PC, encrypted for your Windows account. Paste a new one to replace it."
		: Sources.HasAppTmdbToken && !Sources.AppTmdbTokenRefused
			? "Nixie uses its own TMDb key. To use yours instead, paste your API Read Access Token from TMDb's API settings (the long one); it's kept on this PC, encrypted for your Windows account."
			: "Your API Read Access Token from TMDb's API settings (the long one). Kept on this PC, encrypted for your Windows account.";

	private bool CanSaveToken() => !string.IsNullOrWhiteSpace(TokenEntry);

	/// <summary>Keeps the pasted token (the field empties), or says why not and keeps nothing.</summary>
	[RelayCommand(CanExecute = nameof(CanSaveToken))]
	public void SaveToken()
	{
		var problem = _token.Save(TokenEntry);
		if (problem is not null)
		{
			TokenMessage = problem;
			return;
		}

		TokenEntry = "";
		TokenMessage = "Saved. Nixie uses it for TMDb trending.";
		RefreshToken();
	}

	/// <summary>Forgets the kept token.</summary>
	[RelayCommand]
	public void RemoveToken()
	{
		_token.Clear();
		TokenEntry = "";
		TokenMessage = Sources.HasAppTmdbToken && !Sources.AppTmdbTokenRefused ? "Removed. Nixie uses its own TMDb key again." : "Removed.";
		RefreshToken();
	}

	private void RefreshToken()
	{
		OnPropertyChanged(nameof(HasToken));
		OnPropertyChanged(nameof(TokenDescription));
		Refresh();
	}

	/// <summary>Follows the sources (the page's Loaded; the constructor attaches too).</summary>
	public void Attach()
	{
		if (!_attached)
		{
			_attached = true;
			Sources.Hub.Answered += OnHubChanged;
			Sources.Hub.SwitchChanged += OnHubChanged;
			Sources.Changed += OnSourcesChanged;
			Refresh();
		}
	}

	/// <summary>Stops following them (the page's Unloaded): the sources live as long as the app, the page doesn't.</summary>
	public void Detach()
	{
		if (_attached)
		{
			_attached = false;
			Sources.Hub.Answered -= OnHubChanged;
			Sources.Hub.SwitchChanged -= OnHubChanged;
			Sources.Changed -= OnSourcesChanged;
		}
	}

	private void OnHubChanged(OutsideSourceId id)
	{
		if (TrendingSources.Ids.Contains(id))
		{
			_post(Refresh);
		}
	}

	private void OnSourcesChanged() => _post(() =>
	{
		OnPropertyChanged(nameof(TokenDescription));
		Refresh();
	});

	private void Refresh()
	{
		foreach (var item in Switches)
		{
			item.Refresh();
		}
	}
}

/// <summary>1.7 trending: Trakt's line in Settings &gt; About's credits for the outside lists, in the words its terms ask for.</summary>
public static class TrendingCredits
{
	/// <summary>Trakt's API Use Policy's disclaimer, with the app's name (the README carries it too).</summary>
	public const string TraktDisclaimer = AppIdentity.Name + " uses Trakt but is not endorsed or certified by Trakt.";
}
