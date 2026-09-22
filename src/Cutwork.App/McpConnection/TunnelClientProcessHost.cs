using System.Diagnostics;

namespace Flamoris.Cutwork.App.McpConnection;

public sealed class TunnelClientException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IOwnedProcess : IDisposable
{
    bool HasExited { get; }
    event Action? Exited;
    void Kill(bool entireProcessTree);
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IOwnedProcessLauncher
{
    IOwnedProcess Start(ProcessStartInfo startInfo);
}

public sealed class SystemOwnedProcessLauncher : IOwnedProcessLauncher
{
    public IOwnedProcess Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new TunnelClientException("tunnel_client_start_failed");
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new SystemOwnedProcess(process);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    private sealed class SystemOwnedProcess : IOwnedProcess
    {
        private readonly Process process;
        public SystemOwnedProcess(Process process)
        {
            this.process = process;
            process.EnableRaisingEvents = true;
            process.Exited += ProcessExited;
        }

        public bool HasExited => process.HasExited;
        public event Action? Exited;
        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);
        public void Dispose()
        {
            process.Exited -= ProcessExited;
            process.Dispose();
        }
        private void ProcessExited(object? sender, EventArgs e) => Exited?.Invoke();
    }
}

/// <summary>Starts and stops only the exact child process owned by this Cutwork instance.</summary>
public sealed class TunnelClientProcessHost(IOwnedProcessLauncher launcher) : IAsyncDisposable
{
    private readonly object gate = new();
    private IOwnedProcess? owned;
    private bool unexpectedExit;

    public event Action? UnexpectedExit;

    public async ValueTask StartAsync(OpenAiTunnelClientSettings settings,
        McpConnectionMaterial material, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(material);
        settings.Validate();
        material.Validate();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new TunnelClientException("control_plane_credential_invalid");

        lock (gate)
            if (owned is { HasExited: false })
                throw new TunnelClientException("tunnel_client_already_running");

        Directory.CreateDirectory(settings.ProfileDirectory);
        string yaml = TunnelClientConfiguration.BuildYaml(settings, material.PipeName);
        string temporary = settings.ProfilePath + "." + Environment.ProcessId + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, yaml, cancellationToken);
            File.Move(temporary, settings.ProfilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }

        var start = new ProcessStartInfo
        {
            FileName = settings.TunnelClientExecutable,
            WorkingDirectory = Path.GetDirectoryName(settings.TunnelClientExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(settings.ProfileName);
        start.Environment[TunnelClientConfiguration.ApiKeyEnvironmentVariable] = apiKey;
        start.Environment[TunnelClientConfiguration.CapabilityEnvironmentVariable] = material.Capability;

        IOwnedProcess child;
        try { child = launcher.Start(start); }
        catch (TunnelClientException) { throw; }
        catch { throw new TunnelClientException("tunnel_client_start_failed"); }
        finally
        {
            start.Environment.Remove(TunnelClientConfiguration.ApiKeyEnvironmentVariable);
            start.Environment.Remove(TunnelClientConfiguration.CapabilityEnvironmentVariable);
        }

        if (child.HasExited)
        {
            child.Dispose();
            throw new TunnelClientException("tunnel_client_start_failed");
        }

        lock (gate)
        {
            if (owned is { HasExited: false })
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                child.Dispose();
                throw new TunnelClientException("tunnel_client_already_running");
            }
            owned?.Dispose();
            unexpectedExit = false;
            owned = child;
            child.Exited += OwnedExited;
        }
        if (child.HasExited) OwnedExited();
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        IOwnedProcess? child;
        bool exitedUnexpectedly;
        lock (gate)
        {
            child = owned;
            owned = null;
            exitedUnexpectedly = unexpectedExit;
            unexpectedExit = false;
            if (child is not null) child.Exited -= OwnedExited;
        }
        if (child is null) return;

        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(timeout.Token);
            if (exitedUnexpectedly)
                throw new TunnelClientException("tunnel_client_exited");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TunnelClientException) { throw; }
        catch { throw new TunnelClientException("tunnel_client_stop_failed"); }
        finally { child.Dispose(); }
    }

    private void OwnedExited()
    {
        lock (gate)
        {
            if (owned is null) return;
            unexpectedExit = true;
        }
        foreach (Action handler in UnexpectedExit?.GetInvocationList() ?? [])
            try { handler(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None); }
        catch (TunnelClientException) { }
    }
}
