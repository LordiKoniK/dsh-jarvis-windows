using System.Windows.Forms;
using JarvisPanel.App;
using JarvisPanel.Core;
using JarvisPanel.UI;

namespace JarvisPanel;

/// <summary>
/// Entry point for the Windows panel: one instance, one orb, one message loop.
/// </summary>
/// <remarks>
/// The process is spawned by the host plugin and has no console, so every failure
/// path here ends in <see cref="Log"/> and a live window rather than an exit code
/// nobody can see. The only reasons to leave are the user quitting and the second
/// instance of a single-instance guard.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Per-session, not global: two users on the same machine may each run their
    /// own panel, but one user may not run two orbs.
    /// </summary>
    private const string InstanceMutex = @"Local\jarvis-panel-single-instance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Headless placement check; see PlacementSelfTest. No window is created.
        if (args.Length > 0 && args[0] == "--selftest") return PlacementSelfTest.Run();

        // Before anything visual: a second instance must not flash an orb, and it
        // must not touch panel.json either.
        using var single = new Mutex(initiallyOwned: true, InstanceMutex, out var isFirst);
        if (!isFirst) return 0;

        try
        {
            ApplicationConfiguration.Initialize();
        }
        catch (Exception ex)
        {
            Log.Write("high-DPI setup failed: " + Log.Describe(ex));
        }

        // Keeps every await in the state loop and the quick bar on the UI thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var context = new PanelContext();
        try
        {
            context.Start();
            try
            {
                Application.Run(context);
            }
            catch (Exception ex)
            {
                // Only reached if the message pump itself dies; anything raised by
                // an event handler is reported through Application.ThreadException
                // and the panel carries on.
                Log.Write("message loop ended: " + Log.Describe(ex));
            }
        }
        catch (Exception ex)
        {
            Log.Write("startup failed: " + Log.Describe(ex));
            return 1;
        }
        finally
        {
            context.Shutdown();
            Log.Write("panel exited");
        }
        return 0;
    }
}

/// <summary>
/// Owns everything the panel keeps alive: the settings store, the model, the
/// state loop, the global hotkey, the orb, the tray icon and the quick bar.
/// </summary>
internal sealed class PanelContext : ApplicationContext
{
    private readonly SettingsStore _store = new();
    private readonly PanelModel _model;
    private readonly UiActions _actions;
    private readonly StateLoop _loop;

    private OrbWindow? _orb;
    private TrayIcon? _tray;
    private HotKeySpec _hotKey;
    private bool _hotKeyRegistered;
    private bool _shuttingDown;

    public PanelContext()
    {
        _model = new PanelModel(new JarvisClient(), _store);
        _loop = new StateLoop(_model);
        _actions = new UiActions(_model, _store);
    }

    protected override void OnMainFormClosed(object? sender, EventArgs e)
    {
        // There is no main form; the tray icon is the lifetime. Nothing should be
        // able to reach this, but exiting quietly beats leaving a hidden process.
        ExitThread();
    }

    public void Start()
    {
        Application.ThreadException += (_, e) => Log.Write("UI exception: " + Log.Describe(e.Exception));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("fatal exception: " + Log.Describe(e.ExceptionObject as Exception ?? new Exception("unknown")));

        _orb = new OrbWindow(_model, _store);
        // Attach() owns the orb's event subscriptions, including QuickBarRequested.
        // Do not subscribe it again here: ToggleQuickBar is a true toggle, so a
        // second handler made every click open and immediately close the bar —
        // which reads as "clicking the orb does nothing" (and earlier, as a flash).
        _actions.Attach(_orb);
        _orb.SetMenuActions(_actions.ResetPosition, _actions.Quit);
        _orb.HotKeyPressed += _actions.HotKeyPressed;

        // The orb is a layered window with no taskbar button, so it has to be shown
        // explicitly once its handle exists.
        MainForm = _orb;
        _orb.Show();
        // The constructor restores a position before anything is listening, so the
        // spot the orb settled on is saved here instead.
        _orb.SaveSettledPosition();

        _tray = new TrayIcon(_actions);
        _actions.Attach(_tray);
        _actions.QuitRequested += Quit;

        _model.Changed += _actions.ModelChanged;
        _loop.Start();
        RegisterHotKey();

        Log.Write($"panel started (pid {Environment.ProcessId}, " +
                  $"hotkey {_hotKey.DisplayString}{(_hotKeyRegistered ? "" : " refused")})");
    }

    /// <summary>
    /// Registers the panel's shortcut on the orb's handle. A refused shortcut is
    /// reported and survived: another app owning Ctrl+Alt+J is not a reason to
    /// leave the user without a panel.
    /// </summary>
    private void RegisterHotKey()
    {
        _hotKey = HotKeySpec.TryParse(_store.Settings.HotKey) ?? HotKeySpec.Default;
        if (_orb is null) return;

        _hotKeyRegistered = _orb.RegisterHotKey(UiActions.HotKeyId, _hotKey);
        if (_hotKeyRegistered) return;
        Log.Write($"hotkey {_hotKey.DisplayString} is already taken");
    }

    public void Quit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        ExitThread();
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _loop.Dispose();
        _tray?.Dispose();
        _actions.Dispose();
        _orb?.Dispose();
    }
}
