using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Jarvis.Contracts;
using QRCoder;

namespace Jarvis.Desktop;

public partial class MobilePairingWindow : Window
{
    private readonly CoreClient _client = new();

    public MobilePairingWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void CreatePairingButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy("正在生成配对码…");
        var response = await _client.SendAsync(new CoreRequest(CoreOperations.CreateMobilePairing));
        if (!response.Success || response.MobilePairingOffer is null)
        {
            StatusText.Text = response.Message ?? "无法生成配对码。";
            return;
        }

        var offer = response.MobilePairingOffer;
        QrImage.Source = Qr(offer.QrPayload);
        QrImage.Visibility = Visibility.Visible;
        PayloadBox.Text = offer.QrPayload;
        PayloadBox.Visibility = Visibility.Visible;
        OfferText.Text = $"电脑地址：{offer.Endpoint}\n证书指纹：{FormatFingerprint(offer.CertificateFingerprint)}\n" +
                         $"失效时间：{offer.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        StatusText.Text = response.Message;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "撤销后，当前手机令牌将立即失效；重新连接必须生成新二维码。是否继续？",
            "撤销手机配对", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;
        var response = await _client.SendAsync(new CoreRequest(CoreOperations.RevokeMobile));
        StatusText.Text = response.Message ?? "撤销操作失败。";
        QrImage.Visibility = Visibility.Collapsed;
        PayloadBox.Visibility = Visibility.Collapsed;
        OfferText.Text = "";
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        SetBusy("正在读取手机状态…");
        var response = await _client.SendAsync(new CoreRequest(CoreOperations.GetMobileStatus));
        if (!response.Success || response.Mobile is null)
        {
            StatusText.Text = response.Message ?? "手机状态不可用。";
            return;
        }

        var value = response.Mobile;
        StatusText.Text = value.State switch
        {
            MobileConnectionState.Unpaired => "尚未配对手机。",
            MobileConnectionState.Ready => $"{value.DeviceName} 已连接，阻断所需权限可用。",
            MobileConnectionState.Offline => $"{value.DeviceName} 暂时离线；手机继续执行本地缓存策略。",
            MobileConnectionState.Degraded => $"{value.DeviceName} 已连接但能力降级：{value.Detail ?? "请检查使用情况和悬浮窗权限。"}",
            MobileConnectionState.Revoked => "上一部手机已撤销，可以生成新的配对码。",
            _ => value.Detail ?? value.State.ToString()
        };
        if (value.LastSeenAt is not null)
            StatusText.Text += $"\n最近同步：{value.LastSeenAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private void SetBusy(string value) => StatusText.Text = value;

    private static BitmapImage Qr(string payload)
    {
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(12);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string FormatFingerprint(string value) => string.Join(":",
        Enumerable.Range(0, value.Length / 2).Select(index => value.Substring(index * 2, 2)));
}
