namespace FluentFin.Contracts.ViewModels;

/// <summary>
/// Nixie 1.7.4 (the owner: "going back to the collection from a movie in the collection makes u wait for the collection to load
/// again"; rule 1): a page's view model that Back and Forward show again as it was, at once, with no loading state. The
/// navigation service keeps it while its page is in Back's or Forward's reach (a few pages: the app's PageKeep) and hands it to
/// the page Back or Forward makes, in place of a new one that would load again. It is left as any page is
/// (<see cref="INavigationAware.OnNavigatedFrom"/>: it lets go of what it listens to), and <see cref="Returned"/> takes the place
/// of <see cref="INavigationAware.OnNavigatedTo"/> when it comes back.
/// </summary>
public interface IKeptOnBack
{
	/// <summary>
	/// Whether it can be shown again as it is now: what its page shows has loaded. A page left while it loads, or one that could
	/// not load, loads again on Back as before.
	/// </summary>
	bool CanKeep { get; }

	/// <summary>
	/// Back or Forward shows it again, as it was: it listens again to what <see cref="INavigationAware.OnNavigatedFrom"/> let go
	/// of, and puts back anything that was only for the visit that left (a pending request, a selection). When
	/// <paramref name="stale"/> (something may have changed since: a title marked watched or a favourite, a play, the server's
	/// change messages, or it was kept a while), it asks the server again behind what it shows, with no loading state, and
	/// changes only what changed (the cards stay, so the focus and the scroll do); a refresh that fails leaves the page as it is.
	/// </summary>
	/// <param name="parameter">The page's parameter, as it was opened with.</param>
	Task Returned(object parameter, bool stale);
}
