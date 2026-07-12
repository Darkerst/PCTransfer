using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCTransfer11.Services;

namespace PCTransfer11;

public partial class QrCodeWindow : Window
{
    public QrCodeWindow(string pin, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        PinLabel.Text = pin;
        DrawQrCode(pin);
    }

    private void DrawQrCode(string pin)
    {
        try
        {
            var matrix = SimpleQrGenerator.Encode(pin);
            double cellSize = QrCanvas.Width / matrix.GetLength(0);
            for (int row = 0; row < matrix.GetLength(0); row++)
                for (int col = 0; col < matrix.GetLength(1); col++)
                    if (matrix[row, col])
                    {
                        var rect = new Rectangle { Width = cellSize + 0.5, Height = cellSize + 0.5, Fill = Brushes.Black };
                        Canvas.SetLeft(rect, col * cellSize);
                        Canvas.SetTop(rect, row * cellSize);
                        QrCanvas.Children.Add(rect);
                    }
        }
        catch
        {
            var tb = new TextBlock { Text = "QR-code kon niet worden\ngegenereerd.\nGebruik de PIN hieronder.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brushes.Gray };
            Canvas.SetLeft(tb, 20); Canvas.SetTop(tb, 80);
            QrCanvas.Children.Add(tb);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
