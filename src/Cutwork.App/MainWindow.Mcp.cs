using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Mcp;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private CutworkMcpHost? _mcpHost;
    private McpBoundary? _mcpBoundary;
    private CapabilityGrant? _mcpGrant;
    private CancellationTokenSource? _mcpLifetime;
    private Window? _mcpInfo;
    private bool _remoteEditing;
    private bool _mcpActivityCursor;
    private Cursor? _mcpPreviousCursor;
    private int _fileCoordination;
    private TextBox? _inlineName;

    private void InitializeMcp()
    {
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
        McpNotice.Header = text["Mcp_Notice"];
        UpdateMcp();
    }

    private void UpdateMcp()
    {
        bool enabled = _mcpGrant?.IsActive == true && _mcpBoundary?.Status.Current.Enabled == true;
        McpReadMenu.IsChecked = enabled && _mcpGrant!.Permission == McpPermission.ReadOnly;
        McpEditMenu.IsChecked = enabled && _mcpGrant!.Permission == McpPermission.Edit;
        McpReadMenu.IsEnabled = McpEditMenu.IsEnabled = _session.Document is not null;
        McpStopMenu.IsEnabled = McpCopyMenu.IsEnabled = enabled;

        var status = _mcpBoundary?.Status.Current;
        bool available = status?.IsGreen == true;
        McpStatusDot.Fill = available ? Brushes.LimeGreen : Brushes.IndianRed;
        McpStatusLabel.Text = status?.ActivityVisible == true ? "MCP · AI" : "MCP";
        var text = LocalizationService.Current;
        McpStatusPanel.ToolTip = available
            ? text[status!.Connected ? "Mcp_StatusConnected" : "Mcp_StatusAvailable"]
            : text[enabled ? "Mcp_StatusUnavailable" : "Mcp_StatusDisabled"];
        SetMcpActivity(status?.ActivityVisible == true);
    }

    private void McpStatusChanged()
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
    private void McpStop_Click(object sender, RoutedEventArgs e) => StopMcp();

    private void McpCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpGrant?.IsActive != true) return;
        try { Clipboard.SetText(Connection()); }
        catch (System.Runtime.InteropServices.ExternalException)
        { StatusText.Text = LocalizationService.Current["Mcp_CopyBusy"]; }
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
                    command = Path.Combine(AppContext.BaseDirectory, "mcp", "Flamoris.Mcp.Bridge.exe"),
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
        StopMcp();
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
        var editor = new LiveEditor(_session, permission, HumanBusy, SetRemoteEditing, CutworkLog.Current);
        var boundary = new McpBoundary(_mcpHost, CutworkMcpTools.Create(editor), options,
            new McpDiagnostics(CutworkLog.Current));
        boundary.Status.Changed += McpStatusChanged;
        _mcpBoundary = boundary;
        try
        {
            var grant = await boundary.EnableAsync(permission);
            if (!ReferenceEquals(boundary, _mcpBoundary))
            {
                grant.Dispose();
                return;
            }
            _mcpGrant = grant;
            _mcpLifetime = new CancellationTokenSource();
            _ = RunMcpEndpoint(boundary, grant, _mcpLifetime.Token);

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
            UpdateMcp();
        }
        catch
        {
            StatusText.Text = LocalizationService.Current["Mcp_Unavailable"];
            if (ReferenceEquals(boundary, _mcpBoundary)) StopMcp();
        }
    }

    private async Task RunMcpEndpoint(McpBoundary boundary, CapabilityGrant grant,
        CancellationToken cancellationToken)
    {
        try { await new LocalMcpEndpoint(boundary).RunAsync(grant, cancellationToken); }
        catch (Exception exception) when (exception is OperationCanceledException or McpFault) { }
        finally
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.BeginInvoke(new Action(UpdateMcp), DispatcherPriority.Background);
        }
    }

    private void StopMcp()
    {
        var boundary = _mcpBoundary;
        var grant = _mcpGrant;
        var lifetime = _mcpLifetime;
        _mcpBoundary = null;
        _mcpGrant = null;
        _mcpLifetime = null;
        if (boundary is not null) boundary.Status.Changed -= McpStatusChanged;
        lifetime?.Cancel();
        boundary?.Dispose();
        grant?.Dispose();
        lifetime?.Dispose();
        _mcpInfo?.Close();
        _mcpInfo = null;
        SetRemoteEditing(false);
        SetMcpActivity(false);
        UpdateMcp();
    }

    private void ShutdownMcp()
    {
        StopMcp();
        _mcpHost?.Shutdown();
    }

    private IDisposable CoordinateFiles()
    {
        if (_remoteEditing) throw new InvalidOperationException("Remote operation active.");
        _fileCoordination++;
        return new Coordination(() => _fileCoordination--);
    }

    private sealed class Coordination(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
