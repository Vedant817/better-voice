using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterVoice.App.Audio;
using BetterVoice.App.Services;
using BetterVoice.Core;

namespace BetterVoice.App.UI;

public partial class SetupWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private bool _isInitializing = true;
    private int _saveFeedbackVersion;

    public SetupWindow(SettingsManager settingsManager)
    {
        InitializeComponent();
        _settingsManager = settingsManager;
        LoadSettings();
        _isInitializing = false;
        UpdateOverview();
        UpdateModelHint();
        UpdateGrammarAvailability();
    }

    private void LoadSettings()
    {
        // Populate Microphones
        var devices = AudioRecorder.GetInputDevices();
        MicComboBox.Items.Clear();
        MicComboBox.Items.Add(new ComboBoxItem { Content = "Default System Microphone", Tag = (string?)null });
        int selectedIndex = 0;

        for (int i = 0; i < devices.Count; i++)
        {
            var item = new ComboBoxItem { Content = devices[i].Name, Tag = devices[i].Id };
            MicComboBox.Items.Add(item);
            if (devices[i].Id == _settingsManager.Current.SelectedMicrophoneId)
            {
                selectedIndex = i + 1;
            }
        }
        MicComboBox.SelectedIndex = selectedIndex;

        // Populate Trigger Mode
        TriggerModeComboBox.SelectedIndex = (int)_settingsManager.Current.QuickTriggerMode;

        // Hold Delay
        HoldDelaySlider.Value = _settingsManager.Current.QuickHoldDelayMilliseconds;
        HoldDelayLabel.Text = $"{_settingsManager.Current.QuickHoldDelayMilliseconds} ms";

        // Circle angle sensitivity
        AngleSlider.Value = _settingsManager.Current.CircleMinimumAngleDegrees;
        AngleLabel.Text = $"{(int)_settingsManager.Current.CircleMinimumAngleDegrees}°";

        // Languages
        LanguageComboBox.Items.Clear();
        int langIndex = 0;
        for (int i = 0; i < TranscriptionLanguage.All.Count; i++)
        {
            var lang = TranscriptionLanguage.All[i];
            LanguageComboBox.Items.Add(new ComboBoxItem { Content = $"{lang.Name} ({lang.Code})", Tag = lang.Code });
            if (lang.Code == _settingsManager.Current.TranscriptionLanguageCode)
            {
                langIndex = i;
            }
        }
        LanguageComboBox.SelectedIndex = langIndex;

        ModelSizeComboBox.Items.Clear();
        foreach (TranscriptionModelSize size in Enum.GetValues<TranscriptionModelSize>())
        {
            ModelSizeComboBox.Items.Add(new ComboBoxItem { Content = size.DisplayName(), Tag = size });
        }
        ModelSizeComboBox.SelectedIndex = (int)_settingsManager.Current.TranscriptionModelSize;

        // Toggles
        DeveloperCleanupCheckBox.IsChecked = _settingsManager.Current.DeveloperCleanupEnabled;
        GrammarCorrectionCheckBox.IsChecked = _settingsManager.Current.GrammarCorrectionEnabled;
        bool croppedMode = _settingsManager.Current.ScreenContextCaptureMode == ScreenContextCaptureMode.CroppedSelection;
        CroppedSelectionModeRadio.IsChecked = croppedMode;
        FullDisplayModeRadio.IsChecked = !croppedMode;
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (MicComboBox.SelectedItem is ComboBoxItem item)
        {
            _settingsManager.Current.SelectedMicrophoneId = item.Tag as string;
            SaveSettings();
        }
    }

    private void OnTriggerModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settingsManager.Current.QuickTriggerMode = (RecordingTriggerMode)TriggerModeComboBox.SelectedIndex;
        SaveSettings();
    }

    private void OnHoldDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)e.NewValue;
        HoldDelayLabel.Text = $"{val} ms";
        _settingsManager.Current.QuickHoldDelayMilliseconds = val;
        SaveSettings();
    }

    private void OnAngleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)e.NewValue;
        AngleLabel.Text = $"{val}°";
        _settingsManager.Current.CircleMinimumAngleDegrees = val;
        SaveSettings();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
        {
            _settingsManager.Current.TranscriptionLanguageCode = code;
            UpdateGrammarAvailability();
            SaveSettings();
        }
    }

    private void OnDeveloperCleanupToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settingsManager.Current.DeveloperCleanupEnabled = DeveloperCleanupCheckBox.IsChecked ?? true;
        SaveSettings();
    }

    private void OnModelSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (ModelSizeComboBox.SelectedItem is ComboBoxItem { Tag: TranscriptionModelSize size })
        {
            _settingsManager.Current.TranscriptionModelSize = size;
            UpdateModelHint();
            SaveSettings();
        }
    }

    private void OnGrammarToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settingsManager.Current.GrammarCorrectionEnabled = GrammarCorrectionCheckBox.IsChecked ?? false;
        SaveSettings();
    }

    private void OnCaptureModeChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton { IsChecked: true } selected) return;

        _settingsManager.Current.ScreenContextCaptureMode = ReferenceEquals(selected, CroppedSelectionModeRadio)
            ? ScreenContextCaptureMode.CroppedSelection
            : ScreenContextCaptureMode.FullDisplayWithHighlight;
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settingsManager.Save();
        UpdateOverview();
        ShowSavedFeedback();
    }

    private void UpdateOverview()
    {
        OverviewMicLabel.Text = MicComboBox.SelectedItem is ComboBoxItem mic
            ? mic.Content?.ToString() ?? "System microphone"
            : "System microphone";

        var language = TranscriptionLanguage.FromStoredCode(_settingsManager.Current.TranscriptionLanguageCode);
        OverviewLanguageLabel.Text = language.Name;
        OverviewModelLabel.Text = _settingsManager.Current.TranscriptionModelSize.DisplayName();
        OverviewShortcutLabel.Text = _settingsManager.Current.QuickTriggerMode switch
        {
            RecordingTriggerMode.Hold => "Hold Alt",
            RecordingTriggerMode.Toggle => "Press Alt",
            RecordingTriggerMode.DoubleTap => "Double-tap Alt",
            _ => "Hold Alt"
        };
    }

    private void UpdateModelHint()
    {
        ModelHintText.Text = _settingsManager.Current.TranscriptionModelSize switch
        {
            TranscriptionModelSize.Fast => "Fast starts quickly and uses the least memory. Best for short, simple notes.",
            TranscriptionModelSize.Accurate => "Accurate handles harder vocabulary, but needs more memory and takes longer to process.",
            _ => "Balanced gives the best everyday mix of developer-term accuracy and low latency."
        };
    }

    private void UpdateGrammarAvailability()
    {
        bool supportsGrammar = string.Equals(
            _settingsManager.Current.TranscriptionLanguageCode,
            TranscriptionLanguage.English.Code,
            StringComparison.OrdinalIgnoreCase);
        GrammarCorrectionCheckBox.IsEnabled = supportsGrammar;
        GrammarHintText.Text = supportsGrammar
            ? "Polish English punctuation and sentence structure with local ONNX inference."
            : "Paused because grammar correction currently supports English only.";
    }

    private void ShowSavedFeedback()
    {
        int version = ++_saveFeedbackVersion;
        SaveStatusText.Text = "Saved just now";
        SaveStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(207, 247, 220));
        SaveStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128));
        _ = ResetSavedFeedbackAsync(version);
    }

    private async Task ResetSavedFeedbackAsync(int version)
    {
        await Task.Delay(1_800);
        if (version != _saveFeedbackVersion || !IsLoaded) return;
        SaveStatusText.Text = "Auto-save on";
    }

    private void OnNavigationClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Content: string destination }) NavigateTo(destination);
    }

    private void NavigateTo(string destination)
    {
        FrameworkElement page;
        switch (destination)
        {
            case "Dictation":
                DictationNav.IsChecked = true;
                page = DictationPage;
                PageTitleText.Text = "Dictation";
                PageDescriptionText.Text = "Tune your input, local model, and text finishing.";
                break;
            case "Visual context":
                ContextNav.IsChecked = true;
                page = ContextPage;
                PageTitleText.Text = "Visual context";
                PageDescriptionText.Text = "Control how pointer circles capture what you are referencing.";
                break;
            case "Shortcuts":
                ShortcutsNav.IsChecked = true;
                page = ShortcutsPage;
                PageTitleText.Text = "Shortcuts";
                PageDescriptionText.Text = "Make recording feel immediate without accidental triggers.";
                break;
            case "Storage":
                StorageNav.IsChecked = true;
                page = StoragePage;
                PageTitleText.Text = "Storage";
                PageDescriptionText.Text = "Review local sessions and BetterVoice privacy boundaries.";
                break;
            default:
                OverviewNav.IsChecked = true;
                page = OverviewPage;
                PageTitleText.Text = "Overview";
                PageDescriptionText.Text = "Your recording setup at a glance.";
                break;
        }

        OverviewPage.Visibility = Visibility.Collapsed;
        DictationPage.Visibility = Visibility.Collapsed;
        ContextPage.Visibility = Visibility.Collapsed;
        ShortcutsPage.Visibility = Visibility.Collapsed;
        StoragePage.Visibility = Visibility.Collapsed;

        page.Visibility = Visibility.Visible;
        page.Opacity = 0;
        var translate = new TranslateTransform(0, 7);
        page.RenderTransform = translate;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void OnOpenDictationClicked(object sender, RoutedEventArgs e) => NavigateTo("Dictation");

    private void OnOpenContextClicked(object sender, RoutedEventArgs e) => NavigateTo("Visual context");

    private void OnDoneClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        string? destination = e.Key switch
        {
            Key.D1 => "Overview",
            Key.D2 => "Dictation",
            Key.D3 => "Visual context",
            Key.D4 => "Shortcuts",
            Key.D5 => "Storage",
            _ => null
        };
        if (destination == null) return;
        NavigateTo(destination);
        e.Handled = true;
    }

    private void OnOpenVocabularyClicked(object sender, RoutedEventArgs e)
    {
        string path = VocabularyFile.DefaultPath();
        VocabularyFile.CreateTemplateIfMissing(path);
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // fallback
        }
    }

    private void OnOpenSessionsClicked(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterVoice", "Sessions");
        Directory.CreateDirectory(dir);
        try
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // fallback
        }
    }
}
