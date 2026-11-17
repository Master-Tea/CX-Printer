using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using CxPrint.Core;

namespace CxPrint.Service;

public static class ServiceSetup
{
    public static bool IsCommand(string arg)=>arg is "--install" or "--repair" or "--start" or "--stop" or "--enable-autostart" or "--disable-autostart" or "--remove";
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("此操作需要管理员权限。");
            var command=args[0];
            if (!IsCommand(command) || args.Length!=1 && !(command=="--install" && args.Length==3 && args[1]=="--owner"))
                throw new ArgumentException("无效的服务管理参数。");
            var programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var destination=Path.Combine(programFiles,"CX Printer","Bridge");
            var installedExe=Path.Combine(destination,"CxPrint.Service.exe");
            if (command=="--install")
            {
                var owner=args.Length==3 ? new SecurityIdentifier(args[2]) : identity.User!;
                if (!owner.IsAccountSid()) throw new ArgumentException("服务管理者必须是有效的 Windows 用户 SID。");
                _=owner.Translate(typeof(NTAccount));
                VerifyAncestors(destination); VerifyTrustedTree(destination); SecureDirectory(destination,publicRead:true);
                VerifyAncestors(Paths.Root); VerifyTrustedTree(Paths.Root); SecureDirectory(Paths.Root,publicRead:false);
                Directory.CreateDirectory(Paths.Logs); Directory.CreateDirectory(Paths.Spool);
                if (Exists()) await Stop();
                CopyTree(AppContext.BaseDirectory,destination);
                if (!File.Exists(Paths.Settings))
                {
                    var settings=new BridgeSettings { OwnerSid=owner.Value };
                    File.WriteAllText(Paths.Settings,JsonSerializer.Serialize(settings,Product.Json));
                }
                else
                {
                    var existing=JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(Paths.Settings),Product.Json)
                        ?? throw new InvalidDataException("现有设置损坏，请先备份后检查。");
                    existing.Validate(); // Reinstallation keeps printer selections and authentication.
                }
                SecureTree(destination,publicRead:true); SecureTree(Paths.Root,publicRead:false);
                if (!Exists()) await Sc("create",Product.ServiceName,"binPath=",'"'+installedExe+'"',"start=","delayed-auto","depend=","Spooler","DisplayName=","CX Printer Bridge");
                else await Sc("config",Product.ServiceName,"binPath=",'"'+installedExe+'"',"start=","delayed-auto","depend=","Spooler");
                await Repair(installedExe); await Start();
            }
            else if (command=="--repair") { await Repair(installedExe); await Start(); }
            else if (command=="--start") await Start();
            else if (command=="--stop") await Stop();
            else if (command=="--enable-autostart") { await Sc("config",Product.ServiceName,"start=","delayed-auto"); await Recovery(); }
            else if (command=="--disable-autostart") await Sc("config",Product.ServiceName,"start=","demand");
            else if (command=="--remove")
            {
                if (Exists()) { await Stop(); await Sc("delete",Product.ServiceName); }
                await Netsh("advfirewall","firewall","delete","rule","name=CX Printer IPP");
                await Netsh("advfirewall","firewall","delete","rule","name=CX Printer mDNS");
            }
            return 0;
        }
        catch (Exception ex)
        {
            try { VerifyAncestors(Paths.Root); VerifyTrustedTree(Paths.Root); SecureDirectory(Paths.Root,publicRead:false); Directory.CreateDirectory(Paths.Logs); File.AppendAllText(Path.Combine(Paths.Logs,"setup.log"),$"{DateTimeOffset.Now:O} {args.FirstOrDefault()} {ex}{Environment.NewLine}"); } catch { }
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }
    private static async Task Repair(string installedExe)
    {
        if (!File.Exists(installedExe)) throw new FileNotFoundException("请先安装服务。",installedExe);
        VerifyAncestors(installedExe); VerifyTrustedTree(Path.GetDirectoryName(installedExe)!); VerifyTrustedTree(Paths.Root);
        await Sc("config",Product.ServiceName,"binPath=",'"'+installedExe+'"',"depend=","Spooler");
        await Sc("description",Product.ServiceName,"将 Windows 打印队列通过 IPP / DNS-SD 提供给局域网中的 Apple 设备。");
        await Recovery();
        await Netsh("advfirewall","firewall","delete","rule","name=CX Printer IPP");
        await Netsh("advfirewall","firewall","delete","rule","name=CX Printer mDNS");
        var settings=JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(Paths.Settings),Product.Json)!;
        await Netsh("advfirewall","firewall","add","rule","name=CX Printer IPP","dir=in","action=allow","protocol=TCP",$"localport={settings.Port}","profile=private,domain","remoteip=localsubnet","program="+installedExe,"enable=yes");
        await Netsh("advfirewall","firewall","add","rule","name=CX Printer mDNS","dir=in","action=allow","protocol=UDP","localport=5353","profile=private,domain","remoteip=localsubnet","program="+installedExe,"enable=yes");
    }
    private static async Task Recovery()
    {
        await Sc("failure",Product.ServiceName,"reset=","86400","actions=","restart/5000/restart/15000/restart/60000");
        await Sc("failureflag",Product.ServiceName,"1");
    }
    private static bool Exists()
    {
        var services=ServiceController.GetServices();
        try { return services.Any(s=>s.ServiceName==Product.ServiceName); }
        finally { foreach (var service in services) service.Dispose(); }
    }
    private static async Task Start()
    {
        using var service=new ServiceController(Product.ServiceName); service.Refresh();
        if (service.Status==ServiceControllerStatus.StopPending)
            await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30)));
        service.Refresh();
        if (service.Status==ServiceControllerStatus.Stopped) service.Start();
        await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(45)));
    }
    private static async Task Stop()
    {
        using var service=new ServiceController(Product.ServiceName); service.Refresh();
        if (service.Status==ServiceControllerStatus.Stopped) return;
        if (service.Status!=ServiceControllerStatus.StopPending) service.Stop();
        await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(45)));
    }
    private static Task Sc(params string[] args)=>Run("sc.exe",args);
    private static Task Netsh(params string[] args)=>Run("netsh.exe",args,args.Length>2 && args[2]=="delete");
    private static async Task Run(string command,string[] args,bool allowNoMatch=false)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,command))
            { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process=Process.Start(info) ?? throw new InvalidOperationException("无法启动 "+command);
        var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree:true); throw new System.TimeoutException(command+" 执行超时。"); }
        var output=await stdout+await stderr;
        if (process.ExitCode!=0 && !(allowNoMatch && process.ExitCode==1))
            throw new InvalidOperationException($"{command} 失败（{process.ExitCode}）：{output}");
    }
    private static void VerifyAncestors(string path)
    {
        var current=new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new IOException("安装目录不能包含符号链接或目录联接："+current.FullName);
            current=current.Parent;
        }
    }
    private static readonly SecurityIdentifier Admins=new(WellKnownSidType.BuiltinAdministratorsSid,null);
    private static readonly SecurityIdentifier SystemAccount=new(WellKnownSidType.LocalSystemSid,null);
    private static bool Trusted(SecurityIdentifier sid)=>sid==Admins || sid==SystemAccount;
    private static void VerifyTrustedTree(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        var directory=Directory.Exists(path);
        FileSystemInfo item=directory ? new DirectoryInfo(path) : new FileInfo(path);
        if ((item.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("系统安装目录中不能包含链接："+path);
        FileSystemSecurity security=directory ? ((DirectoryInfo)item).GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner))
            throw new UnauthorizedAccessException("现有目录或文件的所有者不受信任，请先备份并由管理员检查："+path);
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if (rule.AccessControlType==AccessControlType.Allow && !Trusted((SecurityIdentifier)rule.IdentityReference) && (rule.FileSystemRights&writes)!=0)
                throw new UnauthorizedAccessException("现有系统目录允许普通账户写入，已中止安装："+path);
        if (directory) foreach (var child in Directory.EnumerateFileSystemEntries(path)) VerifyTrustedTree(child);
    }
    private static void SecureDirectory(string path,bool publicRead)
    {
        var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false); security.SetOwner(Admins);
        foreach (var sid in new[]{Admins,SystemAccount})
            security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        if (publicRead) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        var info=new DirectoryInfo(path);
        if (info.Exists) info.SetAccessControl(security); else info.Create(security);
    }
    private static void SecureTree(string path,bool publicRead)
    {
        SecureDirectory(path,publicRead);
        foreach (var file in Directory.EnumerateFiles(path))
        {
            if ((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("安装目录包含链接文件。");
            var security=new FileSecurity(); security.SetAccessRuleProtection(true,false); security.SetOwner(Admins);
            foreach (var sid in new[]{Admins,SystemAccount}) security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
            if (publicRead) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
        foreach (var child in Directory.EnumerateDirectories(path)) { VerifyAncestors(child); SecureTree(child,publicRead); }
    }
    private static void CopyTree(string source,string destination)
    {
        if (Path.GetFullPath(source).TrimEnd('\\').Equals(Path.GetFullPath(destination).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) return;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("不允许从链接文件安装。");
            var target=Path.Combine(destination,Path.GetFileName(file)); VerifyAncestors(target);
            if (File.Exists(target) && (File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0) throw new IOException("目标文件是链接。");
            File.Copy(file,target,true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(directory) is "DebugData" or "DebugUI") continue;
            if ((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) throw new IOException("不允许从链接目录安装。");
            var next=Path.Combine(destination,Path.GetFileName(directory)); VerifyAncestors(next); Directory.CreateDirectory(next); CopyTree(directory,next);
        }
    }
}
