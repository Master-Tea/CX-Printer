using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using CxPrint.Core;
using CxPrint.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;

namespace CX_Printer;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<PrinterRow> _printers=[];
    private readonly DispatcherTimer _timer=new() { Interval=TimeSpan.FromSeconds(10) };
    private BridgeStatus? _status;
    private bool _rendering=true,_busy,_ready,_formsInitialized;
    private Process? _debugProcess;
    private static bool DebugMode=>Environment.GetEnvironmentVariable("CXPRINTER_DEBUG_SERVICE")=="1";
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1220,920));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.ico"));
        PrintersList.ItemsSource=_printers;
        _timer.Tick+=async (_,_)=> { if (!_busy) await RefreshAsync(false); };
        Closed+=(_,_)=>
        {
            _timer.Stop();
            try { if (_debugProcess is { HasExited:false }) _debugProcess.Kill(entireProcessTree:true); }
            catch (InvalidOperationException) { }
            _debugProcess?.Dispose();
        };
    }
    private async void Root_Loaded(object sender,RoutedEventArgs e)
    {
        if (_ready) return; _ready=true; _rendering=false;
        await Run(async ()=>
        {
            if (DebugMode) await StartDebugService();
            await RefreshAsync(true);
            if (DebugMode) Tell("Visual Studio 调试模式","后台仅监听本机 18631 端口，不安装系统服务。可在 VS 中附加 CxPrint.Service.exe 调试后台断点。",InfoBarSeverity.Informational);
        });
        _timer.Start();
    }
    private void Navigation_SelectionChanged(NavigationView sender,NavigationViewSelectionChangedEventArgs args)
    {
        if (PrintersPage is null || args.SelectedItem is not NavigationViewItem item) return;
        var tag=item.Tag as string;
        PrintersPage.Visibility=tag=="printers" ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility=tag=="account" ? Visibility.Visible : Visibility.Collapsed;
        JobsPage.Visibility=tag=="jobs" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPage.Visibility=tag=="connection" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text=item.Content?.ToString() ?? "CX Printer";
        PageSubtitle.Text=tag switch
        {
            "account"=>"设置 Apple 设备打印时使用的独立账户。",
            "jobs"=>"查看文档接收、处理和提交到 Windows 队列的状态。",
            "connection"=>"让后台服务随系统启动，并在网络恢复后重新发现。",
            _=>"选择 Windows 打印机，供同一局域网中的 Apple 设备使用。"
        };
    }
    private static bool Installed()
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Product.ServiceName);
        return key is not null;
    }
    private async Task RefreshAsync(bool refreshPrinters)
    {
        BridgeStatus? status=null;
        string? connectionError=null;
        try
        {
            var reply=await ControlClient.SendAsync(new("status"));
            if (reply.Success) status=reply.Status; else connectionError=reply.Error;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        { connectionError=ex is UnauthorizedAccessException ? "当前 Windows 账户无权管理此服务，请使用安装服务时的账户。" : null; }
        _status=status;
        _rendering=true;
        try
        {
            var installed=Installed();
            RailStatus.Text=status is null ? installed ? "服务未运行" : "尚未安装服务" : status.Enabled ? "打印服务运行中" : "共享已暂停";
            SharingTitle.Text=status is null ? installed ? "启动后台服务" : "准备好共享打印机" : DebugMode ? "本机调试服务已就绪" : status.Enabled ? "局域网共享已开启" : "局域网共享已暂停";
            SharingDescription.Text=connectionError ?? status?.DiscoveryStatus ?? "安装并启动后台服务后，选择要共享的 Windows 打印机。";
            ServiceButton.Content=status is null ? DebugMode ? "启动调试服务" : installed ? "启动服务" : "安装服务" : "检测连接";
            SharingToggle.IsEnabled=status is not null; SharingToggle.IsOn=status?.Enabled==true;
            AutoStartToggle.IsEnabled=installed && !DebugMode;
            using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Product.ServiceName);
            AutoStartToggle.IsOn=(int?)key?.GetValue("Start")==2;
            JobsList.ItemsSource=status?.Jobs.Select(j=>new JobRow(j.Name,j.Message,j.Created.ToLocalTime().ToString("g"))).ToList() ?? [];
            NoJobs.Visibility=status?.Jobs.Count>0 ? Visibility.Collapsed : Visibility.Visible;
            NetworkDetails.Text=status is null ? connectionError ?? "打印服务未运行。请先安装或启动服务。" :
                $"服务端口：TCP {status.Port}    设备发现：UDP 5353\n{status.DiscoveryStatus}\n当前地址：{string.Join("，",status.Addresses)}\n\n"+
                string.Join("\n",status.SharedPrinters.Select(p=>$"{p.Name}\nipp://{status.Addresses.FirstOrDefault(a=>!a.Contains(':')) ?? "电脑IP"}:{status.Port}/printers/{p.Id}"));
            static string PrinterFingerprint(PrinterInfo p)=>p.Id+":"+p.Summary+":"+p.DefaultMedia+":"+string.Join(',',p.Media.Select(m=>$"{m.Key}/{m.Width}/{m.Height}"));
            if (refreshPrinters || status is not null && !_printers.Select(p=>PrinterFingerprint(p.Info)).SequenceEqual(status.Printers.Select(PrinterFingerprint)))
            {
                var available=status?.Printers ?? await Task.Run(PrinterCatalog.Enumerate);
                var saved=_printers.ToDictionary(p=>p.Id);
                var selected=(PrintersList.SelectedItem as PrinterRow)?.Id;
                _printers.Clear();
                foreach (var printer in available)
                {
                    var shared=status?.SharedPrinters.FirstOrDefault(p=>p.Id==printer.Id);
                    var old=saved.GetValueOrDefault(printer.Id);
                    _printers.Add(new(printer,old?.Shared ?? shared is not null,old?.DefaultMedia ?? shared?.DefaultMedia ?? printer.DefaultMedia));
                }
                PrintersList.SelectedItem=_printers.FirstOrDefault(p=>p.Id==selected) ?? _printers.FirstOrDefault();
                ShowPaper();
            }
            PrinterCount.Text=status?.Error ?? $"{_printers.Count} 台打印机";
            NoPrinters.Text=status?.Error ?? "没有检测到打印机。请先在 Windows 设置中安装驱动，并确认 Print Spooler 正在运行。";
            NoPrinters.Visibility=_printers.Count==0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_formsInitialized && status is not null)
            {
                AuthToggle.IsOn=status.AuthenticationEnabled; UsernameBox.Text=status.Username;
                _formsInitialized=true;
            }
            CredentialState.Text=status?.HasPassword==true ? "已设置密码。留空可保留；更改用户名时必须设置新密码。" : "尚未设置打印密码。";
            SetCredentialsEnabled();
        }
        finally { _rendering=false; }
    }
    private void Printers_SelectionChanged(object sender,SelectionChangedEventArgs e)=>ShowPaper();
    private void ShowPaper()
    {
        if (PaperCard is null) return;
        var row=PrintersList.SelectedItem as PrinterRow;
        PaperCard.Visibility=row?.Usable==true ? Visibility.Visible : Visibility.Collapsed;
        if (row is null) return;
        var previous=_rendering; _rendering=true;
        try
        {
            PaperPrinterName.Text=row.Name; PaperCombo.ItemsSource=row.Info.Media;
            PaperCombo.SelectedItem=row.Info.Media.FirstOrDefault(m=>m.Key==row.DefaultMedia) ?? row.Info.Media.FirstOrDefault();
            PaperDetails.Text=string.Join("\n",row.Info.Media.Select(m=>m.Description));
        }
        finally { _rendering=previous; }
    }
    private void Paper_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if (!_rendering && PrintersList.SelectedItem is PrinterRow row && PaperCombo.SelectedItem is MediaSize media) row.DefaultMedia=media.Key;
    }
    private void Auth_Toggled(object sender,RoutedEventArgs e)
    { if (CredentialFields is not null) SetCredentialsEnabled(); }
    private void SetCredentialsEnabled()
    {
        if (UsernameBox is null || PasswordBox is null || ConfirmPasswordBox is null) return;
        UsernameBox.IsEnabled=PasswordBox.IsEnabled=ConfirmPasswordBox.IsEnabled=AuthToggle.IsOn;
    }
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await Run(async ()=>await RefreshAsync(true));
    private async void Service_Click(object sender,RoutedEventArgs e)=>await Run(async ()=>
    {
        if (_status is not null) { await Diagnose(); return; }
        if (DebugMode) await StartDebugService(); else await Admin(Installed() ? "--start" : "--install");
        await RefreshAsync(true);
        Tell("服务已就绪", "勾选要共享的打印机，然后保存共享设置。",InfoBarSeverity.Success);
    });
    private async Task EnsureRunning()
    {
        if (_status is not null) return;
        if (DebugMode) await StartDebugService(); else await Admin(Installed() ? "--start" : "--install");
        await RefreshAsync(false);
        if (_status is null) throw new InvalidOperationException("服务尚未就绪，请稍后刷新。若仍失败，请使用“修复服务与防火墙”。");
    }
    private async void SavePrinters_Click(object sender,RoutedEventArgs e)=>await Run(async ()=>
    {
        var selected=_printers.Where(p=>p.Shared && p.Usable).Select(p=>new SharedPrinter(p.Id,p.Name,p.DefaultMedia)).ToList();
        if (selected.Count==0) throw new ArgumentException("请至少勾选一台可用打印机。临时停止打印请使用上方的共享开关。");
        await EnsureRunning();
        await Apply(new(selected,_status!.AuthenticationEnabled,_status.Username,Enabled:_status.Enabled));
        Tell("共享设置已保存","设备发现将在几秒内更新。若 Mac 仍显示旧纸张列表，请移除并重新添加此打印机。",InfoBarSeverity.Success);
    });
    private async void SaveAccount_Click(object sender,RoutedEventArgs e)=>await Run(async ()=>
    {
        if (PasswordBox.Password!=ConfirmPasswordBox.Password) throw new ArgumentException("两次输入的密码不一致。");
        var password=PasswordBox.Password; var username=UsernameBox.Text.Trim(); var enabled=AuthToggle.IsOn;
        if (password.Length>0) DigestSecret.Validate(username,password);
        if (enabled && password.Length==0 && (_status?.HasPassword!=true || _status.Username!=username))
            throw new ArgumentException("首次开启认证或更改用户名时，请输入新密码。");
        await EnsureRunning();
        await Apply(new(_status!.SharedPrinters.ToList(),enabled,username,password.Length==0 ? null : password,_status.Enabled));
        _rendering=true;
        try { AuthToggle.IsOn=enabled; UsernameBox.Text=username; SetCredentialsEnabled(); }
        finally { _rendering=false; }
        PasswordBox.Password=""; ConfirmPasswordBox.Password="";
        Tell("账户设置已保存",enabled ? $"在 Apple 设备的打印认证窗口中使用用户名“{username}”和你设置的密码。" : "局域网设备现在可以直接打印。",InfoBarSeverity.Success);
    });
    private async void Sharing_Toggled(object sender,RoutedEventArgs e)
    {
        if (_rendering || !_ready || _status is null) return;
        var enabled=SharingToggle.IsOn;
        await Run(async ()=>await Apply(new(_status.SharedPrinters.ToList(),_status.AuthenticationEnabled,_status.Username,Enabled:enabled)));
    }
    private async void AutoStart_Toggled(object sender,RoutedEventArgs e)
    {
        if (_rendering || !_ready) return;
        var enabled=AutoStartToggle.IsOn;
        await Run(async ()=>
        {
            await Admin(enabled ? "--enable-autostart" : "--disable-autostart"); await RefreshAsync(false);
            Tell("启动方式已更新",enabled ? "打印服务将在开机后延迟自动启动，并在异常退出后自动恢复。" : "打印服务已改为手动启动。",InfoBarSeverity.Success);
        });
    }
    private async void Repair_Click(object sender,RoutedEventArgs e)=>await Run(async ()=>
    { await Admin("--repair"); await RefreshAsync(true); Tell("修复已完成","已检查服务恢复策略，并重建专用/域网络的局域网防火墙规则。",InfoBarSeverity.Success); });
    private async void Stop_Click(object sender,RoutedEventArgs e)=>await Run(async ()=>
    { await Admin("--stop"); await RefreshAsync(false); Tell("服务已停止","需要打印时可重新启动。",InfoBarSeverity.Informational); });
    private async void Diagnose_Click(object sender,RoutedEventArgs e)=>await Run(Diagnose);
    private async Task Diagnose()
    {
        await EnsureRunning();
        var reply=await ControlClient.SendAsync(new("rescan")); if (!reply.Success) throw new InvalidOperationException(reply.Error);
        using var client=new TcpClient(); using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("127.0.0.1",_status!.Port,deadline.Token);
        await RefreshAsync(true);
        Tell("本机服务可连接","已验证本机 TCP 端口，并请求重新扫描打印机和广播。手机与 Mac 的网络可达性仍需在对应设备上确认。",InfoBarSeverity.Success);
    }
    private void CopyDiagnostics_Click(object sender,RoutedEventArgs e)
    {
        var text=$"CX Printer\n{RailStatus.Text}\n{NetworkDetails.Text}\n认证：{(_status?.AuthenticationEnabled==true ? "已启用" : "未启用")}\n自启：{AutoStartToggle.IsOn}\n\n"+
            string.Join("\n",_printers.Select(p=>$"{p.Name}: {p.Summary}\n"+string.Join("; ",p.Info.Media.Select(m=>$"{m.Key}={m.Width}x{m.Height}"))));
        var package=new DataPackage(); package.SetText(text); Clipboard.SetContent(package);
        Tell("已复制诊断信息","包含服务、网络和纸张信息，不包含密码或凭据摘要。",InfoBarSeverity.Success);
    }
    private async void Remove_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new ContentDialog { XamlRoot=Root.XamlRoot,Title="卸载打印服务？",Content="这会停止共享，移除 CX Printer 服务和它的防火墙规则。设置和诊断记录将保留。",PrimaryButtonText="卸载服务",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close };
        if (await dialog.ShowAsync()!=ContentDialogResult.Primary) return;
        await Run(async ()=> { await Admin("--remove"); _formsInitialized=false; await RefreshAsync(true); Tell("服务已卸载","可以随时重新安装。",InfoBarSeverity.Informational); });
    }
    private async Task Apply(ApplySettings settings)
    {
        var reply=await ControlClient.SendAsync(new("apply",settings));
        if (!reply.Success) throw new InvalidOperationException(reply.Error);
        _status=reply.Status; await RefreshAsync(false);
    }
    private async Task StartDebugService()
    {
        try { var running=await ControlClient.SendAsync(new("status")); if (running.Success) return; }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
        var exe=Path.Combine(AppContext.BaseDirectory,"Bridge","CxPrint.Service.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("没有找到调试服务。请重新生成解决方案。",exe);
        var info=new ProcessStartInfo(exe) { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add("--console");
        _debugProcess=Process.Start(info) ?? throw new InvalidOperationException("无法启动调试服务。");
        for (var attempt=0;attempt<10;attempt++)
        {
            await Task.Delay(500);
            if (_debugProcess.HasExited) throw new InvalidOperationException("调试服务启动失败，请查看 Bridge/DebugData/Logs，或单独启动后台项目调试。");
            try { var reply=await ControlClient.SendAsync(new("status")); if (reply.Success) return; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
        }
        throw new TimeoutException("调试服务启动超时。");
    }
    private async Task Admin(string command)
    {
        if (DebugMode) throw new InvalidOperationException("当前为本机调试模式。要管理系统服务，请切换到“CX Printer · 已安装的服务”启动配置。");
        var bundled=Path.Combine(AppContext.BaseDirectory,"Bridge","CxPrint.Service.exe");
        var installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"CX Printer","Bridge","CxPrint.Service.exe");
        var exe=command=="--install" && File.Exists(bundled) ? bundled : File.Exists(installed) ? installed : bundled;
        if (!File.Exists(exe)) throw new FileNotFoundException("缺少后台服务组件。请运行 scripts/build.ps1，并从 artifacts 中启动完整版本。",exe);
        var info=new ProcessStartInfo(exe) { UseShellExecute=true,Verb="runas",WorkingDirectory=Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add(command);
        if (command=="--install") { info.ArgumentList.Add("--owner"); info.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value); }
        using var process=Process.Start(info) ?? throw new InvalidOperationException("无法启动服务管理程序。");
        await process.WaitForExitAsync();
        if (process.ExitCode!=0) throw new InvalidOperationException($"服务操作失败（退出码 {process.ExitCode}）。详细记录位于 %ProgramData%\\CX Printer\\Logs\\setup.log。");
        if (command is "--install" or "--start" or "--repair") await Task.Delay(1500);
    }
    private async Task Run(Func<Task> action)
    {
        if (_busy) return; _busy=true; BusyRing.IsActive=true; ServiceButton.IsEnabled=false;
        try { await action(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode==1223) { Tell("操作已取消","未完成服务配置。",InfoBarSeverity.Informational); await RefreshAsync(false); }
        catch (Exception ex) { Tell("操作未完成",ex.Message,InfoBarSeverity.Error); try { await RefreshAsync(_printers.Count==0); } catch { } }
        finally { _busy=false; BusyRing.IsActive=false; ServiceButton.IsEnabled=true; }
    }
    private void Tell(string title,string message,InfoBarSeverity severity)
    { Notice.Title=title; Notice.Message=message; Notice.Severity=severity; Notice.IsOpen=true; }
}

public sealed record JobRow(string Name,string Message,string Created);

public sealed class PrinterRow(PrinterInfo info,bool shared,string defaultMedia) : INotifyPropertyChanged
{
    private bool _shared=shared;
    public PrinterInfo Info { get; }=info;
    public string Id=>Info.Id;
    public string Name=>Info.Name;
    public string Summary=>Info.Summary;
    public bool Usable=>Info.Usable;
    public string DefaultMedia { get; set; }=defaultMedia;
    public bool Shared { get=>_shared; set { _shared=value; PropertyChanged?.Invoke(this,new(nameof(Shared))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
