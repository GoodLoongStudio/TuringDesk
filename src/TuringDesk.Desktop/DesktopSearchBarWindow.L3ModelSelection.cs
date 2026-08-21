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
        if (_modelsInitializing) return;
        _quickAnswer.SelectConversationModel(_selectedModel);
    }
}
