using System.Net;
using System.Text;
using CxPrint.Core;
using CxPrint.Service;
using CxPrint.Windows;
using Makaretu.Dns;
using Message=Makaretu.Dns.Message;

internal static class PrinterRegressionTests
{
    private static void Assert(bool condition,string message)
    { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10,timeout.Token);
    }
    public static async Task RunAsync(Func<string,Func<Task>,Task> test,PrinterInfo fixture)
    {
        await test("Slow drivers publish names and ready printers before the scan finishes",async ()=>
        {
            using var release=new ManualResetEventSlim();
            var calls=0;
            var scanner=new PrinterScanner(()=>["slow","fast"],name=>
            {
                Interlocked.Increment(ref calls);
                if (name=="slow") release.Wait(TimeSpan.FromSeconds(10));
                return fixture with { Id=Product.PrinterId(name),Name=name };
            });
            var scan=scanner.RefreshAsync();
            try
            {
                await Until(()=>scanner.Snapshot.Printers.Count==2 && scanner.Snapshot.Printers.Any(p=>p.Name=="fast" && p.Usable));
                Assert(!scan.IsCompleted,"A slow driver should still be running.");
                Assert(scanner.Snapshot.Refreshing && scanner.Snapshot.Message!.Contains("slow"),"Current printer progress was not published.");
                using var cancelled=new CancellationTokenSource();
                var duplicate=scanner.RefreshAsync(cancelled.Token); cancelled.Cancel();
                try { await duplicate; throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
                Assert(calls==2,"A repeated/cancelled request started duplicate native calls.");
            }
            finally { release.Set(); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert(!scanner.Snapshot.Refreshing && scanner.Snapshot.Printers.All(p=>p.Usable),"Final scan is incomplete.");
            await scanner.RefreshAsync();
            Assert(calls==2,"A refresh reread cached driver capabilities.");
        });
        await test("Printer cache detects additions/removals and isolates a failing driver",async ()=>
        {
            IReadOnlyList<string> names=["good","broken"];
            var scanner=new PrinterScanner(()=>names,name=>name=="broken" ? throw new IOException("fixture driver error") : fixture with { Name=name,Id=Product.PrinterId(name) });
            await scanner.RefreshAsync();
            Assert(scanner.Snapshot.Printers.Single(p=>p.Name=="good").Usable,"A broken driver hid another printer.");
            Assert(scanner.Snapshot.Printers.Single(p=>p.Name=="broken").Error=="fixture driver error","The driver error was lost.");
            names=["new"]; await scanner.RefreshAsync();
            Assert(scanner.Snapshot.Printers.Count==1 && scanner.Snapshot.Printers[0].Name=="new","The cache hid changed queue membership.");
        });
        await test("An enumeration error retains the previous printer snapshot",async ()=>
        {
            var fail=false;
            var scanner=new PrinterScanner(()=>fail ? throw new IOException("fixture spooler error") : [fixture.Name],_=>fixture);
            await scanner.RefreshAsync(); fail=true; await scanner.RefreshAsync();
            Assert(scanner.Snapshot.Printers.Single()==fixture,"A transient enumeration error erased the list.");
            Assert(!scanner.Snapshot.Refreshing && scanner.Snapshot.Error!.Contains("fixture spooler error"),"Enumeration failure is not visible.");
        });
        await test("DNS-SD regression reproduces the old ASCII error",()=>
        {
            var legacy=new TXTRecord { Strings=["note=CX Printer · PC"] };
            try { _=legacy.GetData(); throw new Exception("Expected the old library ASCII failure."); }
            catch (ArgumentException ex) { Assert(ex.Message.Contains("ASCII"),"Unexpected legacy failure."); }
            return Task.CompletedTask;
        });
        await test("DNS-SD encodes and decodes Chinese, middle dots, emoji and long TXT values",async ()=>
        {
            foreach (var name in new[]{"HP Laser NS MFP","办公室打印机","财务😀打印机"})
            {
                var printer=fixture with { Name=name,Id=Product.PrinterId(name),Driver=string.Concat(Enumerable.Repeat("中文驱动😀",60)) };
                var profile=PrinterAdvertisement.Create(printer,new BridgeSettings(),[IPAddress.Parse("192.0.2.10")],"办公室电脑😀");
                var txt=profile.Resources.OfType<TXTRecord>().Single();
                Assert(txt.Strings.All(s=>Encoding.UTF8.GetByteCount(s)<=255),"A TXT entry exceeds its one-byte length.");
                var packet=new Message { QR=true }; packet.Answers.AddRange(profile.Resources);
                var bytes=packet.ToByteArray();
                var decoded=new Message(); decoded.Read(bytes);
                Assert(decoded.Answers.OfType<TXTRecord>().Single().Strings.SequenceEqual(txt.Strings),"UTF-8 TXT did not survive a real DNS wire roundtrip.");
                Assert(txt.Strings.Contains("note=CX Printer - 办公室电脑😀"),"The Unicode discovery note was corrupted.");
                // Catalog insertion and a real service lookup must work too, without opening a network socket.
                using var mdns=new MulticastService(); using var discovery=new ServiceDiscovery(mdns);
                discovery.Advertise(profile);
                var query=new Message(); query.Questions.Add(new Question { Name=profile.FullyQualifiedName,Type=DnsType.TXT });
                var answer=await discovery.NameServer.ResolveAsync(query);
                Assert(answer.Answers.OfType<TXTRecord>().Any(),"A printer TXT query returned no answer.");
                _=answer.ToByteArray();
            }
        });
        await test("Custom Apple names roundtrip as one literal DNS-SD label and retain a stable host",async ()=>
        {
            foreach (var name in new[]{"财务.打印机\\A😀",new string('A',63),new string('中',21)})
            {
                var shared=new SharedPrinter(fixture.Id,fixture.Name,DisplayName:name);
                var settings=new BridgeSettings { Printers=[shared] };
                var profile=PrinterAdvertisement.Create(fixture,settings,[IPAddress.Parse("192.0.2.10")],"FILESERVER");
                Assert(profile.InstanceName.Labels.Single()==name,"The configured name was changed or split into multiple labels.");
                using var mdns=new MulticastService(); using var discovery=new ServiceDiscovery(mdns);
                discovery.Advertise(profile);
                var query=new Message(); query.Questions.Add(new Question { Name=profile.QualifiedServiceName,Type=DnsType.PTR });
                var answer=await discovery.NameServer.ResolveAsync(query);
                var decoded=new Message(); decoded.Read(answer.ToByteArray());
                Assert(decoded.Answers.OfType<PTRRecord>().Any(p=>p.DomainName.Labels.First()==name),"The browse response lost the custom Unicode name.");
                var packet=new Message { QR=true }; packet.Answers.AddRange(profile.Resources);
                decoded=new Message(); decoded.Read(packet.ToByteArray());
                var srv=decoded.Answers.OfType<SRVRecord>().Single();
                Assert(srv.Name.Labels.First()==name && srv.Target==$"cx-{Product.PrinterId("FILESERVER")}.local","Renaming changed the host identity.");
                Assert(decoded.Answers.OfType<TXTRecord>().Single().Strings.Contains("rp=printers/"+fixture.Id),"Renaming changed the queue URI.");
            }
        });
        await test("English printer and host announce, browse, resolve and withdraw through the production library",async ()=>
        {
            var name="HP Laser NS MFP 1005";
            var printer=fixture with { Name=name,Id=Product.PrinterId(name),Driver="HP Laser NS MFP 1005" };
            var profile=PrinterAdvertisement.Create(printer,new BridgeSettings(),[IPAddress.Parse("192.0.2.10")],"FILESERVER");
            var txt=profile.Resources.OfType<TXTRecord>().Single();
            Assert(txt.Strings.All(s=>s.All(character=>character<=0x7f)),"English printer discovery TXT contains non-ASCII characters.");
            Assert(txt.Strings.Contains("note=CX Printer - FILESERVER"),"The compatibility-safe discovery note is missing.");
            using var mdns=new MulticastService(); using var discovery=new ServiceDiscovery(mdns);
            discovery.Advertise(profile);
            mdns.Start(); // SendAnswer requires Start to initialize the packet-size limit.
            discovery.Announce(profile); // Includes Truncate/Length and both real SendAnswer calls.
            foreach (var type in new[]{DnsType.PTR,DnsType.SRV,DnsType.TXT})
            {
                var query=new Message();
                query.Questions.Add(new Question { Name=type==DnsType.PTR ? profile.QualifiedServiceName : profile.FullyQualifiedName,Type=type });
                var answer=await discovery.NameServer.ResolveAsync(query);
                Assert(answer.Answers.Any(r=>r.Type==type),$"The {type} query was not answered.");
                var decoded=new Message(); decoded.Read(answer.ToByteArray());
                Assert(decoded.Answers.Any(r=>r.Type==type),$"The {type} response was lost on the wire.");
                mdns.SendAnswer(answer);
            }
            discovery.Unadvertise(profile);
        });
        await test("DNS-SD targets use URI-safe hostnames with matching IPv4 and scoped IPv6 records",async ()=>
        {
            var addresses=new[]{IPAddress.Parse("192.168.0.31"),IPAddress.Parse("2001:db8::31"),IPAddress.Parse("fe80::22ea:4e76:403:63c3%12")};
            // Follow production order: construct discovery before the printer profiles.
            using var mdns=new MulticastService(); using var discovery=new ServiceDiscovery(mdns);
            foreach (var name in new[]{"HP Laser NS MFP 1005","办公室 打印机😀","Printer_with_underscores"})
            {
                var printer=fixture with { Name=name,Id=Product.PrinterId(name),Driver="HP Laser NS MFP 1005 PCLm-S" };
                var profile=PrinterAdvertisement.Create(printer,new BridgeSettings(),addresses,"MASTER-TEA-FILE");
                discovery.Advertise(profile);
                var query=new Message(); query.Questions.Add(new Question { Name=profile.FullyQualifiedName,Type=DnsType.SRV });
                var answer=await discovery.NameServer.ResolveAsync(query);
                var decoded=new Message(); decoded.Read(answer.ToByteArray());
                var srv=decoded.Answers.OfType<SRVRecord>().Single();
                var host=string.Join('.',srv.Target.Labels);
                Assert(Uri.TryCreate($"ipp://{host}:{srv.Port}/printers/{printer.Id}",UriKind.Absolute,out _),"The advertised SRV target cannot be used in an IPP URI.");
                Assert(srv.Target.Labels.All(label=>label.Length is >0 and <=63 && label.All(c=>char.IsAsciiLetterOrDigit(c) || c=='-') && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])),"The SRV target is not a DNS hostname.");
                Assert(srv.Target==profile.HostName && srv.Target==$"cx-{Product.PrinterId("MASTER-TEA-FILE")}.local","The target is not a stable host identity.");
                var resolved=decoded.Answers.Concat(decoded.AdditionalRecords).OfType<AddressRecord>().ToArray();
                Assert(resolved.Length==addresses.Length && resolved.All(a=>a.Name==srv.Target),"SRV resolution did not include all matching addresses.");
                Assert(resolved.Select(a=>Convert.ToHexString(a.Address.GetAddressBytes())).ToHashSet().SetEquals(addresses.Select(a=>Convert.ToHexString(a.GetAddressBytes()))),"Address bytes changed during DNS resolution.");
                foreach (var type in new[]{DnsType.A,DnsType.AAAA})
                {
                    var addressQuery=new Message(); addressQuery.Questions.Add(new Question { Name=srv.Target,Type=type });
                    var addressAnswer=await discovery.NameServer.ResolveAsync(addressQuery);
                    Assert(addressAnswer.Answers.Any(a=>a.Type==type && a.Name==srv.Target),$"The {type} hostname lookup has no answer.");
                }
            }
        });
        await test("PDF test detection uses the driver and output names are unique and bounded",()=>
        {
            var pdf=fixture with { Name="Renamed test printer",Driver="Microsoft Print To PDF",Port="PORTPROMPT:" };
            Assert(pdf.IsPdfTest && pdf.Usable && pdf.Summary.Contains("PDF 测试"),"A renamed Microsoft PDF queue was not recognized.");
            Assert(!(fixture with { Name="Microsoft Print to PDF" }).IsPdfTest,"A queue name spoofed PDF driver detection.");
            var directory=Path.Combine("artifacts","test-data","pdf-output-"+Guid.NewGuid().ToString("N"));
            var first=WindowsPrintBackend.CreateTestOutputPath(directory);
            var second=WindowsPrintBackend.CreateTestOutputPath(directory);
            Assert(first!=second && Path.GetDirectoryName(first)==Path.GetFullPath(directory),"PDF output path is not unique or confined.");
            for (var i=0;i<100;i++) File.WriteAllBytes(Path.Combine(directory,$"CX-Test-{i}.pdf"),[]);
            try { WindowsPrintBackend.CreateTestOutputPath(directory); throw new Exception("PDF file count limit was ignored."); }
            catch (IOException) { }
            return Task.CompletedTask;
        });
        await test("Setup progress arguments retain CLI compatibility and reject unsafe pipe names",()=>
        {
            Assert(ServiceSetup.ParseArguments(["--install"]).ProgressPipe is null,"Legacy install arguments stopped working.");
            var pipe="CxPrintBridge.Setup."+Guid.NewGuid().ToString("N");
            var options=ServiceSetup.ParseArguments(["--install","--owner","S-1-5-21-1-2-3-1001","--progress-pipe",pipe]);
            Assert(options.ProgressPipe==pipe && options.Owner=="S-1-5-21-1-2-3-1001","Installer options were parsed incorrectly.");
            foreach (var args in new[]{new[]{"--start","--progress-pipe","other-pipe"},new[]{"--stop","--owner","S-1-5-18"},new[]{"--install","--progress-pipe"}})
            {
                try { ServiceSetup.ParseArguments(args); throw new Exception("Invalid arguments accepted."); }
                catch (ArgumentException) { }
            }
            return Task.CompletedTask;
        });
        await test("Elevated setup progress transports stages and detailed failures",async ()=>
        {
            using var listener=new SetupProgressListener();
            using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var values=new List<SetupProgress>();
            var reading=listener.ListenAsync(new CallbackProgress(values.Add),cancellation.Token);
            await using (var writer=await SetupProgressWriter.ConnectAsync(listener.Name))
            {
                Assert(writer.IsConnected,"Progress connection failed: "+writer.Error);
                await writer.ReportAsync(new(25,"正在复制后台组件…"));
                await writer.ReportAsync(new(75,"正在配置防火墙…"));
                await writer.ReportAsync(new(0,"fixture detailed failure",true));
                Assert(writer.Error is null,"Progress write failed: "+writer.Error);
            }
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(values.Count==3 && values[0].Percent==25 && listener.Latest is { Failed:true,Message:"fixture detailed failure" },"The progress channel lost a stage or failure.");
        });
    }
    private sealed class CallbackProgress(Action<SetupProgress> report) : IProgress<SetupProgress>
    { public void Report(SetupProgress value)=>report(value); }
}
