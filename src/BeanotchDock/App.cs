using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace BeanotchDock;

// The whole UI is built in code, as in the island: the app starts here rather than from App.xaml, and hands WinUI the
// controls library's own type info so the standard controls find their styles.
public sealed partial class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider controls = new();
    private DockWindow? window;

    private static Mutex? single;

    [STAThread]
    private static void Main()
    {
        // One dock at a time.
        single = new Mutex(true, @"Local\Beanotch.Dock", out var first);
        if (!first) return;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }

    public App()
    {
        XamlControlsXamlMetaDataProvider.Initialize();
        UnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
    }

    // Errors go to %LOCALAPPDATA%\BeanotchDock\crash.log (and next to snapshots), so a report can include them.
    public static void Log(Exception? error)
    {
        if (error is null) return;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}{Environment.NewLine}";
        foreach (var folder in new[] { DockSettings.Folder, DockWindow.SnapshotFolder })
        {
            if (folder is null) continue;
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"), line);
            }
            catch (Exception) { }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        window = new DockWindow();
        window.Activate();
    }

    public IXamlType GetXamlType(Type type) => controls.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => controls.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => controls.GetXmlnsDefinitions();
}
