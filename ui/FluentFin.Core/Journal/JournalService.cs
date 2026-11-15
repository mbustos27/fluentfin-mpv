using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Core.Journal;

/// <summary>An item the user rates from a page (an episode, season or series).</summary>
public sealed record RatedItem(Guid Id, RatedItemKind Kind, Guid? SeriesId, string? Name);

/// <summary>
/// The journal for the pages: the current user's entries and ratings, the settings, the writes to Jellyfin the journal owes,
/// and <see cref="Changed"/> when anything was written. Everything is for the journal of whoever is signed in now
/// (<see cref="IJournalSession"/>). Store calls are small and synchronous; the pages call them off the UI thread.
/// </summary>
public sealed class JournalService
{
	/// <summary>A queued write is dropped after this many failed attempts (an item deleted from the server would fail forever).</summary>
	public const int MaxAttempts = 20;

	private readonly Subject<JournalProfile> _changed = new();
	private readonly SemaphoreSlim _drain = new(1, 1);

	public JournalService(JournalStore store, IJournalServer server, IJournalSession session, ILogger<JournalService>? logger = null, TimeProvider? clock = null)
	{
		Store = store;
		Server = server;
		Session = session;
		Logger = (ILogger?)logger ?? NullLogger.Instance;
		Clock = clock ?? TimeProvider.System;
	}

	public JournalStore Store { get; }

	public IJournalServer Server { get; }

	public IJournalSession Session { get; }

	public TimeProvider Clock { get; }

	internal ILogger Logger { get; }

	/// <summary>The journal whose entries or ratings were written (from any thread).</summary>
	public IObservable<JournalProfile> Changed => _changed.AsObservable();

	public JournalProfile? CurrentProfile => Session.Current;

	public void NotifyChanged(JournalProfile profile)
	{
		try
		{
			_changed.OnNext(profile);
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex, "Journal: a page failed to take a change");
		}
	}

	public JournalSettings GetSettings() => Store.GetSettings();

	public void SaveSettings(JournalSettings settings) => Store.SaveSettings(settings);

	/// <summary>The current journal's entries, newest first (none when nobody is signed in).</summary>
	public IReadOnlyList<DiaryEntry> GetEntries() => CurrentProfile is { } profile ? Store.GetEntries(profile) : [];

	/// <summary>The current journal's ratings by item.</summary>
	public IReadOnlyDictionary<Guid, ItemRating> GetRatings() =>
		CurrentProfile is { } profile ? Store.GetRatings(profile).ToDictionary(x => x.ItemId) : new Dictionary<Guid, ItemRating>();

	public bool UpdateEntry(long id, DiaryEntryEdit edit)
	{
		if (CurrentProfile is not { } profile || !Store.UpdateEntry(profile, id, edit))
		{
			return false;
		}

		NotifyChanged(profile);
		return true;
	}

	/// <summary>Deletes a diary entry. Jellyfin's played state stays: deleting a diary line is not "unwatch".</summary>
	public bool DeleteEntry(long id)
	{
		if (CurrentProfile is not { } profile || !Store.DeleteEntry(profile, id))
		{
			return false;
		}

		NotifyChanged(profile);
		return true;
	}

	/// <summary>The current user's rating of the item: 1-10, 0 when not rated.</summary>
	public int GetRating(Guid itemId) => CurrentProfile is { } profile ? Store.GetRating(profile, itemId)?.Value ?? 0 : 0;

	/// <summary>
	/// Sets (1-10) or clears (0) the current user's rating. Sent to Jellyfin as well only when that setting is on (it defaults
	/// off, decision 5); a clear is not sent (what the server does with one is not verified). False when nobody is signed in.
	/// </summary>
	public async Task<bool> SetRatingAsync(RatedItem item, int value, CancellationToken cancellationToken = default)
	{
		if (CurrentProfile is not { } profile)
		{
			return false;
		}

		value = StarRating.Normalize(value);
		Store.SetRating(profile, item.Id, item.Kind, value, item.SeriesId, item.Name);
		NotifyChanged(profile);
		if (value > 0 && Store.GetSettings().MirrorRatingToJellyfin)
		{
			await Server.SetRatingAsync(item.Id, value, cancellationToken);
		}

		return true;
	}

	/// <summary>
	/// Sends the writes this journal still owes Jellyfin (a mark-played after a failed Stopped report), only while FluentFin is
	/// signed in as that journal's server and user. One at a time; never throws (but for cancellation).
	/// </summary>
	public async Task DrainPendingAsync(JournalProfile profile, CancellationToken cancellationToken = default)
	{
		if (!await _drain.WaitAsync(0, cancellationToken))
		{
			return;
		}

		try
		{
			foreach (var action in Store.GetPendingActions(profile))
			{
				if (Session.Current != profile)
				{
					return;
				}

				if (action.Action != PendingJournalAction.MarkPlayed || action.Attempts >= MaxAttempts)
				{
					Logger.LogWarning("Journal: dropping the queued {Action} of {ItemId} after {Attempts} attempts", action.Action, action.ItemId, action.Attempts);
					Store.CompletePendingAction(action.Id);
					continue;
				}

				var data = await Server.GetUserDataAsync(action.ItemId, cancellationToken);
				if (data is null)
				{
					Store.FailPendingAction(action.Id, "user data not readable");
					continue;
				}

				// the server has this play already (the report may have arrived after all)
				var done = data.Played && data.LastPlayedDate is { } last && last >= action.At - JournalSync.SamePlayWindow;
				if (!done && !await Server.MarkPlayedAsync(action.ItemId, action.At, cancellationToken))
				{
					Store.FailPendingAction(action.Id, "mark played refused");
					continue;
				}

				Store.CompletePendingAction(action.Id);
				if (action.EntryId is { } entryId)
				{
					Store.MarkOnServer(profile, entryId);
					NotifyChanged(profile);
				}
			}
		}
		catch (JournalUnavailableException ex)
		{
			Logger.LogWarning("Journal: {Message}", ex.Message);
		}
		finally
		{
			_drain.Release();
		}
	}
}
