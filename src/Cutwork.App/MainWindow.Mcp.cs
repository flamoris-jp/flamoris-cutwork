using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.Mcp;
using Flamoris.Capture.Wpf;
using Flamoris.Mcp.Core;
using Flamoris.Mcp.Wpf;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private EmbeddedWindowCapture? _capture;
    private CutworkMcpHost? _mcpHost;
    private McpBoundary? _mcpBoundary;
    private CapabilityGrant? _mcpGrant;
    private McpDesktopUi? _mcpUi;
    private bool _remoteEditing;
    private int _fileCoordination;
    private TextBox? _inlineName;

    private void InitializeMcp()
    {
        _mcpHost = new CutworkMcpHost(_session, HumanBusy, DispatchMcp);
        _mcpUi = new McpDesktopUi(this, McpMenu, "flamoris-cutwork",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "Cutwork", "mcp-connection.json"),
            "FLAMORIS.Cutwork", Path.Combine(AppContext.BaseDirectory, "mcp", "Flamoris.Mcp.Bridge.exe"),
            AttachMcp, () => _session.Document is not null, () => LocalizationService.Current.Culture);
        McpStatusPanel.Content = _mcpUi.StatusIndicator;
        _capture = new EmbeddedWindowCapture(this, StopMcp);
        CaptureStatusPanel.Content = _capture.Control;
        _session.DocumentReplacing += (_, _) => { _capture.Stop(); StopMcp(); };
        Closed += (_, _) => ShutdownMcp();
        Dispatcher.ShutdownStarted += (_, _) => ShutdownMcp();
        UpdateMcp();
    }
    private async Task<McpDesktopAttachment> AttachMcp(McpPermission permission)
    {
        var captureLease = _capture!.BeginConnection();
        McpBoundary? boundary = null;
        try
        {
            var editor = new LiveEditor(_session, permission, HumanBusy, SetRemoteEditing,
                CutworkLog.Current, currentGrant: () => _mcpGrant,workspace:_workspace,
                dispatchCompletion: action => DispatchMcp(action,CancellationToken.None));
            boundary = new McpBoundary(_mcpHost!, CutworkMcpTools.Create(editor).Concat(_capture.Tools()), new McpOptions {
                Permission = permission, PipeName = "flamoris-cutwork-" + Guid.NewGuid().ToString("N"),
                MaxConcurrentRequests = 1, MaxRequestBytes = LiveLimits.FrameBytes,
            }, new McpDiagnostics(CutworkLog.Current));
            _mcpBoundary = boundary;
            _mcpGrant = await boundary.EnableAsync(permission);
            return new(boundary, _mcpGrant, () => { captureLease.Dispose(); return Task.CompletedTask; });
        }
        catch { captureLease.Dispose(); boundary?.Dispose(); throw; }
    }
    private void LocalizeMcp() => _mcpUi?.Refresh();
    private void UpdateMcp() { _mcpUi?.Refresh(); _mcpUi?.NotifyHostReady(); }
    private void StopMcp() { _workspace.RevokeAiSave(); AiSaveMenuItem.IsChecked=false; _mcpUi?.Invalidate(); _mcpGrant = null; _mcpBoundary = null; SetRemoteEditing(_workspace.IsSaving); }
    private void ShutdownMcp() { _capture?.Dispose(); _mcpUi?.Shutdown(); _mcpHost?.Shutdown(); }
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
        if(!active)UpdateFileCommandState();
        CommandManager.InvalidateRequerySuggested();
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
