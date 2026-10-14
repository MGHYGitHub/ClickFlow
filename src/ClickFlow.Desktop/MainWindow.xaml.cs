using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClickFlow.Core;
using ClickFlow.Desktop.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClickFlow.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ActionStep> _steps = [];
    private readonly ObservableCollection<ActionStep> _recording = [];
    private readonly IInputDriver _input;
    private readonly AutomationEngine _engine;
    private readonly DispatcherTimer _cursorTimer = new() { Interval=TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _saveTimer = new() { Interval=TimeSpan.FromMilliseconds(900) };
    private HotkeyService? _hotkeys;
    private RecorderService? _recorder;
    private Forms.NotifyIcon? _tray;
    private Task? _run;
    private string _mode = "Quick", _executionMode = "Quick";
    private bool _ready, _loading, _closing, _allowClose, _loadFailed, _recordUiActive;
    private string? _lastSaveError;
    private RunProgress? _lastProgress;
    private EngineState _lastState = EngineState.Idle;
    private string ProfilePath => Path.Combine(App.DataDirectory,"workflow.json");

    public MainWindow()
    {
        InitializeComponent();
        InitializeUpdateUi();
        if(App.PreviewMode)Title+=" · 空跑预览";
        _input = App.PreviewMode ? new PreviewDriver() : new InputDriver();
        _engine = new AutomationEngine(_input);
        _engine.Progress += p => Dispatcher.BeginInvoke(() => ShowProgress(p));
        StepsGrid.ItemsSource=_steps;RecordingGrid.ItemsSource=_recording;
        StepsGrid.IsReadOnly=true; StepsGrid.MouseDoubleClick+=(_,_)=>EditStep_Click(this,new());
        RecordingGrid.MouseDoubleClick+=(_,_)=>EditRecording_Click(this,new());
        _steps.CollectionChanged+=(_,_)=>QueueSave();_recording.CollectionChanged+=(_,_)=>QueueSave();
        AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>QueueSave()));
        AddHandler(ComboBox.SelectionChangedEvent,new SelectionChangedEventHandler((_,_)=>QueueSave()));
        AddHandler(CheckBox.CheckedEvent,new RoutedEventHandler((_,_)=>QueueSave()));
        AddHandler(CheckBox.UncheckedEvent,new RoutedEventHandler((_,_)=>QueueSave()));
        _saveTimer.Tick+=(_,_)=>{ _saveTimer.Stop();SaveProfile(false); };
        _cursorTimer.Tick+=(_,_)=> { var p=_input.GetCursorPosition();CursorText.Text=$"X {p.X}   Y {p.Y}"; if(_recorder?.IsRecording==true)RecordStatus.Text=$"正在录制 · {_recording.Count:N0} 个动作 · F7 结束"; };
        SourceInitialized+=(_,_)=>InitializeServices();
        Loaded+=(_,_)=> { _ready=true;LoadProfile();SetPage("Quick");_cursorTimer.Start();UpdateButtons();Log(App.PreviewMode?"预览模式：所有执行均为空跑，不发送鼠标或键盘输入。":"ClickFlow 已就绪。F2 捕获坐标，F3 开始，F6 停止。"); };
        Closing+=OnClosing;
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape&&_engine.IsActive){_engine.Stop();e.Handled=true;} };
    }
    private void InitializeServices()
    {
        if(!App.PreviewMode)
        {
            _hotkeys=new HotkeyService(this);_hotkeys.Pressed+=OnHotkey;
            foreach(var failure in _hotkeys.Failures)Log(failure);
            _recorder=new RecorderService();
            _recorder.Captured+=s=>_recording.Add(s);
            _recorder.Error+=m=>{ StopRecording();ShowError(m); };
        }
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("打开 ClickFlow",null,(_,_)=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();}));
        menu.Items.Add("暂停 / 继续 · F4",null,(_,_)=>Dispatcher.Invoke(()=>_engine.TogglePause()));
        menu.Items.Add("停止 · F6",null,(_,_)=>Dispatcher.Invoke(()=>{_engine.Stop();StopRecording();}));
        menu.Items.Add("收起到托盘",null,(_,_)=>Dispatcher.Invoke(Hide));
        menu.Items.Add("退出",null,(_,_)=>Dispatcher.Invoke(Close));
        using var iconStream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ClickFlow.ico"))!.Stream;
        _tray=new Forms.NotifyIcon { Text="ClickFlow · 自动点击器",Icon=new System.Drawing.Icon(iconStream),Visible=true,ContextMenuStrip=menu };
        _tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();});
    }
    private void OnHotkey(int key)
    {
        if(_closing)return;
        if(OwnedWindows.Count>0 && key is not 117)return;
        switch(key)
        {
            case 113: CapturePoint();break;
            case 114: if(_engine.IsActive)_engine.Stop();else Start_Click(this,new());break;
            case 115: _engine.TogglePause();break;
            case 117: _engine.Stop();StopRecording();break;
            case 118: Record_Click(this,new());break;
        }
    }
    private void Nav_Checked(object sender,RoutedEventArgs e)
    {
        if(!_ready)return;
        if(sender is RadioButton b && b.Tag is string tag)SetPage(tag);
    }
    private void SetPage(string page)
    {
        _mode=page;if(page is "Quick" or "Flow" or "Record")_executionMode=page;
        StartButton.Content=_executionMode switch {"Flow"=>"运行流程 · F3","Record"=>"回放 · F3",_=>"开始 · F3"};
        QuickPage.Visibility=page=="Quick"?Visibility.Visible:Visibility.Collapsed;
        FlowPage.Visibility=page=="Flow"?Visibility.Visible:Visibility.Collapsed;
        RecordPage.Visibility=page=="Record"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility=page=="Settings"?Visibility.Visible:Visibility.Collapsed;
        LogsPage.Visibility=page=="Logs"?Visibility.Visible:Visibility.Collapsed;
        (PageTitle.Text,PageSubtitle.Text)=page switch {
            "Flow"=>("步骤流程","把多个动作串起来，一次完成整套操作。"),
            "Record"=>("操作录制","操作一次，让 ClickFlow 记住你的步骤。"),
            "Settings"=>("运行设置","节奏、循环与保护，由你掌控。"),
            "Logs"=>("运行记录","每一次运行，都有迹可循。"),
            _=>("快速连点","设好节奏，把重复点击交给 ClickFlow。")};
    }
    private bool CanEdit() { if(_engine.IsActive || _recorder?.IsRecording==true){StatusText.Text="请先停止运行或录制，再修改步骤。";return false;}return true; }
    private static int ReadInt(TextBox field,string name,int min,int max)
    {
        if(!int.TryParse(field.Text,out var n)||n<min||n>max)throw new FormatException($"{name}必须是 {min:N0}–{max:N0} 之间的整数。");return n;
    }
    private static int ReadSeconds(TextBox field,string name,int min,int max)
    {
        if(!double.TryParse(field.Text,out var seconds)||seconds<0||seconds>max/1000d)throw new FormatException($"{name}必须是 0–{max/1000d:N3} 秒之间的数字。");
        return (int)Math.Round(seconds*1000);
    }
    private WorkflowDocument ReadDocument() => new() {
        Name=string.IsNullOrWhiteSpace(WorkflowName.Text)?"我的工作流":WorkflowName.Text.Trim(),Theme=DarkTheme.IsChecked==true?"Dark":"Light",
        Steps=_steps.Select(x=>x.Clone()).ToList(),Recording=_recording.Select(x=>x.Clone()).ToList(),
        Quick=new QuickOptions { IntervalMs=ReadSeconds(QuickInterval,"点击间隔",0,PlanValidator.MaxDelayMs),Count=ReadInt(QuickCount,"点击次数",0,1000000),X=ReadInt(QuickX,"X 坐标",-1000000,1000000),Y=ReadInt(QuickY,"Y 坐标",-1000000,1000000),Button=(PointerButton)QuickButton.SelectedIndex,UseCurrentPosition=QuickCurrent.IsChecked==true,DoubleClick=QuickDouble.IsChecked==true },
        Options=new RunOptions {RepeatCount=ReadInt(RepeatCount,"循环次数",0,1000000),StartDelayMs=ReadSeconds(StartDelay,"启动延迟",0,PlanValidator.MaxDelayMs),CycleDelayMs=ReadSeconds(CycleDelay,"循环间隔",0,PlanValidator.MaxDelayMs),RandomOffset=ReadInt(RandomOffset,"坐标随机偏移",0,1000),RandomDelayMs=ReadSeconds(RandomDelay,"随机延时",0,60000),CornerFailSafe=CornerFailSafe.IsChecked==true}
    };
    private void ApplyDocument(WorkflowDocument d)
    {
        _loading=true;
        try {
            WorkflowName.Text=d.Name;_steps.Clear();foreach(var s in d.Steps)_steps.Add(s);_recording.Clear();foreach(var s in d.Recording)_recording.Add(s);
            QuickInterval.Text=(d.Quick.IntervalMs/1000d).ToString("0.###");QuickCount.Text=d.Quick.Count.ToString();QuickX.Text=d.Quick.X.ToString();QuickY.Text=d.Quick.Y.ToString();QuickCurrent.IsChecked=d.Quick.UseCurrentPosition;QuickDouble.IsChecked=d.Quick.DoubleClick;QuickButton.SelectedIndex=(int)d.Quick.Button;
            RepeatCount.Text=d.Options.RepeatCount.ToString();StartDelay.Text=(d.Options.StartDelayMs/1000d).ToString("0.###");CycleDelay.Text=(d.Options.CycleDelayMs/1000d).ToString("0.###");RandomOffset.Text=d.Options.RandomOffset.ToString();RandomDelay.Text=(d.Options.RandomDelayMs/1000d).ToString("0.###");CornerFailSafe.IsChecked=d.Options.CornerFailSafe;
            DarkTheme.IsChecked=d.Theme=="Dark";ApplyTheme();RecordStatus.Text=$"已保存 {_recording.Count:N0} 个动作";
        } finally {_loading=false;}
    }
    private void LoadProfile()
    {
        if(!File.Exists(ProfilePath)){ApplyDocument(new());return;}
        try {ApplyDocument(WorkflowStore.Load(ProfilePath));}
        catch(Exception ex) {_loadFailed=true;ApplyDocument(new());Log("原配置读取失败，已保留原文件并停用自动覆盖："+ex.Message);StatusText.Text="配置读取失败，可导入备份或另行导出。";}
    }
    private void QueueSave() { if(!_ready||_loading||_closing||_recorder?.IsRecording==true)return;_saveTimer.Stop();_saveTimer.Start(); }
    private bool SaveProfile(bool report)
    {
        if(!_ready||_loadFailed)return false;
        try {WorkflowStore.Save(ProfilePath,ReadDocument());_lastSaveError=null;if(report){StatusText.Text="工作流已保存";Log("配置已保存。");}return true;}
        catch(Exception ex){if(report)ShowError(ex.Message);else if(_lastSaveError!=ex.Message){_lastSaveError=ex.Message;StatusText.Text="当前修改尚未保存，请检查数值。";Log("自动保存未完成："+ex.Message);}return false;}
    }
    private void Save_Click(object sender,RoutedEventArgs e) { if(!CanEdit())return;if(_loadFailed){Export_Click(sender,e);return;}SaveProfile(true); }
    private void Import_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEdit())return;
        var dialog=new OpenFileDialog {Title="导入工作流或旧版配置",Filter="支持的配置|*.json;*.properties|JSON 工作流|*.json|Java 旧版配置|*.properties"};if(dialog.ShowDialog(this)!=true)return;
        try {
            WorkflowDocument d;
            if(new FileInfo(dialog.FileName).Length>16*1024*1024)throw new InvalidDataException("配置文件超过 16 MB，无法导入。");
            if(Path.GetExtension(dialog.FileName).Equals(".properties",StringComparison.OrdinalIgnoreCase))d=LegacyImporter.Import(dialog.FileName);
            else {using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(dialog.FileName));d=json.RootElement.ValueKind==System.Text.Json.JsonValueKind.Object&&json.RootElement.TryGetProperty("schemaVersion",out _)?WorkflowStore.Load(dialog.FileName):LegacyImporter.Import(dialog.FileName);}
            ApplyDocument(d);if(!_loadFailed)SaveProfile(false);Log($"已导入 {Path.GetFileName(dialog.FileName)}，{d.Steps.Count} 个步骤、{d.Recording.Count} 个录制动作。");StatusText.Text="导入完成，请检查步骤和时间设置后运行。";
        } catch(Exception ex){ShowError("导入失败："+ex.Message);}
    }
    private void Export_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEdit())return;
        var dialog=new SaveFileDialog {Title="导出工作流",FileName="ClickFlow-workflow.json",DefaultExt=".json",Filter="ClickFlow 工作流|*.json"};if(dialog.ShowDialog(this)!=true)return;
        try {WorkflowStore.Save(dialog.FileName,ReadDocument());Log("工作流已导出："+dialog.FileName);StatusText.Text="工作流已导出";}catch(Exception ex){ShowError(ex.Message);}
    }
    private async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(_closing||_updateBusy||_engine.IsActive||_recorder?.IsRecording==true)return;
        try {
            var d=ReadDocument();var options=d.Options.Clone();IReadOnlyList<ActionStep> steps;
            if(_executionMode=="Quick"){
                options.RepeatCount=d.Quick.Count;options.CycleDelayMs=Math.Max(10,d.Quick.IntervalMs);
                steps=[new(){Name="快速连点",Kind=d.Quick.DoubleClick?ActionKind.DoubleClick:ActionKind.Click,Button=d.Quick.Button,UseCurrentPosition=d.Quick.UseCurrentPosition,X=d.Quick.X,Y=d.Quick.Y,DelayMs=0,IntervalMs=0,Count=1}];
            } else { steps=_executionMode=="Record"?d.Recording:d.Steps;if(_executionMode=="Record"){options.RandomOffset=0;options.RandomDelayMs=0;} }
            var plan=new ExecutionPlan(steps,options);var error=PlanValidator.Validate(plan);if(error!=null)throw new FormatException(error);
            SaveProfile(false);Log($"开始{(_executionMode=="Quick"?"快速连点":_executionMode=="Flow"?"步骤流程":"录制回放")} · {(options.RepeatCount==0?"持续运行":options.RepeatCount+" 次循环")}");
            _run=_engine.RunAsync(plan);UpdateButtons();await _run;
        }catch(Exception ex){ShowError(ex.Message);}finally{UpdateButtons();}
    }
    private void Pause_Click(object sender,RoutedEventArgs e) { _engine.TogglePause();UpdateButtons(); }
    private void Stop_Click(object sender,RoutedEventArgs e) { _engine.Stop();StopRecording(); }
    private void ShowProgress(RunProgress p)
    {
        if(_allowClose)return;_lastProgress=p;StatusText.Text=p.Message;
        ActionCountText.Text=$"已完成 {p.ActionsCompleted:N0}";ElapsedText.Text=p.Elapsed.ToString(@"hh\:mm\:ss");DetailText.Text=p.State is EngineState.Completed or EngineState.Stopped or EngineState.Faulted?$"本次执行结束 · 第 {p.Cycle:N0} 轮":$"第 {p.Cycle:N0} 轮 · 步骤 {Math.Max(0,p.StepIndex+1)}";
        if(_lastState!=p.State){Log(p.Message);_lastState=p.State;}
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        if(!_ready)return;bool running=_engine.IsActive,recording=_recorder?.IsRecording==true;
        StartButton.IsEnabled=!running&&!recording&&!_updateBusy;PauseButton.IsEnabled=running;PauseButton.Content=_engine.IsPaused?"继续 · F4":"暂停 · F4";StopButton.IsEnabled=running||recording;RecordButton.IsEnabled=!running&&!_updateBusy;
        StepsGrid.IsEnabled=RecordingGrid.IsEnabled=!running&&!recording;
    }
    private void CapturePoint()
    {
        if(_executionMode=="Record"){StatusText.Text="请切换到步骤流程，按 F2 添加点位。";return;}
        if(!CanEdit())return;var p=_input.GetCursorPosition();
        if(_executionMode=="Quick") {QuickX.Text=p.X.ToString();QuickY.Text=p.Y.ToString();QuickCurrent.IsChecked=false;StatusText.Text=$"已捕获坐标 ({p.X}, {p.Y})";}
        else {_steps.Add(new(){Name=$"点位 {_steps.Count+1}",X=p.X,Y=p.Y});StepsGrid.SelectedIndex=_steps.Count-1;StatusText.Text=$"已添加点位 ({p.X}, {p.Y})";}
        QueueSave();
    }
    private void CaptureQuick_Click(object sender,RoutedEventArgs e) {StatusText.Text="将鼠标移到目标位置，然后按 F2 捕获坐标。";}
    private void AddStep_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEdit())return;var p=_input.GetCursorPosition();var editor=new StepEditor(this,new(){Name=$"步骤 {_steps.Count+1}",X=p.X,Y=p.Y});if(editor.ShowDialog()==true){_steps.Add(editor.Result);StepsGrid.SelectedIndex=_steps.Count-1;}
    }
    private void EditStep_Click(object sender,RoutedEventArgs e) => EditSelected(StepsGrid,_steps);
    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit()) return;
        var steps = StepsGrid.SelectedItems.OfType<ActionStep>().ToList();
        if (steps.Count == 0) steps = _steps.ToList();
        new GuideOverlayWindow(this, steps).ShowDialog();
    }
    private void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit()) return;
        var selected = StepsGrid.SelectedItems.OfType<ActionStep>().ToList();
        if (selected.Count == 0) { StatusText.Text = "请先在步骤列表中多选至少一个步骤。"; return; }
        var editor = new BatchStepEditor(this, selected.Count);
        if (editor.ShowDialog() != true) return;
        foreach (var step in selected) { if (editor.DelayMs is int delay) step.DelayMs = delay; if (editor.Count is int count) step.Count = count; if (editor.IntervalMs is int interval) step.IntervalMs = interval; }
        StepsGrid.Items.Refresh(); QueueSave(); StatusText.Text = $"已批量更新 {selected.Count} 个步骤。"; Log(StatusText.Text);
    }
    private void EditRecording_Click(object sender,RoutedEventArgs e) => EditSelected(RecordingGrid,_recording);
    private void EditSelected(DataGrid grid,ObservableCollection<ActionStep> list)
    {
        if(!CanEdit()||grid.SelectedItem is not ActionStep step)return;var editor=new StepEditor(this,step);int index=list.IndexOf(step);if(editor.ShowDialog()==true){list[index]=editor.Result;grid.SelectedIndex=index;QueueSave();}
    }
    private void DuplicateStep_Click(object sender,RoutedEventArgs e) {if(!CanEdit()||StepsGrid.SelectedItem is not ActionStep s)return;var clone=s.Clone();clone.Name+=" 副本";_steps.Insert(_steps.IndexOf(s)+1,clone);StepsGrid.SelectedItem=clone;}
    private void RemoveStep_Click(object sender,RoutedEventArgs e) {if(CanEdit()&&StepsGrid.SelectedItem is ActionStep s)_steps.Remove(s);}
    private void RemoveRecording_Click(object sender,RoutedEventArgs e) {if(CanEdit()&&RecordingGrid.SelectedItem is ActionStep s)_recording.Remove(s);}
    private void MoveUp_Click(object sender,RoutedEventArgs e)=>Move(-1);
    private void MoveDown_Click(object sender,RoutedEventArgs e)=>Move(1);
    private void Move(int direction){if(!CanEdit())return;int i=StepsGrid.SelectedIndex,j=i+direction;if(i>=0&&j>=0&&j<_steps.Count){_steps.Move(i,j);StepsGrid.SelectedIndex=j;}}
    private void Record_Click(object sender,RoutedEventArgs e)
    {
        if(_closing||_updateBusy||_engine.IsActive)return;
        if(_recorder?.IsRecording==true){StopRecording();return;}
        if(App.PreviewMode){StatusText.Text="预览模式不监听或录制真实输入。";return;}
        if(_recording.Count>0&&MessageBox.Show(this,"开始新录制会替换当前录制。是否继续？","开始录制",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try{_saveTimer.Stop();_recorder!.Start();_recording.Clear();_recordUiActive=true;RecordButton.Content="结束录制 · F7";RecordStatus.Text="正在录制 · F7 结束";Log("开始录制。仅保存在本机；录制期间请勿输入密码等敏感内容。");UpdateButtons();}
        catch(Exception ex){ShowError(ex.Message);}
    }
    private void StopRecording()
    {
        var hadRecording=_recordUiActive||_recorder?.IsRecording==true;
        if(_recorder?.IsRecording==true)_recorder.Stop();
        _recordUiActive=false;RecordButton.Content="开始录制 · F7";
        if(hadRecording){RecordStatus.Text=$"录制完成 · {_recording.Count:N0} 个动作";StatusText.Text=RecordStatus.Text;Log(RecordStatus.Text);QueueSave();}
        UpdateButtons();
    }
    private void ClearRecording_Click(object sender,RoutedEventArgs e) {if(!CanEdit())return;if(_recording.Count>0&&MessageBox.Show(this,"清空当前录制？","清空录制",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;_recording.Clear();RecordStatus.Text="还没有录制，按 F7 开始。";}
    private void Theme_Click(object sender,RoutedEventArgs e) {ApplyTheme();QueueSave();}
    private void ApplyTheme() {System.Windows.Application.Current.Resources.MergedDictionaries[0]=new ResourceDictionary {Source=new Uri($"Themes/{(DarkTheme.IsChecked==true?"Dark":"Light")}.xaml",UriKind.Relative)};}
    private void Log(string message)
    {
        if(LogText==null)return;LogText.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");if(LogText.Text.Length>100000)LogText.Text=LogText.Text[^80000..];LogText.ScrollToEnd();
    }
    private void ShowError(string message) {StatusText.Text=message;Log("未完成："+message);MessageBox.Show(this,message,"ClickFlow",MessageBoxButton.OK,MessageBoxImage.Information);}
    private void ExportReport_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEdit())return;
        var dialog=new SaveFileDialog {FileName=$"ClickFlow-report-{DateTime.Now:yyyyMMdd-HHmm}.txt",Filter="文本报告|*.txt"};if(dialog.ShowDialog(this)!=true)return;
        try {File.WriteAllText(dialog.FileName,$"ClickFlow {CurrentVersion} 运行报告\n工作流：{WorkflowName.Text}\n导出时间：{DateTimeOffset.Now:O}\n完成动作：{_lastProgress?.ActionsCompleted??0}\n状态：{_lastProgress?.Message??"就绪"}\n\n{LogText.Text}",System.Text.Encoding.UTF8);StatusText.Text="运行报告已导出";}catch(Exception ex){ShowError(ex.Message);}
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_allowClose)return;e.Cancel=true;if(_closing)return;
        _closing=true;_engine.Stop();StopRecording();
        _updateCancellation?.Cancel();
        if(_updateTask!=null){try{await _updateTask;}catch{}}
        if(_run!=null){try{await _run;}catch{}}
        if(!_loadFailed&&!SaveProfile(false)&&MessageBox.Show(this,"当前设置含无效数值或无法保存。退出将保留上次成功保存的配置，是否退出？","退出 ClickFlow",MessageBoxButton.YesNo)!=MessageBoxResult.Yes){_closing=false;return;}
        _saveTimer.Stop();_cursorTimer.Stop();
        if(!_loadFailed)SaveProfile(false);_recorder?.Dispose();_hotkeys?.Dispose();_engine.Dispose();_tray?.Dispose();_updates.Dispose();_allowClose=true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private void InputTest_Click(object sender,RoutedEventArgs e) {if(CanEdit())new InputTestWindow(this).ShowDialog();}
    public void StopForUnexpectedError() { _engine.Stop();StopRecording(); }
    private sealed class PreviewDriver : IInputDriver
    {
        public ScreenPoint GetCursorPosition()=>new(960,540);
        public bool IsAtFailSafeCorner()=>false;
        public void Execute(ActionStep step,int offsetX,int offsetY){}
        public void ReleaseAll(){}
    }
}


