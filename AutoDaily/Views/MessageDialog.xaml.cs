using System.Windows;
using System.Windows.Media;

namespace AutoDaily.Views;

public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    /// <summary>顯示確認對話框；按下確認按鈕傳回 true。</summary>
    public static bool Show(Window owner, string title, string message, string okText = "確定",
        string? cancelText = "取消", bool danger = false)
    {
        var dialog = new MessageDialog { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = okText;
        if (cancelText == null)
            dialog.CancelButton.Visibility = Visibility.Collapsed;
        else
            dialog.CancelButton.Content = cancelText;

        if (danger)
        {
            dialog.OkButton.Background = new SolidColorBrush(Color.FromRgb(0xB4, 0x32, 0x3F));
            dialog.OkButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x48, 0x56));
            dialog.OkButton.Foreground = Brushes.White;
        }
        else
        {
            dialog.OkButton.Style = (Style)dialog.FindResource("PrimaryButton");
            dialog.OkButton.Height = 36;
            dialog.OkButton.FontSize = 13.5;
        }

        return dialog.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
