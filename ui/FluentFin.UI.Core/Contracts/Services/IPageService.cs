using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.UI.Core.Contracts.Services;

public interface IPageService
{
	Type GetPageType(string key);
	Type GetViewModelType(Type type);
	Type? GetParent(Type type);

	/// <summary>Nixie 1.3.0: the shared page a page stands for (a layout theme's own page: the shared page of the same view model).</summary>
	Type SharedPageType(Type pageType) => pageType;
}

public interface IPageRegistration
{
	public void Configure<VM, V>() where VM : ObservableObject where V : Page;
}
