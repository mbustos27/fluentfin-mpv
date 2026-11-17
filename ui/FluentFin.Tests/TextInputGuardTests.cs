using FluentFin.Services;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Tests;

/// <summary>Seaglass: no shortcut fires while the focus is in something that takes text (WindowShortcuts, the player's keys, Home's N and C).</summary>
public sealed class TextInputGuardTests
{
	[Theory]
	[InlineData(typeof(TextBox))]
	[InlineData(typeof(PasswordBox))]
	[InlineData(typeof(RichEditBox))]
	[InlineData(typeof(AutoSuggestBox))]
	[InlineData(typeof(NumberBox))]
	public void TextControls_AreTyping(Type type) => Assert.True(TextInput.IsTextInputType(type));

	[Theory]
	[InlineData(typeof(Button))]
	[InlineData(typeof(Slider))]
	[InlineData(typeof(ToggleSwitch))]
	[InlineData(typeof(Grid))]
	[InlineData(typeof(ListViewItem))]
	[InlineData(typeof(ComboBox))]
	[InlineData(typeof(FluentFin.Controls.BaseItemDtoCard))]
	public void EverythingElse_IsNot(Type type) => Assert.False(TextInput.IsTextInputType(type));

	[Fact]
	public void NothingFocused_IsNotTyping() => Assert.False(TextInput.IsTextInput(null));
}
