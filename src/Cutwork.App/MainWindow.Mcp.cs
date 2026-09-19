using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Mcp;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private LiveAccess? _mcpAccess;
    private string? _mcpPipe;
    private Window? _mcpInfo;
    private bool _remoteEditing;
    private int _fileCoordination;
    private TextBox? _inlineName;
    private void InitializeMcp()
    {
        _session.DocumentReplacing += (_, _) => StopMcp();
        Closed += (_, _) => StopMcp();
        Dispatcher.ShutdownStarted += (_, _) => StopMcp();
        _session.Changed += (_, _) => { if (_mcpAccess is { IsActive: false }) StopMcp(); };
    }
    private bool HumanBusy() => _fileCoordination > 0 || _inlineName is not null
        || _partTool.State != PartToolState.Idle || _maskTool.State != MaskBrushState.Idle
        || _cloneTool.State != CloneRepairState.Idle || _inputRouter.HasPendingToolWork
        || _patchTool.HasPending || _blurTool.State != RepairFinishingState.Idle || _smudgeTool.State != RepairFinishingState.Idle
        || SemanticNameEditor.IsKeyboardFocusWithin
        || (_session.SelectedLayerId is { } selected && _session.Document?.GetLayer(selected) is PartLayer currentPart
            && SemanticNameEditor.Text != (currentPart.SemanticName ?? ""))
        || !IsEnabled;
    private void SetRemoteEditing(bool active)
    {
        _remoteEditing = active;
        // Only the MCP menu remains interactive while a chunked transaction is owned remotely.
        WorkArea.IsEnabled = MainToolbar.IsEnabled = !active;
        FileMenu.IsEnabled = EditMenu.IsEnabled = ViewMenu.IsEnabled = LayerMenu.IsEnabled = ExportMenu.IsEnabled = HelpMenu.IsEnabled = !active;
        CommandManager.InvalidateRequerySuggested();
    }
    private void McpMenu_Opened(object sender, RoutedEventArgs e) => UpdateMcp();
    private void LocalizeMcp()
    {
        var t = LocalizationService.Current;
        McpMenu.Header = "MCP / AI";
        McpReadMenu.Header = t["Mcp_Read"];
        McpEditMenu.Header = t["Mcp_Edit"];
        McpStopMenu.Header = t["Mcp_Stop"];
        McpCopyMenu.Header = t["Mcp_Copy"];
        McpNotice.Header = t["Mcp_Notice"];
        UpdateMcp();
    }
    private void UpdateMcp()
    {
        bool enabled = _mcpAccess?.IsActive == true;
        McpReadMenu.IsChecked = enabled && _mcpAccess!.Permission == LivePermission.ReadOnly;
        McpEditMenu.IsChecked = enabled && _mcpAccess!.Permission == LivePermission.Edit;
        McpReadMenu.IsEnabled = McpEditMenu.IsEnabled = _session.Document is not null;
        McpStopMenu.IsEnabled = McpCopyMenu.IsEnabled = enabled;
    }
    private void McpRead_Click(object sender, RoutedEventArgs e) => EnableMcp(LivePermission.ReadOnly);
    private void McpEdit_Click(object sender, RoutedEventArgs e) => EnableMcp(LivePermission.Edit);
    private void McpStop_Click(object sender, RoutedEventArgs e) => StopMcp();
    private void McpCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpAccess?.IsActive != true) return;
        try { Clipboard.SetText(Connection()); }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = LocalizationService.Current["Mcp_CopyBusy"]; }
    }
    private string Connection() => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> {
        ["cutwork"] = new { command = Path.Combine(AppContext.BaseDirectory, "mcp", "Cutwork.Bridge.exe"), args = new[] { "--pipe", _mcpPipe } }
    } }, new JsonSerializerOptions { WriteIndented = true });
    private void EnableMcp(LivePermission permission)
    {
        if (_session.Document is null) return;
        StopMcp();
        var access = new LiveAccess(_session, permission, CutworkLog.Current);
        _mcpAccess = access; _mcpPipe = "cutwork-" + Guid.NewGuid().ToString("N");
        CutworkLog.Current.Info("mcp", "MCP server enabled", new Dictionary<string, object?>
        {
            ["permission"] = permission.ToString(),
            ["transport"] = "same-user-named-pipe",
            ["documentToken"] = _session.DocumentToken,
        });
        var text = new TextBox { Text = Connection(), IsReadOnly = true, Margin = new Thickness(16), MinWidth = 620 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(text, "McpConnection");
        _mcpInfo = new Window { Owner = this, Title = LocalizationService.Current["Mcp_Connection"], Content = text,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _mcpInfo.Closed += (_, _) => text.Clear(); _mcpInfo.Show(); UpdateMcp();
        _ = ServeMcp(_mcpPipe, access);
    }
    private void StopMcp()
    {
        var old = _mcpAccess; _mcpAccess = null; _mcpPipe = null;
        old?.Revoke(); _mcpInfo?.Close(); _mcpInfo = null;
        if (old is not null)
            CutworkLog.Current.Info("mcp", "MCP server disabled", new Dictionary<string, object?>
            {
                ["permission"] = old.Permission.ToString(),
                ["transport"] = "same-user-named-pipe",
            });
        UpdateMcp();
    }
    private async Task ServeMcp(string name, LiveAccess access)
    {
        CutworkLog.Current.Info("mcp.transport", "Named-pipe server started", new Dictionary<string, object?>
        {
            ["transport"] = "same-user-named-pipe",
        });
        try
        {
            while (access.IsActive)
            {
                await using var pipe = WindowsLocalPipe.Create(name);
                using var revoke = access.Token.Register(() => pipe.Dispose());
                try
                {
                    await pipe.WaitForConnectionAsync(access.Token);
                    CutworkLog.Current.Info("mcp.transport", "Client connected", new Dictionary<string, object?>
                    {
                        ["transport"] = "same-user-named-pipe",
                    });
                    CutworkLog.Current.Info("mcp.auth", "Same-user transport accepted client", new Dictionary<string, object?>
                    {
                        ["policy"] = "current-user-only",
                        ["permission"] = access.Permission.ToString(),
                    });
                    var editor = new LiveEditor(_session, access, HumanBusy,
                        async () => await Dispatcher.Yield(DispatcherPriority.Background), SetRemoteEditing,
                        CutworkLog.Current);
                    await Task.Run(() => LiveProtocol.ServeAsync(pipe, access,
                        action => Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap(), editor,
                        CutworkLog.Current));
                    CutworkLog.Current.Info("mcp.transport", "Client disconnected", new Dictionary<string, object?>
                    {
                        ["transport"] = "same-user-named-pipe",
                    });
                }
                catch (OperationCanceledException) when (!access.IsActive) { }
                catch (ObjectDisposedException) when (!access.IsActive) { }
                catch (UnauthorizedAccessException exception)
                {
                    CutworkLog.Current.Warn("mcp.auth", "Named-pipe access denied", new Dictionary<string, object?>
                    {
                        ["transport"] = "same-user-named-pipe",
                        ["exceptionType"] = exception.GetType().Name,
                    });
                }
                catch (Exception exception)
                {
                    LiveDiagnostics.TransportFailure(CutworkLog.Current, exception, recoverable: true);
                }
            }
        }
        catch (Exception exception)
        {
            LiveDiagnostics.TransportFailure(CutworkLog.Current, exception, recoverable: false);
            StatusText.Text = LocalizationService.Current["Mcp_Unavailable"];
        }
        finally
        {
            CutworkLog.Current.Info("mcp.transport", "Named-pipe server stopped", new Dictionary<string, object?>
            {
                ["transport"] = "same-user-named-pipe",
            });
            if (ReferenceEquals(access, _mcpAccess)) StopMcp(); else access.Revoke();
        }
    }
    private IDisposable CoordinateFiles()
    {
        if (_remoteEditing) throw new InvalidOperationException("Remote operation active.");
        _fileCoordination++;
        return new Coordination(() => _fileCoordination--);
    }
    private sealed class Coordination(Action end) : IDisposable { public void Dispose() => end(); }
}
