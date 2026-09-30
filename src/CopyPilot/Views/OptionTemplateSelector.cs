using System.Windows;
using System.Windows.Controls;
using CopyPilot.Core.Models;
using CopyPilot.ViewModels;

namespace CopyPilot.Views;

/// <summary>Picks the editor for an option based on its kind.</summary>
public sealed class OptionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FlagTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ChoiceTemplate { get; set; }
    public DataTemplate? PathTemplate { get; set; }
    public DataTemplate? SecretTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not OptionViewModel o) return base.SelectTemplate(item, container);
        if (o.IsSensitive) return SecretTemplate;
        return o.Kind switch
        {
            OptionKind.Flag => FlagTemplate,
            OptionKind.Choice => ChoiceTemplate,
            OptionKind.FilePath or OptionKind.FolderPath => PathTemplate,
            _ => TextTemplate,
        };
    }
}
