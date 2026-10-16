using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace wsld.Ui;

/// <summary>
/// 只读文本框的"滚到最新一行"。日志区(功能 005)和拉取进度区(功能 011)共用这一份:
/// WinUI 的 TextBox 没有公开的滚动 API,只能从自己的模板里把那个 ScrollViewer 找出来。
/// </summary>
public static class TextBoxScroller
{
    /// <summary>内容为空时什么都不做 —— 那时 ScrollableHeight 还是旧值,滚了反而会跳一下。</summary>
    public static void ScrollToBottom(TextBox box)
    {
        if (box.Text.Length == 0) return;
        if (FindScrollViewer(box) is not { } viewer) return;
        viewer.ChangeView(null, viewer.ScrollableHeight, null);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }

        return null;
    }
}