using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Flamoris.Cutwork.App.McpConnection;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Mcp;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private readonly McpConnectionPreferencesStore _mcpPreferencesStore = new();
    private readonly IProviderCredentialStore _mcpCredentialStore = new WindowsCredentialStore();
    private McpConnectionPreferences _mcpPreferences = new();
    private CutworkMcpHost? _mcpHost;
    private McpBoundary? _mcpBoundary;
    private CapabilityGrant? _mcpGrant;
    private CancellationTokenSource? _mcpLifetime;
    private ManagedConnectionLifecycle? _mcpManagedLifecycle;
    private McpConnectionController? _mcpConnection;
    private TunnelClientProcessHost? _mcpProcessHost;
    private Window? _mcpInfo;
    private readonly object _mcpStopGate = new();
    private Task? _mcpStopTask;
    private bool _mcpStopping;
    private bool _mcpShutdown;
    private bool _remoteEditing;
    private bool _mcpActivityCursor;
    private Cursor? _mcpPreviousCursor;
    private int _fileCoordination;
    private TextBox? _inlineName;

    private string McpBridgeExecutable => Path.Combine(
        AppContext.BaseDirectory, "mcp", "Flamoris.Mcp.Bridge.exe");

    private void InitializeMcp()
    {
        _mcpPreferences = _mcpPreferencesStore.Load();
        _mcpHost = new CutworkMcpHost(_session, HumanBusy, DispatchMcp);
        _session.DocumentReplacing += (_, _) => StopMcp();
        Closed += (_, _) => ShutdownMcp();
        Dispatcher.ShutdownStarted += (_, _) => ShutdownMcp();
        UpdateMcp();
    }

    private Task DispatchMcp(Action action, CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            throw new McpFault(McpErrors.HostUnavailable);
        if (Dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }, DispatcherPriority.Background, cancellationToken).Task;
    }

    private bool HumanBusy() => _fileCoordination > 0 || _inlineName is not null
        || _partTool.State != PartToolState.Idle || _maskTool.State != MaskBrushState.Idle
        || _cloneTool.State != CloneRepairState.Idle || _inputRouter.HasPendingToolWork
        || _patchTool.HasPending || _blurTool.State != RepairFinishingState.Idle
        || _smudgeTool.State != RepairFinishingState.Idle
        || SemanticNameEditor.IsKeyboardFocusWithin
        || (_session.SelectedLayerId is { } selected
            && _session.Document?.GetLayer(selected) is PartLayer currentPart
            && SemanticNameEditor.Text != (currentPart.SemanticName ?? ""))
        || !IsEnabled;

    private void SetRemoteEditing(bool active)
    {
        _remoteEditing = active;
        WorkArea.IsEnabled = MainToolbar.IsEnabled = !active;
        FileMenu.IsEnabled = EditMenu.IsEnabled = ViewMenu.IsEnabled = LayerMenu.IsEnabled
            = ExportMenu.IsEnabled = HelpMenu.IsEnabled = !active;
        CommandManager.InvalidateRequerySuggested();
    }

    private void McpMenu_Opened(object sender, RoutedEventArgs e) => UpdateMcp();

    private void LocalizeMcp()
    {
        var text = LocalizationService.Current;
        McpMenu.Header = "MCP / AI";
        McpReadMenu.Header = text["Mcp_Read"];
        McpEditMenu.Header = text["Mcp_Edit"];
        McpStopMenu.Header = text["Mcp_Stop"];
        McpCopyMenu.Header = text["Mcp_Copy"];
        McpMethodMenu.Header = text["Mcp_Method"];
        McpManualMethodMenu.Header = text["Mcp_MethodManual"];
        McpTunnelMethodMenu.Header = text["Mcp_MethodTunnel"];
        McpAutoStartMenu.Header = text["Mcp_AutoStart"];
        McpStartHelperMenu.Header = text["Mcp_StartHelper"];
        McpStopHelperMenu.Header = text["Mcp_StopHelper"];
        McpSettingsMenu.Header = text["Mcp_Settings"];
        McpNotice.Header = text["Mcp_Notice"];
        UpdateMcp();
    }

    private void UpdateMcp()
    {
        bool enabled = _mcpGrant?.IsActive == true && _mcpBoundary?.Status.Current.Enabled == true;
        McpReadMenu.IsChecked = enabled && _mcpGrant!.Permission == McpPermission.ReadOnly;
        McpEditMenu.IsChecked = enabled && _mcpGrant!.Permission == McpPermission.Edit;
        McpReadMenu.IsEnabled = McpEditMenu.IsEnabled = _session.Document is not null && !_mcpStopping;
        McpStopMenu.IsEnabled = McpCopyMenu.IsEnabled = enabled && !_mcpStopping;

        var managed = _mcpConnection?.Current;
        bool tunnel = _mcpPreferences.Method == McpConnectionMethod.OpenAiTunnelClient;
        McpManualMethodMenu.IsChecked = !tunnel;
        McpTunnelMethodMenu.IsChecked = tunnel;
        McpAutoStartMenu.IsChecked = _mcpPreferences.AutoStart;
        McpAutoStartMenu.IsEnabled = tunnel && !_mcpStopping;
        McpStartHelperMenu.IsEnabled = enabled && tunnel && !_mcpStopping
            && managed?.ProviderState is ManagedConnectionProviderState.Stopped
                or ManagedConnectionProviderState.Faulted;
        McpStopHelperMenu.IsEnabled = enabled && !_mcpStopping
            && managed?.ProviderState == ManagedConnectionProviderState.Running;
        McpHelperStatusMenu.Header = LocalizationService.Current[HelperStatusKey(
            managed?.ProviderState ?? ManagedConnectionProviderState.Stopped)];

        var status = _mcpBoundary?.Status.Current;
        bool available = status?.IsGreen == true;
        McpStatusDot.Fill = available ? Brushes.LimeGreen : Brushes.IndianRed;
        McpStatusLabel.Text = status?.ActivityVisible == true ? "MCP · AI" : "MCP";
        var text = LocalizationService.Current;
        string boundaryStatus = available
            ? text[status!.Connected ? "Mcp_StatusConnected" : "Mcp_StatusAvailable"]
            : text[enabled ? "Mcp_StatusUnavailable" : "Mcp_StatusDisabled"];
        McpStatusPanel.ToolTip = boundaryStatus + " · "
            + text[HelperStatusKey(managed?.ProviderState ?? ManagedConnectionProviderState.Stopped)];
        SetMcpActivity(status?.ActivityVisible == true);
    }

    private static string HelperStatusKey(ManagedConnectionProviderState state) => state switch
    {
        ManagedConnectionProviderState.Starting => "Mcp_HelperStarting",
        ManagedConnectionProviderState.Running => "Mcp_HelperRunning",
        ManagedConnectionProviderState.Refreshing => "Mcp_HelperRefreshing",
        ManagedConnectionProviderState.Stopping => "Mcp_HelperStopping",
        ManagedConnectionProviderState.Faulted => "Mcp_HelperFaulted",
        _ => "Mcp_HelperStopped",
    };

    private void McpStatusChanged()
    {
        _mcpConnection?.ProjectExternalClient(_mcpBoundary?.Status.Current.Connected == true);
        QueueMcpUpdate();
    }

    private void ManagedStatusChanged() => QueueMcpUpdate();

    private void QueueMcpUpdate()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) UpdateMcp();
        else _ = Dispatcher.BeginInvoke(new Action(UpdateMcp), DispatcherPriority.Background);
    }

    private void SetMcpActivity(bool active)
    {
        if (active == _mcpActivityCursor) return;
        _mcpActivityCursor = active;
        if (active)
        {
            _mcpPreviousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
        }
        else
        {
            Mouse.OverrideCursor = _mcpPreviousCursor;
            _mcpPreviousCursor = null;
        }
    }

    private async void McpRead_Click(object sender, RoutedEventArgs e) =>
        await EnableMcp(McpPermission.ReadOnly);
    private async void McpEdit_Click(object sender, RoutedEventArgs e) =>
        await EnableMcp(McpPermission.Edit);
    private async void McpStop_Click(object sender, RoutedEventArgs e) =>
        await DisableMcpAsync(shutdown: false);

    private void McpCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpGrant?.IsActive != true) return;
        try { Clipboard.SetText(Connection()); }
        catch (System.Runtime.InteropServices.ExternalException)
        { StatusText.Text = LocalizationService.Current["Mcp_CopyBusy"]; }
    }

    private async void McpManualMethod_Click(object sender, RoutedEventArgs e) =>
        await ChangeConnectionMethod(McpConnectionMethod.Manual);
    private async void McpTunnelMethod_Click(object sender, RoutedEventArgs e) =>
        await ChangeConnectionMethod(McpConnectionMethod.OpenAiTunnelClient);

    private async Task ChangeConnectionMethod(McpConnectionMethod method)
    {
        if (_mcpPreferences.Method == method) return;
        if (_mcpGrant?.IsActive == true) await DisableMcpAsync(shutdown: false);
        SaveMcpPreferences(_mcpPreferences with { Method = method });
    }

    private void McpAutoStart_Click(object sender, RoutedEventArgs e) =>
        SaveMcpPreferences(_mcpPreferences with { AutoStart = McpAutoStartMenu.IsChecked });

    private async void McpStartHelper_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpConnection is null) return;
        try { await _mcpConnection.StartManagedConnectionAsync(); }
        catch (ManagedConnectionException) { ShowManagedHelperFailure(); }
    }

    private async void McpStopHelper_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpConnection is null) return;
        try { await _mcpConnection.StopManagedConnectionAsync(); }
        catch (ManagedConnectionException) { ShowManagedHelperFailure(); }
    }

    private async void McpSettings_Click(object sender, RoutedEventArgs e)
    {
        bool credentialExists;
        try { credentialExists = _mcpCredentialStore.Exists(); }
        catch { credentialExists = false; }
        var dialog = new McpConnectionSettingsWindow(
            _mcpPreferences, credentialExists, McpBridgeExecutable) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (_mcpGrant?.IsActive == true) await DisableMcpAsync(shutdown: false);
            SaveMcpPreferences(dialog.Preferences);
            if (dialog.DeleteCredential) _mcpCredentialStore.Delete();
            else if (dialog.TakeNewCredential() is { } credential) _mcpCredentialStore.Save(credential);
        }
        catch
        {
            StatusText.Text = LocalizationService.Current["Mcp_SettingsSaveFailed"];
        }
    }

    private void SaveMcpPreferences(McpConnectionPreferences preferences)
    {
        try
        {
            _mcpPreferencesStore.Save(preferences);
            _mcpPreferences = preferences;
        }
        catch
        {
            StatusText.Text = LocalizationService.Current["Mcp_SettingsSaveFailed"];
        }
        UpdateMcp();
    }

    private string Connection()
    {
        if (_mcpBoundary is null || _mcpGrant?.IsActive != true) return "{}";
        return JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["cutwork"] = new
                {
                    command = McpBridgeExecutable,
                    args = new[] { "--pipe", _mcpBoundary.Options.PipeName },
                    env = new Dictionary<string, string>
                    {
                        [StdioBridge.CredentialEnvironmentVariable] = _mcpGrant.ExportCredential(),
                    },
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task EnableMcp(McpPermission permission)
    {
        if (_session.Document is null || _mcpHost is null) return;
        await DisableMcpAsync(shutdown: false);

        var options = new McpOptions
        {
            Enabled = true,
            Permission = permission,
            PipeName = "flamoris-cutwork-" + Guid.NewGuid().ToString("N"),
            MaxConcurrentRequests = 1,
            MaxRequestBytes = LiveLimits.FrameBytes,
            RequestTimeoutMs = 15_000,
            ReadTimeoutMs = 120_000,
            WriteTimeoutMs = 5_000,
        };
        var editor = new LiveEditor(_session, permission, HumanBusy, SetRemoteEditing,
            CutworkLog.Current, currentGrant: () => _mcpGrant);
        var boundary = new McpBoundary(_mcpHost, CutworkMcpTools.Create(editor), options,
            new McpDiagnostics(CutworkLog.Current));
        boundary.Status.Changed += McpStatusChanged;

        var processHost = new TunnelClientProcessHost(new SystemOwnedProcessLauncher());
        var materialSource = new CurrentMcpMaterialSource(this);
        var provider = new TunnelClientConnectionProvider(
            _mcpPreferences.ToProviderSettings(McpBridgeExecutable),
            materialSource, _mcpCredentialStore, processHost);
        var lifecycle = new ManagedConnectionLifecycle(provider, RevokeCurrentMcpAsync);
        var connection = new McpConnectionController(lifecycle);
        connection.Changed += ManagedStatusChanged;
        processHost.UnexpectedExit += ManagedHelperExited;

        _mcpBoundary = boundary;
        _mcpProcessHost = processHost;
        _mcpManagedLifecycle = lifecycle;
        _mcpConnection = connection;

        try
        {
            bool autoStart = _mcpPreferences.Method == McpConnectionMethod.OpenAiTunnelClient
                && _mcpPreferences.AutoStart;
            try { await connection.EnableAsync(IssueCurrentMcpGrantAsync, autoStart); }
            catch (ManagedConnectionException) { ShowManagedHelperFailure(); }
            ShowManualConnection();
            UpdateMcp();
        }
        catch
        {
            if (ReferenceEquals(boundary, _mcpBoundary))
            {
                StatusText.Text = LocalizationService.Current["Mcp_Unavailable"];
                await DisableMcpAsync(shutdown: false);
            }
        }
    }

    private async Task IssueCurrentMcpGrantAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var boundary = _mcpBoundary ?? throw new InvalidOperationException("MCP boundary unavailable.");
        var grant = await boundary.EnableAsync(boundary.Options.Permission);
        if (!ReferenceEquals(boundary, _mcpBoundary))
        {
            grant.Dispose();
            throw new OperationCanceledException("MCP enable was superseded.");
        }
        _mcpGrant = grant;
        _mcpLifetime = new CancellationTokenSource();
        _ = RunMcpEndpoint(boundary, grant, _mcpLifetime.Token);
    }

    private void ShowManualConnection()
    {
        var text = new TextBox
        {
            Text = Connection(),
            IsReadOnly = true,
            Margin = new Thickness(16),
            MinWidth = 620,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(text, "McpConnection");
        _mcpInfo = new Window
        {
            Owner = this,
            Title = LocalizationService.Current["Mcp_Connection"],
            Content = text,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _mcpInfo.Closed += (_, _) => text.Clear();
        _mcpInfo.Show();
    }

    private async Task RunMcpEndpoint(McpBoundary boundary, CapabilityGrant grant,
        CancellationToken cancellationToken)
    {
        try { await new LocalMcpEndpoint(boundary).RunAsync(grant, cancellationToken); }
        catch (Exception exception) when (exception is OperationCanceledException or McpFault) { }
        finally { QueueMcpUpdate(); }
    }

    private ValueTask RevokeCurrentMcpAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var boundary = _mcpBoundary;
        var grant = _mcpGrant;
        var lifetime = _mcpLifetime;
        _mcpBoundary = null;
        _mcpGrant = null;
        _mcpLifetime = null;
        if (boundary is not null) boundary.Status.Changed -= McpStatusChanged;

        grant?.Dispose();
        lifetime?.Cancel();
        boundary?.Dispose();
        lifetime?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void PrepareMcpUiForDisable()
    {
        _mcpInfo?.Close();
        _mcpInfo = null;
        SetRemoteEditing(false);
        SetMcpActivity(false);
    }

    private Task DisableMcpAsync(bool shutdown, bool uiPrepared = false)
    {
        if (!uiPrepared) PrepareMcpUiForDisable();
        lock (_mcpStopGate)
        {
            if (_mcpStopTask is not null) return _mcpStopTask;
            _mcpStopping = true;
            _mcpStopTask = Task.Run(() => DisableMcpCoreAsync(shutdown));
            return _mcpStopTask;
        }
    }

    private async Task DisableMcpCoreAsync(bool shutdown)
    {
        var connection = _mcpConnection;
        var lifecycle = _mcpManagedLifecycle;
        var processHost = _mcpProcessHost;
        try
        {
            if (connection is not null)
            {
                try
                {
                    if (shutdown) await connection.ShutdownAsync();
                    else await connection.DisableAsync();
                }
                catch (ManagedConnectionException) { ShowManagedHelperFailure(); }
            }
            await RevokeCurrentMcpAsync(CancellationToken.None);
            if (lifecycle is not null) await lifecycle.DisposeAsync();
            if (processHost is not null) await processHost.DisposeAsync();
        }
        finally
        {
            if (connection is not null) connection.Changed -= ManagedStatusChanged;
            if (processHost is not null) processHost.UnexpectedExit -= ManagedHelperExited;
            _mcpConnection = null;
            _mcpManagedLifecycle = null;
            _mcpProcessHost = null;
            lock (_mcpStopGate)
            {
                _mcpStopping = false;
                _mcpStopTask = null;
            }
            QueueMcpUpdate();
        }
    }

    private void ManagedHelperExited()
    {
        if (_mcpStopping || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_mcpConnection is null || _mcpStopping) return;
            try { await _mcpConnection.StopManagedConnectionAsync(); }
            catch (ManagedConnectionException) { ShowManagedHelperFailure(); }
        }), DispatcherPriority.Background);
    }

    private void ShowManagedHelperFailure()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ShowManagedHelperFailure),
                DispatcherPriority.Background);
            return;
        }
        StatusText.Text = LocalizationService.Current["Mcp_HelperUnavailable"];
        UpdateMcp();
    }

    private void StopMcp()
    {
        PrepareMcpUiForDisable();
        DisableMcpAsync(shutdown: false, uiPrepared: true).GetAwaiter().GetResult();
    }

    private void ShutdownMcp()
    {
        if (_mcpShutdown) return;
        _mcpShutdown = true;
        PrepareMcpUiForDisable();
        DisableMcpAsync(shutdown: true, uiPrepared: true).GetAwaiter().GetResult();
        _mcpHost?.Shutdown();
    }

    private IDisposable CoordinateFiles()
    {
        if (_remoteEditing) throw new InvalidOperationException("Remote operation active.");
        _fileCoordination++;
        return new Coordination(() => _fileCoordination--);
    }

    private sealed class CurrentMcpMaterialSource(MainWindow owner) : IMcpConnectionMaterialSource
    {
        public McpConnectionMaterial GetCurrent()
        {
            if (owner._mcpBoundary is not { } boundary || owner._mcpGrant?.IsActive != true)
                throw new TunnelClientException("mcp_material_unavailable");
            return new McpConnectionMaterial(
                boundary.Options.PipeName, owner._mcpGrant.ExportCredential());
        }
    }

    private sealed class Coordination(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
