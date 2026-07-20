using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinDayFlow.App.Controls;

public sealed partial class EmptyStateControl : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(Symbol),
        typeof(EmptyStateControl),
        new PropertyMetadata(Symbol.Document));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(EmptyStateControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(EmptyStateControl),
        new PropertyMetadata(string.Empty));

    public EmptyStateControl()
    {
        InitializeComponent();
    }

    public Symbol Icon
    {
        get => (Symbol)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
