using System.Windows.Controls;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    private bool _l3ModelSelectionInitialized;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_l3ModelSelectionInitialized) return;

        // The main search-bar partial already owns OnContentRendered. Initialize
        // model/session coupling on first activation instead of competing for the
        // same WPF lifecycle override across partial files.
        _l3ModelSelectionInitialized = true;
        ModelSelector.SelectionChanged += L3ModelSelector_SelectionChanged;
        _quickAnswer.SelectConversationModel(_selectedModel);
    }

    private void L3ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Programmatic RefreshModelChoices updates _selectedModel before assigning
        // SelectedItem. Session selection must follow that refresh too; otherwise a
        // user can click "new conversation" before the first message and clear the
        // previously-active model session instead of the model shown in the UI.
        _quickAnswer.SelectConversationModel(_selectedModel);
    }
}
