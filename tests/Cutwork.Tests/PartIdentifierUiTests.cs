using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Flamoris.Cutwork.App;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PartIdentifierUiTests
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    private static Dispatcher _dispatcher = null!;
    private static Thread _uiThread = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiThread = new Thread(() =>
        {
            Application application;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/Cutwork;component/Resources/ToolIcons.xaml", UriKind.Relative),
                });
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.SetResult();
            }
            catch (Exception exception) { ready.SetException(exception); return; }
            // Application.Shutdown stops only the dispatcher started by Application.Run.
            // This plain Application has no production startup hooks or StartupUri.
            application.Run();
        }) { IsBackground = true };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        ready.Task.WaitAsync(UiTimeout).GetAwaiter().GetResult();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        if (!_uiThread.IsAlive) return;
        try { RunSta(() => Application.Current?.Shutdown()); }
        finally
        {
            if (!_uiThread.Join(UiTimeout))
                throw new TimeoutException("The Part identifier test UI did not shut down within 30 seconds.");
        }
    }

    [TestMethod]
    public void PresetSurvivesSessionRefreshAndApplyUsesSharedHistoryAndPersistence()
    {
        RunSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var session = Session(window);
                var part = EditHistoryTests.Part();
                session.Open(new CutworkDocument(FlimgRoundTripTests.Original(4, 3)));
                session.Execute(new AddLayer(part));
                session.Open(session.Document!);
                session.SelectLayer(part.Id);
                var maskTool = Field<MaskBrushController>(window, "_maskTool");
                Field<CanvasInputRouter>(window, "_inputRouter").SetActiveTool(maskTool);
                var editor = (ComboBox)window.FindName("SemanticNameEditor");
                var apply = (Button)window.FindName("SemanticNameApplyButton");

                editor.SelectedItem = "eye_left";
                Assert.AreEqual("eye_left", editor.Text);
                Assert.IsNull(part.SemanticName);
                Assert.IsFalse(session.IsDirty);

                // These real session notifications formerly rewrote the pending preset.
                session.SelectLayer(part.Id);
                session.SetPreviewSource(PreviewSource.Original);
                maskTool.PointerMove(new DocumentPoint(1, 1), CanvasModifiers.None);
                Assert.AreEqual("eye_left", editor.Text);
                apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.AreEqual("eye_left", part.SemanticName);
                Assert.AreEqual("eye_left", editor.Text);
                Assert.AreEqual(part.Id, session.SelectedLayerId);
                Assert.AreEqual(1, session.UndoCount);
                Assert.IsTrue(session.IsDirty);
                var saved = FlimgRoundTripTests.Read(FlimgRoundTripTests.Write(session.Document!));
                Assert.AreEqual("eye_left", saved.GetLayer(part.Id).SemanticName);

                session.Undo();
                Assert.IsNull(part.SemanticName);
                Assert.AreEqual("", editor.Text);
                session.Redo();
                Assert.AreEqual("eye_left", part.SemanticName);
                Assert.AreEqual("eye_left", editor.Text);
            }
            finally { CloseWithoutPrompt(window); }
        });
    }

    [TestMethod]
    public void CustomDraftTracksSelectedPartAndApplyNormalizesEvenANoOp()
    {
        RunSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var session = Session(window);
                var first = EditHistoryTests.Part();
                var second = EditHistoryTests.Part(1);
                session.Open(new CutworkDocument(FlimgRoundTripTests.Original(4, 3)));
                session.Execute(new AddLayer(first), new AddLayer(second),
                    new SetLayerSemanticName(second.Id, "eye_right"));
                session.Open(session.Document!);
                session.SelectLayer(first.Id);
                var editor = (ComboBox)window.FindName("SemanticNameEditor");
                var apply = (Button)window.FindName("SemanticNameApplyButton");

                editor.Text = "  costume_ribbon_07  ";
                session.MarkSaved();
                Assert.AreEqual("  costume_ribbon_07  ", editor.Text);
                apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.AreEqual("costume_ribbon_07", first.SemanticName);
                Assert.AreEqual("costume_ribbon_07", editor.Text);

                editor.Text = "unapplied";
                session.SelectLayer(second.Id);
                Assert.AreEqual("eye_right", editor.Text);
                editor.Text = "  eye_right  ";
                var undoCount = session.UndoCount;
                apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.AreEqual("eye_right", second.SemanticName);
                Assert.AreEqual("eye_right", editor.Text);
                Assert.AreEqual(undoCount, session.UndoCount);

                editor.Text = "  ";
                apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.IsNull(second.SemanticName);
                Assert.AreEqual("", editor.Text);
                Assert.AreEqual("costume_ribbon_07", first.SemanticName);

                editor.Text = "unsaved_draft";
                session.Open(session.Document!);
                session.SelectLayer(second.Id);
                Assert.AreEqual("", editor.Text);

                session.SelectLayer(session.Document!.Base.Id);
                Assert.IsFalse(editor.IsEnabled);
                Assert.IsFalse(apply.IsEnabled);
            }
            finally { CloseWithoutPrompt(window); }
        });
    }

    private static EditorSession Session(MainWindow window) => Field<EditorSession>(window, "_session");

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void CloseWithoutPrompt(MainWindow window)
    {
        var session = Session(window);
        if (session.Document is not null) session.MarkSaved();
        window.Close();
    }

    private static void RunSta(Action action)
        => _dispatcher.InvokeAsync(action).Task.WaitAsync(UiTimeout).GetAwaiter().GetResult();
}
