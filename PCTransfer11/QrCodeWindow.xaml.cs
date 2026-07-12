using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using ZXing.Rendering;

namespace PCTransfer11;

/// <summary>
/// Toont de PIN als scanbare QR-code gegenereerd via ZXing.Net (open-source,
/// Apache 2.0, puur lokaal - geen externe server of internet nodig).
/// </summary>
public partial class QrCodeWindow : Window
{
    public QrCodeWindow(string pin, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        PinLabel.Text = $"PIN: {pin}";
        QrImage.Source = GenerateQrCode(pin);
    }

    private static BitmapSource GenerateQrCode(string content)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = 300,
                Height = 300,
                Margin = 2,
                ErrorCorrection = ErrorCorrectionLevel.M,
                CharacterSet = "UTF-8"
            }
        };

        var pixelData = writer.Write(content);

        // Zet de pixeldata om naar een WPF-BitmapSource
        var bitmap = BitmapSource.Create(
            pixelData.Width,
            pixelData.Height,
            96, 96,
            PixelFormats.Bgr32,
            null,
            pixelData.Pixels,
            pixelData.Width * 4);

        return bitmap;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
