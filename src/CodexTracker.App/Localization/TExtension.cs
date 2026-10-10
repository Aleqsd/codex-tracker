using System.Windows.Markup;

namespace CodexTracker.App;

/// <summary>French XAML text shown in the chosen language: <c>Text="{local:T 'Comptes'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;
    [ConstructorArgument("text")] public string Text { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
