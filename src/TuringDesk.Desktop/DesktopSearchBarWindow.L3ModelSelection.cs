using System.Windows.Controls;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    private bool _l3ModelSelectionInitialized;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_l3ModelSelectionInitialized) return;

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
