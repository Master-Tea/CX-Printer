using Microsoft.UI.Xaml;

namespace CX_Printer;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        InitializeComponent();
        UnhandledException+=(_,args)=>
        {
            try
            {
                var directory=Environment.GetEnvironmentVariable("CXPRINTER_DEBUG_SERVICE")=="1"
                    ? Path.Combine(AppContext.BaseDirectory,"DebugUI")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CX Printer","Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory,"ui-error.log"),$"{DateTimeOffset.Now:O} {args.Exception}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window=new MainWindow(); _window.Activate();
    }
}
