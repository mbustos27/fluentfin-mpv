using System.Windows.Input;
using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;


namespace FluentFin.Views.JellyfinSettings;

public partial class ServerConfigurationPage : UserControl
{
	[GeneratedDependencyProperty]
	public partial List<JellyfinConfigItemViewModel>? Items { get; set; }

	[GeneratedDependencyProperty]
	public partial ICommand? SaveCommand { get; set; }

	[GeneratedDependencyProperty]
	public partial ICommand? ResetCommand { get; set; }

	[GeneratedDependencyProperty]
	public partial string? SectionName { get; set; }

	/// <summary>1.5 (the button review's S22): what the last Save did ("Saved", or why not), beside Save.</summary>
	[GeneratedDependencyProperty]
	public partial string? SaveStatus { get; set; }

	public ServerConfigurationPage()
	{
		InitializeComponent();
	}
}