using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using JevMultimodalApp.Services;
using Microsoft.Win32;

namespace JevMultimodalApp;

public partial class MainWindow : Window
{
    private readonly JevMultimodalEngine _engine = new();
    private string? _imagePath;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _engine.Dispose();
    }

    private void BrowseModelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "GGUFモデルファイルを選択",
            Filter = "GGUFモデル (*.gguf)|*.gguf|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        ModelPathTextBox.Text = dialog.FileName;
    }

    private void BrowseMmprojButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "mmprojファイルを選択",
            Filter = "GGUFモデル (*.gguf)|*.gguf|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        MmprojPathTextBox.Text = dialog.FileName;
    }

    private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "画像ファイルを選択",
            Filter = "画像ファイル (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dialog.FileName, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            _imagePath = dialog.FileName;
            ImagePreview.Source = bitmap;
        }
        catch (Exception ex)
        {
            _imagePath = null;
            ImagePreview.Source = null;
            MessageBox.Show(this, $"画像の読み込みに失敗しました。\n\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadModelButton_Click(object sender, RoutedEventArgs e)
    {
        var modelPath = ModelPathTextBox.Text.Trim();
        var mmprojPath = MmprojPathTextBox.Text.Trim();

        if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(mmprojPath))
        {
            MessageBox.Show(this, "本体GGUFとmmprojの両方のパスを指定してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await LoadModelAsync(modelPath, mmprojPath);
    }

    private async Task LoadModelAsync(string modelPath, string mmprojPath)
    {
        SetBusy(true, "モデルを読み込み中...");
        RunButton.IsEnabled = false;
        BackendLabel.Content = "読み込み中...";

        try
        {
            var result = await _engine.LoadModelAsync(modelPath, mmprojPath);
            BackendLabel.Content = result.UsingGpu ? "GPU (CUDA)" : "CPU";
            StatusTextBlock.Text = $"モデル読み込み完了 ({result.ElapsedMilliseconds} ms): {Path.GetFileName(modelPath)}";
            RunButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            BackendLabel.Content = "読み込み失敗";
            StatusTextBlock.Text = "モデル読み込み失敗";
            MessageBox.Show(this, $"モデルの読み込みに失敗しました。\n\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var question = QuestionTextBox.Text;

        if (string.IsNullOrEmpty(_imagePath))
        {
            MessageBox.Show(this, "画像を選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(question))
        {
            MessageBox.Show(this, "質問を入力してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "推論中...");
        ResponseTextBox.Clear();

        try
        {
            var responseBuilder = new StringBuilder();
            await foreach (var chunk in _engine.InferStreamingAsync(_imagePath, question))
            {
                responseBuilder.Append(chunk);
                ResponseTextBox.Text = responseBuilder.ToString();
                ResponseTextBox.ScrollToEnd();
            }

            var tokPerSec = _engine.LastGenerationMs > 0
                ? _engine.LastTokenCount / (_engine.LastGenerationMs / 1000.0)
                : 0;
            LatencyLabel.Content = $"Latency: 画像処理 {_engine.LastPromptProcessingMs} ms / 生成 {_engine.LastGenerationMs} ms ({tokPerSec:F1} tok/s)";
            StatusTextBlock.Text = "推論完了";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "推論失敗";
            MessageBox.Show(this, $"推論に失敗しました。\n\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private void SetBusy(bool busy, string status)
    {
        RunButton.IsEnabled = !busy && _engine.IsModelLoaded;
        LoadModelButton.IsEnabled = !busy;
        BrowseModelButton.IsEnabled = !busy;
        BrowseMmprojButton.IsEnabled = !busy;
        BrowseImageButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        StatusTextBlock.Text = status;
    }
}
