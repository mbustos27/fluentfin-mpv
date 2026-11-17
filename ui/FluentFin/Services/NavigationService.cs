using System.Diagnostics.CodeAnalysis;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Helpers;
using FluentFin.UI.Core.Contracts.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Services;

// For more information on navigation between pages see
// https://github.com/microsoft/TemplateStudio/blob/main/docs/WinUI/navigation.md
public class NavigationService : INavigationService
{
	private readonly IPageService _pageService;
	private object? _lastParameterUsed;
	private Frame? _frame;

	public event NavigatedEventHandler? Navigated;

	public Frame? Frame
	{
		get
		{
			if (_frame == null)
			{
				_frame = App.MainWindow.Content as Frame;
				RegisterFrameEvents();
			}

			return _frame;
		}

		set
		{
			UnregisterFrameEvents();
			_frame = value;
			RegisterFrameEvents();
		}
	}

	[MemberNotNullWhen(true, nameof(Frame), nameof(_frame))]
	public bool CanGoBack => Frame != null && Frame.CanGoBack;

	public NavigationService(IPageService pageService)
	{
		_pageService = pageService;
	}

	private void RegisterFrameEvents()
	{
		if (_frame != null)
		{
			_frame.Navigated += OnNavigated;
		}
	}

	private void UnregisterFrameEvents()
	{
		if (_frame != null)
		{
			_frame.Navigated -= OnNavigated;
		}
	}

	public bool GoBack()
	{
		if (CanGoBack)
		{
			var vmBeforeNavigation = _frame.GetPageViewModel();
			_frame.GoBack();
			if (vmBeforeNavigation is INavigationAware navigationAware)
			{
				navigationAware.OnNavigatedFrom();
			}

			return true;
		}

		return false;
	}

	[MemberNotNullWhen(true, nameof(Frame), nameof(_frame))]
	public bool CanGoForward => Frame != null && Frame.CanGoForward;

	/// <summary>Seaglass: forward, the Frame's own forward history (<see cref="PruneForwardStack{T}"/> keeps the player out of it).</summary>
	public bool GoForward()
	{
		if (CanGoForward)
		{
			var vmBeforeNavigation = _frame.GetPageViewModel();
			_frame.GoForward();
			if (vmBeforeNavigation is INavigationAware navigationAware)
			{
				navigationAware.OnNavigatedFrom();
			}

			return true;
		}

		return false;
	}

	public bool NavigateTo(string pageKey, object? parameter = null, bool clearNavigation = false)
	{
		try
		{
			var pageType = _pageService.GetPageType(pageKey);

			if (_frame != null && (_frame.Content?.GetType() != pageType || (parameter != null && !parameter.Equals(_lastParameterUsed))))
			{
				_frame.Tag = clearNavigation;
				var vmBeforeNavigation = _frame.GetPageViewModel();
				var navigated = _frame.Navigate(pageType, parameter);
				if (navigated)
				{
					_lastParameterUsed = parameter;
					if (vmBeforeNavigation is INavigationAware navigationAware)
					{
						navigationAware.OnNavigatedFrom();
					}
				}

				return navigated;
			}

			return false;
		}
		catch
		{
			return false;
		}

	}

	private async void OnNavigated(object sender, NavigationEventArgs e)
	{
		if (sender is Frame frame)
		{
			// Seaglass 1.1.4: the motion follows the direction (a rail section, in, Back out)
			PageTransition.Play(e.Content, e.NavigationMode);
			var clearNavigation = (bool)frame.Tag;
			if (clearNavigation)
			{
				frame.BackStack.Clear();
				frame.ForwardStack.Clear();
			}

			// Seaglass: Forward never leads into the player (it would start playing again)
			PruneForwardStack(frame.ForwardStack, x => x.SourcePageType);

			// fluentfin-mpv: a player page replacing a player page (a remote Play while playing) takes over its history entry
			DropReplacedPlayerPage(frame.BackStack, x => x.SourcePageType, e.SourcePageType, e.NavigationMode);

			if (frame.GetPageViewModel() is INavigationAware navigationAware)
			{
				await navigationAware.OnNavigatedTo(e.Parameter);
			}

			Navigated?.Invoke(sender, e);
		}
	}

	/// <summary>
	/// Seaglass: the player page is not a forward target (Back out of the player, then Forward, would start the item again).
	/// Removes every player entry from the forward history; true when one was removed.
	/// </summary>
	internal static bool PruneForwardStack<T>(IList<T> forwardStack, Func<T, Type> pageType)
	{
		var removed = false;
		for (var i = forwardStack.Count - 1; i >= 0; i--)
		{
			if (pageType(forwardStack[i]) == typeof(Views.VideoPlayerPage))
			{
				forwardStack.RemoveAt(i);
				removed = true;
			}
		}

		return removed;
	}

	/// <summary>
	/// fluentfin-mpv: when a player page was replaced by a new one, the old one leaves the back stack, so Back from the new
	/// page goes to where the user was before the first player page instead of playing the old item again.
	/// </summary>
	internal static bool DropReplacedPlayerPage<T>(IList<T> backStack, Func<T, Type> pageType, Type incoming, NavigationMode mode)
	{
		if (mode != NavigationMode.New || incoming != typeof(Views.VideoPlayerPage) || backStack.Count == 0 || pageType(backStack[^1]) != incoming)
		{
			return false;
		}

		backStack.RemoveAt(backStack.Count - 1);
		return true;
	}
}
