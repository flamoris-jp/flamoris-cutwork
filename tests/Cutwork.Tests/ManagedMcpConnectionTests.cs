using System.Diagnostics;
using Flamoris.Cutwork.App.McpConnection;
using Flamoris.Mcp.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class ManagedMcpConnectionTests
{
    private const string CapabilityOne =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string CapabilityTwo =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string ApiKey = "CONTROL-PLANE-SECRET";

    [TestMethod]
    public async Task ManualModeKeepsGrantAndExternalClientIndependentFromHelper()
    {
        var provider = new CountingProvider();
        bool revoked = false;
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ =>
        {
            revoked = true;
            return ValueTask.CompletedTask;
        });
        var controller = new McpConnectionController(lifecycle);

        await controller.EnableAsync(_ => Task.CompletedTask, startManagedHelper: false);
        controller.ProjectExternalClient(true);
        Assert.IsTrue(controller.Current.McpEnabled);
        Assert.AreEqual(ManagedConnectionProviderState.Stopped, controller.Current.ProviderState);
        Assert.IsTrue(controller.Current.ExternalClientConnected);

        await controller.StartManagedConnectionAsync();
        await controller.StopManagedConnectionAsync();
        Assert.IsTrue(controller.Current.McpEnabled);
        Assert.IsTrue(controller.Current.ExternalClientConnected);
        Assert.AreEqual(1, provider.Starts);
        Assert.AreEqual(1, provider.Stops);

        await controller.DisableAsync();
        Assert.IsTrue(revoked);
        Assert.IsFalse(controller.Current.McpEnabled);
        Assert.IsFalse(controller.Current.ExternalClientConnected);
    }

    [TestMethod]
    public async Task AutoStartAndDuplicateStartLaunchOnlyOneHelper()
    {
        var provider = new CountingProvider();
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ => ValueTask.CompletedTask);
        var controller = new McpConnectionController(lifecycle);

        await controller.EnableAsync(_ => Task.CompletedTask, startManagedHelper: true);
        await Task.WhenAll(controller.StartManagedConnectionAsync(),
            controller.StartManagedConnectionAsync());

        Assert.AreEqual(1, provider.Starts);
        Assert.AreEqual(ManagedConnectionProviderState.Running, controller.Current.ProviderState);
    }

    [TestMethod]
    public async Task SuccessfulGrantMutationIgnoresImmediateCallerCancellationForReconciliation()
    {
        var provider = new CountingProvider();
        bool issued = false;
        bool revoked = false;
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ =>
        {
            revoked = true;
            return ValueTask.CompletedTask;
        });
        var controller = new McpConnectionController(lifecycle);
        using var cancellation = new CancellationTokenSource();

        await controller.EnableAsync(_ =>
        {
            issued = true;
            cancellation.Cancel();
            return Task.CompletedTask;
        }, startManagedHelper: true, cancellation.Token);

        Assert.IsTrue(issued);
        Assert.IsTrue(controller.Current.McpEnabled);
        Assert.AreEqual(1, provider.Starts);
        await controller.DisableAsync();
        Assert.IsTrue(revoked);
    }

    [TestMethod]
    public async Task RotationConsumesLatestPipeAndCapabilityAfterImmediateCancellation()
    {
        using var fixture = new ProviderFixture();
        var material = new MutableMaterialSource(new("flamoris-pipe-one", CapabilityOne));
        var launcher = new FakeLauncher();
        await using var host = new TunnelClientProcessHost(launcher);
        var provider = new TunnelClientConnectionProvider(
            fixture.Settings, material, new FixedCredentialSource(), host);
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ => ValueTask.CompletedTask);
        var controller = new McpConnectionController(lifecycle);
        await controller.EnableAsync(_ => Task.CompletedTask, startManagedHelper: true);

        using var cancellation = new CancellationTokenSource();
        await controller.RefreshAfterGrantRotationAsync(_ =>
        {
            material.Current = new("flamoris-pipe-two", CapabilityTwo);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);

        Assert.AreEqual(2, launcher.Starts.Count);
        Assert.AreEqual(CapabilityTwo,
            launcher.Starts[1].Environment[TunnelClientConfiguration.CapabilityEnvironmentVariable]);
        Assert.AreEqual(ApiKey,
            launcher.Starts[1].Environment[TunnelClientConfiguration.ApiKeyEnvironmentVariable]);
        StringAssert.Contains(File.ReadAllText(fixture.Settings.ProfilePath), "flamoris-pipe-two");
        Assert.IsTrue(launcher.Processes[0].Killed);
        Assert.AreEqual(ManagedConnectionProviderState.Running, controller.Current.ProviderState);
    }

    [TestMethod]
    public async Task DisableRevokesBeforeStoppingExactOwnedChild()
    {
        var order = new List<string>();
        var provider = new CountingProvider(order);
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ =>
        {
            order.Add("revoke");
            return ValueTask.CompletedTask;
        });
        var controller = new McpConnectionController(lifecycle);
        await controller.EnableAsync(_ => Task.CompletedTask, startManagedHelper: true);
        order.Clear();

        await controller.DisableAsync();

        CollectionAssert.AreEqual(new[] { "revoke", "stop" }, order);
    }

    [TestMethod]
    public async Task ProviderFailureIsBoundedAndLeavesManualGrantAvailable()
    {
        using var fixture = new ProviderFixture();
        var launcher = new FakeLauncher { FailStart = true };
        await using var host = new TunnelClientProcessHost(launcher);
        var provider = new TunnelClientConnectionProvider(fixture.Settings,
            new MutableMaterialSource(new("flamoris-pipe-one", CapabilityOne)),
            new FixedCredentialSource(), host);
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ => ValueTask.CompletedTask);
        var controller = new McpConnectionController(lifecycle);

        var failure = await Assert.ThrowsExactlyAsync<ManagedConnectionException>(() =>
            controller.EnableAsync(_ => Task.CompletedTask, startManagedHelper: true));

        Assert.AreEqual(ManagedConnectionErrors.ProviderStartFailed, failure.Code);
        Assert.AreEqual(ManagedConnectionErrors.ProviderStartFailed, failure.Message);
        Assert.IsTrue(controller.Current.McpEnabled);
        Assert.AreEqual(ManagedConnectionProviderState.Faulted, controller.Current.ProviderState);
        controller.ProjectExternalClient(true);
        Assert.IsTrue(controller.Current.ExternalClientConnected);
    }

    [TestMethod]
    public async Task UnexpectedExitBecomesFaultWithoutTouchingUnrelatedProcess()
    {
        using var fixture = new ProviderFixture();
        var unrelated = new FakeProcess();
        var launcher = new FakeLauncher { Unrelated = unrelated };
        await using var host = new TunnelClientProcessHost(launcher);
        var provider = new TunnelClientConnectionProvider(fixture.Settings,
            new MutableMaterialSource(new("flamoris-pipe-one", CapabilityOne)),
            new FixedCredentialSource(), host);
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ => ValueTask.CompletedTask);
        var controller = new McpConnectionController(lifecycle);
        await controller.EnableAsync(_ => Task.CompletedTask, startManagedHelper: true);

        launcher.Processes.Single().ExitUnexpectedly();
        await Assert.ThrowsExactlyAsync<ManagedConnectionException>(
            () => controller.StopManagedConnectionAsync());

        Assert.AreEqual(ManagedConnectionProviderState.Faulted, controller.Current.ProviderState);
        Assert.IsFalse(unrelated.Killed);
    }

    [TestMethod]
    public async Task RepeatedEnableDisableLeavesNoOwnedProcess()
    {
        using var fixture = new ProviderFixture();
        var launcher = new FakeLauncher();
        await using var host = new TunnelClientProcessHost(launcher);
        var provider = new TunnelClientConnectionProvider(fixture.Settings,
            new MutableMaterialSource(new("flamoris-pipe-one", CapabilityOne)),
            new FixedCredentialSource(), host);
        await using var lifecycle = new ManagedConnectionLifecycle(provider, _ => ValueTask.CompletedTask);
        var controller = new McpConnectionController(lifecycle);

        for (int iteration = 0; iteration < 3; iteration++)
        {
            await controller.EnableAsync(_ => Task.CompletedTask, startManagedHelper: true);
            await controller.DisableAsync();
        }

        Assert.AreEqual(3, launcher.Processes.Count);
        Assert.IsTrue(launcher.Processes.All(process => process.Killed && process.HasExited));
    }

    [TestMethod]
    public async Task V0014YamlAndArgvContainNoSecretAndQuotePathsWithSpaces()
    {
        using var fixture = new ProviderFixture(pathsWithSpaces: true);
        var launcher = new FakeLauncher();
        await using var host = new TunnelClientProcessHost(launcher);
        await host.StartAsync(fixture.Settings,
            new McpConnectionMaterial("flamoris-pipe-one", CapabilityOne), ApiKey,
            CancellationToken.None);

        string yaml = File.ReadAllText(fixture.Settings.ProfilePath);
        StringAssert.Contains(yaml, "config_version: 1");
        StringAssert.Contains(yaml, "api_key: \"env:CONTROL_PLANE_API_KEY\"");
        StringAssert.Contains(yaml, "listen_addr: \"127.0.0.1:8080\"");
        StringAssert.Contains(yaml, "mcp:");
        StringAssert.Contains(yaml, "Flamoris Mcp Bridge.exe");
        StringAssert.Contains(yaml, "--pipe");
        Assert.IsFalse(yaml.Contains(CapabilityOne, StringComparison.Ordinal));
        Assert.IsFalse(yaml.Contains(ApiKey, StringComparison.Ordinal));

        CapturedStart start = launcher.Starts.Single();
        Assert.AreEqual(fixture.Settings.TunnelClientExecutable, start.FileName);
        Assert.IsFalse(start.UseShellExecute);
        CollectionAssert.AreEqual(new[] { "run", "--profile", "flamoris-cutwork" }, start.Arguments);
        Assert.IsFalse(start.Arguments.Any(argument =>
            argument.Contains(CapabilityOne, StringComparison.Ordinal)
            || argument.Contains(ApiKey, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PreferencesPersistOnlyStableNonSecretValues()
    {
        using var fixture = new ProviderFixture();
        string path = System.IO.Path.Combine(fixture.Root, "settings", "mcp-connection.json");
        var store = new McpConnectionPreferencesStore(path);
        var preferences = new McpConnectionPreferences
        {
            Method = McpConnectionMethod.OpenAiTunnelClient,
            AutoStart = true,
            TunnelClientExecutable = fixture.Settings.TunnelClientExecutable,
            ProfileDirectory = fixture.Settings.ProfileDirectory,
            ProfileName = fixture.Settings.ProfileName,
            TunnelId = fixture.Settings.TunnelId,
            HealthListenAddress = fixture.Settings.HealthListenAddress,
        };

        store.Save(preferences);
        string json = File.ReadAllText(path);

        Assert.AreEqual(preferences, store.Load());
        Assert.IsFalse(json.Contains(CapabilityOne, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("FLAMORIS_MCP_CAPABILITY", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("CONTROL_PLANE_API_KEY", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MaterialDiagnosticsAreAlwaysRedacted()
    {
        var material = new McpConnectionMaterial("flamoris-pipe-one", CapabilityOne);
        Assert.AreEqual("McpConnectionMaterial [redacted]", material.ToString());
        Assert.IsFalse(material.ToString().Contains(CapabilityOne, StringComparison.Ordinal));
    }

    private sealed class CountingProvider(List<string>? order = null) : IMcpConnectionProvider
    {
        public string Id => "counting";
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            order?.Add("start");
            return ValueTask.CompletedTask;
        }
        public ValueTask RefreshAsync(CancellationToken cancellationToken)
        {
            order?.Add("refresh");
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            order?.Add("stop");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableMaterialSource(McpConnectionMaterial current)
        : IMcpConnectionMaterialSource
    {
        public McpConnectionMaterial Current { get; set; } = current;
        public McpConnectionMaterial GetCurrent() => Current;
    }

    private sealed class FixedCredentialSource : IProviderCredentialSource
    {
        public ValueTask<string> GetControlPlaneApiKeyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApiKey);
    }

    private sealed record CapturedStart(string FileName, bool UseShellExecute,
        string[] Arguments, Dictionary<string, string?> Environment);

    private sealed class FakeLauncher : IOwnedProcessLauncher
    {
        public bool FailStart { get; init; }
        public FakeProcess? Unrelated { get; init; }
        public List<CapturedStart> Starts { get; } = [];
        public List<FakeProcess> Processes { get; } = [];

        public IOwnedProcess Start(ProcessStartInfo startInfo)
        {
            if (FailStart) throw new InvalidOperationException("unsafe provider detail " + ApiKey);
            Starts.Add(new(startInfo.FileName, startInfo.UseShellExecute,
                startInfo.ArgumentList.ToArray(), startInfo.Environment.ToDictionary(
                    item => item.Key, item => item.Value, StringComparer.Ordinal)));
            var process = new FakeProcess();
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : IOwnedProcess
    {
        public bool HasExited { get; private set; }
        public bool Killed { get; private set; }
        public event Action? Exited;
        public void Kill(bool entireProcessTree)
        {
            Assert.IsTrue(entireProcessTree);
            Killed = true;
            HasExited = true;
            Exited?.Invoke();
        }
        public void ExitUnexpectedly()
        {
            HasExited = true;
            Exited?.Invoke();
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class ProviderFixture : IDisposable
    {
        public ProviderFixture(bool pathsWithSpaces = false)
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "cutwork-managed-" + Guid.NewGuid().ToString("N"),
                pathsWithSpaces ? "folder with spaces" : "fixture");
            string tunnelClient = System.IO.Path.Combine(Root, "bin", "tunnel client.exe");
            string bridge = System.IO.Path.Combine(Root, "app", "mcp", "Flamoris Mcp Bridge.exe");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tunnelClient)!);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bridge)!);
            File.WriteAllText(tunnelClient, "fixture");
            File.WriteAllText(bridge, "fixture");
            Settings = new OpenAiTunnelClientSettings
            {
                TunnelClientExecutable = tunnelClient,
                ProfileDirectory = System.IO.Path.Combine(Root, "profile directory"),
                ProfileName = "flamoris-cutwork",
                TunnelId = "flamoris-cutwork",
                BridgeExecutable = bridge,
            };
        }

        public string Root { get; }
        public OpenAiTunnelClientSettings Settings { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
