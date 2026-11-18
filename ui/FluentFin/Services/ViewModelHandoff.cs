namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.0: a theme switch shows the current page in the new theme's view with the SAME view model (nothing is fetched
/// again). Pages get their view model from <see cref="App.GetService{T}"/>; while a view model is offered here, the first
/// request for exactly its type gets it instead of a new one. One request only, and only during the switch's navigation
/// (<see cref="NavigationService.ReloadForTheme"/>).
/// </summary>
public static class ViewModelHandoff
{
	private static object? _offered;

	/// <summary>Offers <paramref name="viewModel"/> to the next request for its type; disposing the result withdraws it.</summary>
	public static IDisposable Offer(object? viewModel)
	{
		_offered = viewModel;
		return new Withdraw();
	}

	/// <summary>The offered view model when it is exactly of <paramref name="requested"/>, once; else false.</summary>
	public static bool TryTake(Type requested, out object? viewModel)
	{
		if (_offered is { } offered && offered.GetType() == requested)
		{
			_offered = null;
			viewModel = offered;
			return true;
		}

		viewModel = null;
		return false;
	}

	/// <summary>Whether a view model is still on offer (none was asked for).</summary>
	public static bool IsOffered => _offered is not null;

	public static void Clear() => _offered = null;

	private sealed class Withdraw : IDisposable
	{
		public void Dispose() => Clear();
	}
}
