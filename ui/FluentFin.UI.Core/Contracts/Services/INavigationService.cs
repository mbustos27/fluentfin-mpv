using FluentFin.Core.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Contracts.Services;

public interface INavigationService : INavigationServiceCore
{
	event NavigatedEventHandler Navigated;
	Frame? Frame { get; set; }

	/// <summary>Seaglass: forward in the frame's history (never into the player page).</summary>
	bool CanGoForward { get; }
	bool GoForward();

	/// <summary>
	/// 1.7.5 (the review's Major): the shell this service navigates went (Sign out, Switch user): its frame is let go, and
	/// nothing navigates until the next shell sets <see cref="Frame"/> (a cast or a remote command that still arrived played
	/// in the detached frame, with no page or controls). Nothing for a service that keeps no shell.
	/// </summary>
	void Release() { }
}
