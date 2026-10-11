using System.Windows;
namespace ClickFlow.Desktop;
public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    public static bool PreviewMode { get; private set; }
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickFlow");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smokeTest=e.Args.Contains("--smoke-test");
        PreviewMode = smokeTest || e.Args.Contains("--preview") || Path.GetFileNameWithoutExtension(Environment.ProcessPath??"").EndsWith(".Preview", StringComparison.OrdinalIgnoreCase);
        if(PreviewMode) DataDirectory=Path.Combine(AppContext.BaseDirectory,"preview-data");
        int index = Array.IndexOf(e.Args, "--data-dir");
        if(index >= 0 && index + 1 < e.Args.Length) DataDirectory = Path.GetFullPath(e.Args[index + 1]);
        string instanceName=PreviewMode?"Local\\ClickFlow3.Preview."+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..16]:"Local\\ClickFlow3";
        _instance = new Mutex(true, instanceName, out bool created);
        if (!created) { MessageBox.Show("ClickFlow 已经在运行，请从任务栏或托盘打开。", "ClickFlow"); Shutdown(); return; }
        DispatcherUnhandledException += (_, a) => { try { (MainWindow as MainWindow)?.StopForUnexpectedError(); Directory.CreateDirectory(DataDirectory); File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), $"{DateTimeOffset.Now:O} {a.Exception}\n"); } catch { } MessageBox.Show("操作未完成：" + a.Exception.Message, "ClickFlow"); a.Handled = true; };
        var window=new MainWindow();window.Show();
        if(smokeTest)
        {
            var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(700) };
            timer.Tick+=(_,_)=>{timer.Stop();window.Close();};timer.Start();
        }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
