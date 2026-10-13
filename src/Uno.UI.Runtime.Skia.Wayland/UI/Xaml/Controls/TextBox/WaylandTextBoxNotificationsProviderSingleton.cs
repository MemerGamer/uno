// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using Microsoft.UI.Xaml.Controls;
using Uno.UI.Xaml.Controls.Extensions;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Forwards caret, selection and text changes to <see cref="WaylandImeTextBoxExtension"/>
/// so the input method sees current surrounding text and places its popup at the caret.
/// </summary>
internal sealed class WaylandTextBoxNotificationsProviderSingleton(WaylandImeTextBoxExtension ime) : ITextBoxNotificationsProviderSingleton
{
	public void OnFocused(TextBox textBox) => ime.UpdateState(textBox);

	public void OnUnfocused(TextBox textBox)
	{
	}

	public void OnEnteredVisualTree(TextBox textBox)
	{
	}

	public void OnLeaveVisualTree(TextBox textBox)
	{
	}

	public void FinishAutofillContext(bool shouldSave)
	{
	}

	public void NotifyValueChanged(TextBox textBox) => ime.UpdateState(textBox);

	public void NotifySelectionChanged(TextBox textBox) => ime.UpdateState(textBox);
}
