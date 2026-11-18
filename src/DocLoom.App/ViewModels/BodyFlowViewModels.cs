using System.Windows;
using System.Windows.Controls;

namespace DocLoom.App.ViewModels;

/// <summary>
/// 正文流里的一组图片（原文档中相邻的若干张）。
/// 它引用的是图片墙里的同一批 ImageItemViewModel，勾选状态天然同步。
/// </summary>
public sealed class ImageGroupViewModel
{
    public ImageGroupViewModel(IReadOnlyList<ImageItemViewModel> images, int paragraphsBefore, int totalParagraphs)
    {
        Images = images;
        PositionText = BuildPosition(paragraphsBefore, totalParagraphs);
    }

    public IReadOnlyList<ImageItemViewModel> Images { get; }

    /// <summary>「位于第 1 段与第 2 段之间」这类位置说明。</summary>
    public string PositionText { get; }

    public string CountText => $"{Images.Count} 张";

    private static string BuildPosition(int paragraphsBefore, int totalParagraphs)
    {
        if (totalParagraphs == 0)
        {
            return "正文中的图片";
        }

        if (paragraphsBefore == 0)
        {
            return "位于正文开头";
        }

        if (paragraphsBefore >= totalParagraphs)
        {
            return $"位于第 {paragraphsBefore} 段之后（正文末尾）";
        }

        return $"位于第 {paragraphsBefore} 段与第 {paragraphsBefore + 1} 段之间";
    }
}

/// <summary>按条目类型在两种模板之间切换：段落 / 图片组。</summary>
public sealed class BodyFlowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ParagraphTemplate { get; set; }

    public DataTemplate? ImageGroupTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        ImageGroupViewModel => ImageGroupTemplate,
        ParagraphItemViewModel => ParagraphTemplate,
        _ => ParagraphTemplate,
    };
}