using System.Windows;
using System.Windows.Controls;
using GKRStudio.Core;

namespace GKRStudio.UI;

/// <summary>Translates the static texts of a XAML window in place (TextBlock, buttons, check boxes, headers, title).</summary>
public static class Localizer
{
    public static void Translate(DependencyObject root)
    {
        if (L.Lang == "tr") return;
        if (root is Window w) w.Title = L.T(w.Title);
        Walk(root);
    }

    private static void Walk(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock tb when tb.Inlines.Count <= 1 && !string.IsNullOrEmpty(tb.Text):
                tb.Text = L.T(tb.Text);
                break;
            case HeaderedContentControl h when h.Header is string hs:
                h.Header = L.T(hs);
                break;
        }
        if (node is ContentControl { Content: string s } cc) cc.Content = L.T(s);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            Walk(child);
    }
}
