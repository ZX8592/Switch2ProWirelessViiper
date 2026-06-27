using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Win32;
using Switch2ProWirelessViiper.Core;
using Windows.Devices.Bluetooth;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Text;
using WinRT.Interop;

namespace Switch2ProWirelessViiper
{
    public sealed partial class MainWindow : Window
    {
        private enum OnboardingStep
        {
            Language,
            Environment,
            Scan,
            Settings
        }
        
        private OnboardingStep _onboardingStep = OnboardingStep.Language;
        
        private StackPanel OnboardingStepLanguage = null!;
        private StackPanel OnboardingStepEnvironment = null!;
        private TextBlock OnboardingEnvDesc = null!;
        private HyperlinkButton OnboardingUsbipButton = null!;
        private StackPanel OnboardingStepScan = null!;
        private TextBlock OnboardingScanTitle = null!;
        private TextBlock OnboardingScanDesc = null!;
        private ProgressRing OnboardingScanProgress = null!;
        private TextBlock OnboardingScanResult = null!;
        private StackPanel OnboardingStepSettings = null!;
        private TextBlock OnboardingSettingsTitle = null!;
        private CheckBox OnboardingStartupCheckBox = null!;
        private CheckBox OnboardingStartToTrayCheckBox = null!;
        private CheckBox OnboardingCloseToTrayCheckBox = null!;
        private CheckBox OnboardingPreloadCheckBox = null!;
        private Button OnboardingNextButton = null!;
        private Button OnboardingBackButton = null!;

private void BuildOnboardingNew()
	{
		OnboardingOverlay = new Grid
		{
			Visibility = Visibility.Collapsed,
			Background = ThemeBrush("SolidBackgroundFillColorBaseBrush", Colors.White)
		};
		Grid.SetRowSpan(OnboardingOverlay, 2);
		RootGrid.Children.Add(OnboardingOverlay);
		Border border = new Border
		{
			Width = 640.0,
			MaxWidth = 640.0,
			MaxHeight = 620.0,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", Colors.White),
			BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 229, 229, 229)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(48.0)
		};
		OnboardingOverlay.Children.Add(border);
		UIElement uIElement = (border.Child = new StackPanel());
		StackPanel obj = (StackPanel)uIElement;
		OnboardingTitleText = Text("First run setup", 24.0, Weight(600), ThemeBrush("TextFillColorPrimaryBrush", Colors.Black));
		OnboardingSubtitleText = Text("Choose language, check the environment, and scan for the controller.", 14.0, Weight(400), ThemeBrush("TextFillColorSecondaryBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 96, 96, 96)));
		OnboardingSubtitleText.TextWrapping = TextWrapping.Wrap;
		OnboardingSubtitleText.Margin = new Thickness(0.0, 8.0, 0.0, 32.0);
		obj.Children.Add(OnboardingTitleText);
		obj.Children.Add(OnboardingSubtitleText);
		Grid grid = new Grid();
		obj.Children.Add(grid);
		OnboardingStepLanguage = new StackPanel
		{
			Visibility = Visibility.Visible
		};
		OnboardingLanguageLabel = Label("Language");
		OnboardingLanguageCombo = LanguageSelector();
		OnboardingLanguageCombo.SelectionChanged += OnboardingLanguageCombo_SelectionChanged;
		OnboardingStepLanguage.Children.Add(OnboardingLanguageLabel);
		OnboardingStepLanguage.Children.Add(OnboardingLanguageCombo);
		grid.Children.Add(OnboardingStepLanguage);
		OnboardingStepEnvironment = new StackPanel
		{
			Visibility = Visibility.Collapsed
		};
		EnvironmentTitleText = SectionTitle("Environment check");
		OnboardingEnvDesc = Text("We need to verify if usbip-win2 is installed on your system.", 14.0, Weight(400), ThemeBrush("TextFillColorSecondaryBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 96, 96, 96)));
		OnboardingEnvDesc.TextWrapping = TextWrapping.Wrap;
		OnboardingEnvDesc.Margin = new Thickness(0.0, 0.0, 0.0, 16.0);
		Border border2 = new Border
		{
			Background = ThemeBrush("CardBackgroundFillColorSecondaryBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 243, 248, byte.MaxValue)),
			BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 200, 224, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(16.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		};
		EnvironmentStatusText = Text(string.Empty, 14.0, Weight(400), ThemeBrush("TextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 31, 31, 31)));
		EnvironmentStatusText.TextWrapping = TextWrapping.Wrap;
		border2.Child = EnvironmentStatusText;
		OnboardingUsbipButton = new HyperlinkButton
		{
			Content = "Download usbip-win2",
			NavigateUri = new Uri("https://github.com/vadimgrn/usbip-win2/releases"),
			Visibility = Visibility.Collapsed
		};
		OnboardingStepEnvironment.Children.Add(EnvironmentTitleText);
		OnboardingStepEnvironment.Children.Add(OnboardingEnvDesc);
		OnboardingStepEnvironment.Children.Add(border2);
		OnboardingStepEnvironment.Children.Add(OnboardingUsbipButton);
		grid.Children.Add(OnboardingStepEnvironment);
		OnboardingStepScan = new StackPanel
		{
			Visibility = Visibility.Collapsed
		};
		OnboardingScanTitle = SectionTitle("Pair Controller");
		OnboardingScanDesc = Text("Press and hold the pairing button on the top of your controller until the LEDs start flashing rapidly. Then click Scan below.", 14.0, Weight(400), ThemeBrush("TextFillColorSecondaryBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 96, 96, 96)));
		OnboardingScanDesc.TextWrapping = TextWrapping.Wrap;
		OnboardingScanDesc.Margin = new Thickness(0.0, 0.0, 0.0, 24.0);
		OnboardingScanButton = new Button
		{
			Content = "Scan",
			Width = 200.0,
			Height = 48.0,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		ApplyStyle(OnboardingScanButton, "AccentButtonStyle");
		OnboardingScanButton.Click += OnboardingScanButton_Click_New;
		OnboardingScanProgress = new ProgressRing
		{
			IsActive = false,
			Width = 32.0,
			Height = 32.0,
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0),
			HorizontalAlignment = HorizontalAlignment.Center
		};
		OnboardingScanResult = Text(string.Empty, 14.0, Weight(600), ThemeBrush("SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 16, 124, 16)));
		OnboardingScanResult.HorizontalAlignment = HorizontalAlignment.Center;
		OnboardingScanResult.Margin = new Thickness(0.0, 16.0, 0.0, 0.0);
		OnboardingStepScan.Children.Add(OnboardingScanTitle);
		OnboardingStepScan.Children.Add(OnboardingScanDesc);
		OnboardingStepScan.Children.Add(OnboardingScanButton);
		OnboardingStepScan.Children.Add(OnboardingScanProgress);
		OnboardingStepScan.Children.Add(OnboardingScanResult);
		grid.Children.Add(OnboardingStepScan);
		OnboardingStepSettings = new StackPanel
		{
			Visibility = Visibility.Collapsed
		};
		OnboardingSettingsTitle = SectionTitle("Startup Settings");
		OnboardingStartupCheckBox = OnboardingCheckBox("Start with Windows");
		OnboardingCloseToTrayCheckBox = OnboardingCheckBox("Close to system tray");
		OnboardingCloseToTrayCheckBox.IsChecked = true;
		OnboardingStartToTrayCheckBox = OnboardingCheckBox("Start hidden in tray");
		OnboardingPreloadCheckBox = OnboardingCheckBox("Preload VIIPER");
		OnboardingPreloadCheckBox.IsChecked = true;
		OnboardingStepSettings.Children.Add(OnboardingSettingsTitle);
		OnboardingStepSettings.Children.Add(OnboardingStartupCheckBox);
		OnboardingStepSettings.Children.Add(OnboardingCloseToTrayCheckBox);
		OnboardingStepSettings.Children.Add(OnboardingStartToTrayCheckBox);
		OnboardingStepSettings.Children.Add(OnboardingPreloadCheckBox);
		grid.Children.Add(OnboardingStepSettings);
		Grid grid2 = new Grid
		{
			Margin = new Thickness(0.0, 48.0, 0.0, 0.0)
		};
		OnboardingBackButton = new Button
		{
			Content = "Back",
			Visibility = Visibility.Collapsed,
			Width = 100.0
		};
		OnboardingBackButton.Click += delegate
		{
			GoPrevStep();
		};
		OnboardingNextButton = new Button
		{
			Content = "Next",
			HorizontalAlignment = HorizontalAlignment.Right,
			Width = 100.0
		};
		ApplyStyle(OnboardingNextButton, "AccentButtonStyle");
		OnboardingNextButton.Click += delegate
		{
			GoNextStep();
		};
		grid2.Children.Add(OnboardingBackButton);
		grid2.Children.Add(OnboardingNextButton);
		obj.Children.Add(grid2);
	}

private void GoNextStep()
	{
		if (_onboardingStep == OnboardingStep.Language)
		{
			_onboardingStep = OnboardingStep.Environment;
			_ = CheckEnvironmentAsync();
		}
		else if (_onboardingStep == OnboardingStep.Environment)
		{
			_onboardingStep = OnboardingStep.Scan;
		}
		else if (_onboardingStep == OnboardingStep.Scan)
		{
			_onboardingStep = OnboardingStep.Settings;
			OnboardingNextButton.Content = T("finish");
		}
		else if (_onboardingStep == OnboardingStep.Settings)
		{
			_settings.FirstRunCompleted = true;
			_settings.StartWithWindows = OnboardingStartupCheckBox.IsChecked == true;
			_settings.MinimizeToTray = OnboardingCloseToTrayCheckBox.IsChecked == true;
			_settings.StartToTray = OnboardingStartToTrayCheckBox.IsChecked == true;
			_settings.PreloadViiper = OnboardingPreloadCheckBox.IsChecked == true;
			_settings.Save();
			ApplyStartupRegistration();
			OnboardingOverlay.Visibility = Visibility.Collapsed;
			MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
			StartupCheckBox.IsChecked = _settings.StartWithWindows;
			StartToTrayCheckBox.IsChecked = _settings.StartToTray;
			PreloadViiperCheckBox.IsChecked = _settings.PreloadViiper;
			return;
		}
		UpdateOnboardingStepUi();
	}

private void GoPrevStep()
	{
		if (_onboardingStep == OnboardingStep.Environment)
		{
			_onboardingStep = OnboardingStep.Language;
		}
		else if (_onboardingStep == OnboardingStep.Scan)
		{
			_onboardingStep = OnboardingStep.Environment;
		}
		else if (_onboardingStep == OnboardingStep.Settings)
		{
			_onboardingStep = OnboardingStep.Scan;
			OnboardingNextButton.Content = T("next");
		}
		UpdateOnboardingStepUi();
	}

    private void UpdateOnboardingStepUi()
    {
        if (OnboardingSubtitleText != null)
        {
            var stepName = _onboardingStep switch
            {
                OnboardingStep.Environment => T("stepEnvironment"),
                OnboardingStep.Scan => T("stepScan"),
                OnboardingStep.Settings => T("stepSettings"),
                _ => T("stepLanguage"),
            };
            OnboardingSubtitleText.Text = string.Format(T("onboardingStepFormat"), (int)_onboardingStep + 1, stepName);
        }
        OnboardingStepLanguage.Visibility = ((_onboardingStep != OnboardingStep.Language) ? Visibility.Collapsed : Visibility.Visible);
        OnboardingStepEnvironment.Visibility = ((_onboardingStep != OnboardingStep.Environment) ? Visibility.Collapsed : Visibility.Visible);
        OnboardingStepScan.Visibility = ((_onboardingStep != OnboardingStep.Scan) ? Visibility.Collapsed : Visibility.Visible);
        OnboardingStepSettings.Visibility = ((_onboardingStep != OnboardingStep.Settings) ? Visibility.Collapsed : Visibility.Visible);
        OnboardingBackButton.Visibility = ((_onboardingStep == OnboardingStep.Language) ? Visibility.Collapsed : Visibility.Visible);
        OnboardingBackButton.Content = T("back");
        OnboardingNextButton.Content = _onboardingStep == OnboardingStep.Settings ? T("finish") : T("next");
    }

	private async Task CheckEnvironmentAsync()
	{
		EnvironmentStatusText.Text = T("checkingUsbip");
		UsbipEnvironmentStatus environment;
		try
		{
			environment = await UsbipVirtualController.InspectAsync(CancellationToken.None);
		}
		catch (Exception ex)
		{
			environment = new UsbipEnvironmentStatus(null, false, ex.Message);
		}
		if (environment.IsReady)
		{
			EnvironmentStatusText.Text = $"{T("usbipReady")}{Environment.NewLine}{environment.Details}";
			EnvironmentStatusText.Foreground = ThemeBrush("SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 16, 124, 16));
			OnboardingUsbipButton.Visibility = Visibility.Collapsed;
		}
		else
		{
			EnvironmentStatusText.Text = $"{T("usbipMissing")}{Environment.NewLine}{environment.Details}";
			EnvironmentStatusText.Foreground = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 196, 43, 28));
			OnboardingUsbipButton.Visibility = Visibility.Visible;
		}
	}

private async void OnboardingScanButton_Click_New(object sender, RoutedEventArgs e)
	{
		OnboardingScanButton.IsEnabled = false;
		OnboardingBackButton.IsEnabled = false;
		OnboardingScanProgress.IsActive = true;
		OnboardingScanResult.Text = string.Empty;
		try
		{
			await Task.Yield();
			CandidateItem[] array = (await _scanner.ScanAsync(TimeSpan.FromSeconds(12L), CancellationToken.None)).Select((BleDeviceCandidate c) => new CandidateItem(c)).ToArray();
			if (array.Length != 0)
			{
				CandidateItem candidateItem = array.First();
				_settings.BluetoothAddress = candidateItem.BluetoothAddress.ToString("X12");
				_settings.Save();
				AddressBox.Text = _settings.BluetoothAddress;
				OnboardingScanResult.Text = string.Format(T("scanSuccessFormat"), candidateItem.DisplayText, candidateItem.BluetoothAddress.ToString("X12"));
				OnboardingScanResult.Foreground = ThemeBrush("SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 16, 124, 16));
			}
			else
			{
				OnboardingScanResult.Text = T("scanNotFound");
				OnboardingScanResult.Foreground = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 196, 43, 28));
			}
		}
		catch (Exception ex)
		{
			Log("Onboarding scan failed: " + ex);
			OnboardingScanResult.Text = string.Format(T("scanFailedFormat"), ex.Message);
			OnboardingScanResult.Foreground = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(byte.MaxValue, 196, 43, 28));
		}
		finally
		{
			OnboardingScanProgress.IsActive = false;
			OnboardingScanButton.IsEnabled = true;
			OnboardingBackButton.IsEnabled = true;
		}
	}

private CheckBox OnboardingCheckBox(string content)
	{
		return new CheckBox
		{
			Content = content,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
	}
    }
}





