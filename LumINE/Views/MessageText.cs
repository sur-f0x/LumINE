using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using LumINE.Utils;

namespace LumINE.Views
{
    public static class MessageText
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
            "Value", typeof(string), typeof(MessageText), new PropertyMetadata(null, OnValueChanged));
        public static string GetValue(DependencyObject target) { return (string)target.GetValue(ValueProperty); }
        public static void SetValue(DependencyObject target, string value) { target.SetValue(ValueProperty, value); }

        private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        {
            var block = target as RichTextBlock;
            if (block == null) return;
            block.Blocks.Clear();
            var paragraph = new Paragraph();
            foreach (var part in MessageLinks.Split(args.NewValue as string))
            {
                var run = new Run { Text = part.Text };
                if (part.Uri == null) paragraph.Inlines.Add(run);
                else
                {
                    var link = new Hyperlink { NavigateUri = part.Uri };
                    link.Inlines.Add(run);
                    paragraph.Inlines.Add(link);
                }
            }
            block.Blocks.Add(paragraph);
        }
    }
}
