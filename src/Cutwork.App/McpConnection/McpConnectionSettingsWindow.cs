using System.Windows;
using System.Windows.Controls;

namespace Flamoris.Cutwork.App.McpConnection;

public sealed class McpConnectionSettingsWindow : Window
{
    private readonly ComboBox method = new();
    private readonly CheckBox autoStart = new();
    private readonly TextBox executable = new();
    private readonly TextBox profileDirectory = new();
    private readonly TextBox profileName = new();
    private readonly TextBox tunnelId = new();
    private readonly TextBox healthAddress = new();
    private readonly PasswordBox apiKey = new();
    private readonly CheckBox deleteCredential = new();
    private readonly string bridgeExecutable;

    public McpConnectionPreferences Preferences { get; private set; }
    public string? NewCredential { get; private set; }
    public bool DeleteCredential => deleteCredential.IsChecked == true;

    public McpConnectionSettingsWindow(McpConnectionPreferences preferences,
        bool credentialExists, string bridgeExecutable)
    {
        Preferences = preferences;
        this.bridgeExecutable = bridgeExecutable;
        var text = LocalizationService.Current;
        Title = text["Mcp_SettingsTitle"];
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        method.Items.Add(new ComboBoxItem { Content = text["Mcp_MethodManual"],
            Tag = McpConnectionMethod.Manual });
        method.Items.Add(new ComboBoxItem { Content = text["Mcp_MethodTunnel"],
            Tag = McpConnectionMethod.OpenAiTunnelClient });
        method.SelectedIndex = preferences.Method == McpConnectionMethod.Manual ? 0 : 1;
        autoStart.Content = text["Mcp_AutoStart"];
        autoStart.IsChecked = preferences.AutoStart;
        executable.Text = preferences.TunnelClientExecutable;
        profileDirectory.Text = preferences.ProfileDirectory;
        profileName.Text = preferences.ProfileName;
        tunnelId.Text = preferences.TunnelId;
        healthAddress.Text = preferences.HealthListenAddress;
        deleteCredential.Content = text["Mcp_DeleteCredential"];
        deleteCredential.IsEnabled = credentialExists;

        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        int row = 0;
        AddRow(grid, ref row, text["Mcp_Method"], method);
        AddRow(grid, ref row, "", autoStart);
        AddRow(grid, ref row, text["Mcp_TunnelExecutable"], executable);
        AddRow(grid, ref row, text["Mcp_ProfileDirectory"], profileDirectory);
        AddRow(grid, ref row, text["Mcp_ProfileName"], profileName);
        AddRow(grid, ref row, text["Mcp_TunnelId"], tunnelId);
        AddRow(grid, ref row, text["Mcp_HealthAddress"], healthAddress);
        AddRow(grid, ref row, text["Mcp_ApiKey"], apiKey);
        AddRow(grid, ref row, "", new TextBlock
        {
            Text = credentialExists ? text["Mcp_CredentialSaved"] : text["Mcp_CredentialMissing"],
            Margin = new Thickness(0, 0, 0, 5),
        });
        AddRow(grid, ref row, "", deleteCredential);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var save = new Button { Content = text["Common_Save"], MinWidth = 88, Margin = new Thickness(4) };
        var cancel = new Button { Content = text["Common_Cancel"], MinWidth = 88, Margin = new Thickness(4) };
        save.Click += SaveClicked;
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, row);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }

    private static void AddRow(Grid grid, ref int row, string label, FrameworkElement editor)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        editor.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(caption, row);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(caption);
        grid.Children.Add(editor);
        row++;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (method.SelectedItem is not ComboBoxItem { Tag: McpConnectionMethod selectedMethod })
            return;
        var candidate = Preferences with
        {
            Method = selectedMethod,
            AutoStart = autoStart.IsChecked == true,
            TunnelClientExecutable = executable.Text.Trim(),
            ProfileDirectory = profileDirectory.Text.Trim(),
            ProfileName = profileName.Text.Trim(),
            TunnelId = tunnelId.Text.Trim(),
            HealthListenAddress = healthAddress.Text.Trim(),
        };
        try
        {
            if (candidate.Method == McpConnectionMethod.OpenAiTunnelClient)
                candidate.ToProviderSettings(bridgeExecutable).Validate();
            Preferences = candidate;
            NewCredential = string.IsNullOrWhiteSpace(apiKey.Password) ? null : apiKey.Password;
            apiKey.Clear();
            DialogResult = true;
        }
        catch (ArgumentException)
        {
            MessageBox.Show(this, LocalizationService.Current["Mcp_SettingsInvalid"],
                LocalizationService.Current["Mcp_SettingsTitle"], MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
