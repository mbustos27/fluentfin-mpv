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
}
