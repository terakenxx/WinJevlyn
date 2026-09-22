using System.Collections.ObjectModel;
using System.Windows;
using JevInferenceApp.Models;
using JevInferenceApp.Services;
using Microsoft.Win32;

namespace JevInferenceApp;

public partial class MainWindow : Window
{
    private readonly JevInferenceEngine _engine = new();
    private readonly ObservableCollection<ChoiceResult> _results = new();

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        Closed += (_, _) => _engine.Dispose();
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "GGUFモデルファイルを選択",
            Filter = "GGUFモデル (*.gguf)|*.gguf|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        ModelPathTextBox.Text = dialog.FileName;
        await LoadModelAsync(dialog.FileName);
    }

    private async void LoadModelButton_Click(object sender, RoutedEventArgs e)
    {
        var path = ModelPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(this, "GGUFファイルのパスを指定してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await LoadModelAsync(path);
    }

    private async Task LoadModelAsync(string path)
    {
        SetBusy(true, "モデルを読み込み中...");
        RunButton.IsEnabled = false;

        try
        {
            await _engine.LoadModelAsync(path);
            StatusTextBlock.Text = $"モデル読み込み完了: {System.IO.Path.GetFileName(path)}";
            RunButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
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
        var context = ContextTextBox.Text;
        var choiceA = ChoiceATextBox.Text;
        var choiceB = ChoiceBTextBox.Text;
        var choiceC = ChoiceCTextBox.Text;

        if (string.IsNullOrWhiteSpace(choiceA) || string.IsNullOrWhiteSpace(choiceB) || string.IsNullOrWhiteSpace(choiceC))
        {
            MessageBox.Show(this, "選択肢 A・B・C をすべて入力してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "推論中...");

        try
        {
            var outcome = await _engine.InferAsync(context, choiceA, choiceB, choiceC);

            _results.Clear();
            foreach (var result in outcome.Results)
                _results.Add(result);

            LatencyLabel.Content = $"Latency: {outcome.ElapsedMilliseconds} ms";
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
        BrowseButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        StatusTextBlock.Text = status;
    }
}
