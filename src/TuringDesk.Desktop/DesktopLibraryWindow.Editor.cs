using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class DesktopLibraryWindow
{
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        SceneList.MouseDoubleClick -= SceneList_MouseDoubleClick;
        SceneList.MouseDoubleClick += SceneList_MouseDoubleClick;
        SceneList.ContextMenuOpening -= SceneList_ContextMenuOpening;
        SceneList.ContextMenuOpening += SceneList_ContextMenuOpening;
    }

    private void SceneList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SceneList.SelectedItem is SceneManifest scene) OpenSceneProperties(scene);
    }

    private void SceneList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SceneList.SelectedItem is not SceneManifest scene)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        var apply = new MenuItem { Header = "应用到桌面" };
        apply.Click += (_, _) => ApplySelectedScene(scene);
        menu.Items.Add(apply);

        var properties = new MenuItem { Header = "桌面属性" };
        properties.Click += (_, _) => OpenSceneProperties(scene);
        menu.Items.Add(properties);

        var edit = new MenuItem { Header = scene.IsBuiltIn ? "复制并在 Scene Editor 中编辑" : "在 Scene Editor 中编辑" };
        edit.Click += (_, _) => OpenSceneEditor(scene);
        menu.Items.Add(edit);

        if (!scene.IsBuiltIn)
        {
            var export = new MenuItem { Header = "导出 .tdscene" };
            export.Click += (_, _) => ExportSelectedScene(scene);
            menu.Items.Add(export);
        }

        SceneList.ContextMenu = menu;
    }

    private void OpenSceneProperties(SceneManifest scene)
    {
        var dialog = new ScenePropertiesWindow(scene) { Owner = this };
        dialog.ShowDialog();
    }

    private void OpenSceneEditor(SceneManifest scene)
    {
        var editor = new SceneEditorWindow(scene) { Owner = this };
        editor.ShowDialog();
        ReloadScenes();
        var latest = _allScenes.FirstOrDefault(item => item.Id == editor.Scene.Id);
        if (latest is not null) SceneList.SelectedItem = latest;
    }

    private void ApplySelectedScene(SceneManifest scene)
    {
        SceneList.SelectedItem = scene;
        ApplyScene_Click(this, new RoutedEventArgs());
    }

    private void ExportSelectedScene(SceneManifest scene)
    {
        SceneList.SelectedItem = scene;
        ExportScene_Click(this, new RoutedEventArgs());
    }
}
