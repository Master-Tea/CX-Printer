using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CxPrint.Core;
using CxPrint.Service;
using CxPrint.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static CxPrint.Core.IppCodec;

var root=Path.GetFullPath(Path.Combine("artifacts","test-data",Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var paper=new MediaSize("iso_a4_210x297mm","A4",21000,29700,9);
var a3=new MediaSize("iso_a3_297x420mm","A3",29700,42000,8);
var printer=new PrinterInfo(Product.PrinterId("Fixture printer"),"Fixture printer","Test driver","TEST",true,true,[paper,a3],paper.Key);
var selected=new SharedPrinter(printer.Id,printer.Name);
var failures=0; var count=0;
void Assert(bool ok,string message="Assertion failed") { if (!ok) throw new Exception(message); }
async Task Test(string name,Func<Task> test)
{
    count++; try { await test(); Console.WriteLine("PASS "+name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL "+name+": "+ex); }
}
async Task Throws<T>(Func<Task> action) where T:Exception
{ try { await action(); } catch (T) { return; } throw new Exception("Expected "+typeof(T).Name); }
JobManager Manager(FakeBackend backend,string name)=>new(backend,NullLogger<JobManager>.Instance,Path.Combine(root,name),_=>printer);
async Task<JobInfo> Terminal(JobManager manager,int id)
{
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (manager.Find(id)!.Info.State<7) await Task.Delay(10,deadline.Token);
    return manager.Find(id)!.Info;
}

await Test("Restart aborts interrupted jobs without reprinting",async ()=>
{
    var backend=new FakeBackend(); using var before=Manager(backend,"restart");
    var job=before.Create(printer,new(paper.Key),"restart","fixture","application/pdf");
    await before.UploadAsync(job.Info.Id,new MemoryStream("%PDF-fixture"u8.ToArray()),"application/pdf",default);
    using var after=Manager(backend,"restart"); Assert(after.Find(job.Info.Id)!.Info.State==8);
    await after.StartAsync(default); await Task.Delay(100); await after.StopAsync(default);
    Assert(backend.Calls==0); Assert(!File.Exists(Path.Combine(root,"restart",job.Info.Id.ToString(),"document.bin")));
});
await Test("Job IDs remain monotonic after history directories are removed",()=>
{
    var backend=new FakeBackend(); using var before=Manager(backend,"sequence");
    var first=before.Create(printer,new(paper.Key),"one","fixture","application/pdf"); before.Cancel(first.Info.Id);
    var path=Path.GetFullPath(Path.Combine(root,"sequence",first.Info.Id.ToString()));
    Assert(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)); Directory.Delete(path,true);
    using var after=Manager(backend,"sequence");
    Assert(after.Create(printer,new(paper.Key),"two","fixture","application/pdf").Info.Id>first.Info.Id);
    return Task.CompletedTask;
});
await Test("Empty upload aborts the job and removes partial content",async ()=>
{
    using var manager=Manager(new(),"empty"); var job=manager.Create(printer,new(paper.Key),"empty","fixture","application/pdf");
    await Throws<InvalidDataException>(()=>manager.UploadAsync(job.Info.Id,new MemoryStream(),"application/pdf",default));
    Assert(manager.Find(job.Info.Id)!.Info.State==8);
    Assert(!File.Exists(Path.Combine(root,"empty",job.Info.Id.ToString(),"document.incoming")));
});
await Test("Simultaneous duplicate upload and cancellation cannot print twice",async ()=>
{
    using var manager=Manager(new(),"duplicate"); var job=manager.Create(printer,new(paper.Key),"duplicate","fixture","application/pdf");
    using var source=new GateStream(); var uploading=manager.UploadAsync(job.Info.Id,source,"application/pdf",default);
    await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Throws<InvalidOperationException>(()=>manager.UploadAsync(job.Info.Id,new MemoryStream([1]),"application/pdf",default));
    manager.Cancel(job.Info.Id); source.Release.SetResult();
    await Throws<OperationCanceledException>(()=>uploading); Assert(manager.Find(job.Info.Id)!.Info.State==7);
});
await Test("Queue admission is bounded to 64 unfinished jobs",async ()=>
{
    using var manager=Manager(new(),"admission");
    for (var i=0;i<64;i++) manager.Create(printer,new(paper.Key),"fixture","fixture","application/pdf");
    await Throws<InvalidOperationException>(()=> { manager.Create(printer,new(paper.Key),"overflow","fixture","application/pdf"); return Task.CompletedTask; });
});
await Test("Backend failures become an aborted job with cleaned spool files",async ()=>
{
    var backend=new FakeBackend { Fail=true }; using var manager=Manager(backend,"failure"); await manager.StartAsync(default);
    try
    {
        var job=manager.Create(printer,new(a3.Key),"failure","fixture","application/pdf");
        await manager.UploadAsync(job.Info.Id,new MemoryStream("%PDF-fixture"u8.ToArray()),"application/pdf",default);
        Assert((await Terminal(manager,job.Info.Id)).State==8); Assert(backend.Calls==1);
    }
    finally { await manager.StopAsync(default); }
});

var settingsPath=Path.Combine(root,"http-settings.json");
File.WriteAllText(settingsPath,JsonSerializer.Serialize(new BridgeSettings { Port=18631,OwnerSid=WindowsIdentity.GetCurrent().User!.Value,Printers=[selected] },Product.Json));
var state=new StateStore(settingsPath);
var monitor=new PrinterMonitor(NullLogger<PrinterMonitor>.Instance,()=>[printer]); await monitor.RefreshAsync();
var discovery=new DiscoveryWorker(state,monitor,NullLogger<DiscoveryWorker>.Instance);
var httpBackend=new FakeBackend(); using var httpJobs=Manager(httpBackend,"http-jobs"); await httpJobs.StartAsync(default);
var endpoint=new IppEndpoint(state,monitor,httpJobs,discovery,new(),NullLogger<IppEndpoint>.Instance);
var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
await using var app=builder.Build(); app.MapPost("/printers/{id}",endpoint.HandleAsync); app.MapPost("/jobs/{id}",endpoint.HandleAsync);
await app.StartAsync();
var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var http=new HttpClient { BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(10) };
var requestId=1;
IppMessage Request(ushort operation,params IppAttribute[] attributes)=>new(2,0,operation,requestId++,
    [Text(1,IppTag.Charset,"attributes-charset","utf-8"),Text(1,IppTag.Language,"attributes-natural-language","en"),
     Text(1,IppTag.Uri,"printer-uri",$"ipp://localhost:18631/printers/{printer.Id}"),..attributes]);
async Task<(HttpResponseMessage Http,IppMessage? Ipp)> Send(IppMessage request,byte[]? document=null,string? auth=null)
{
    using var message=new HttpRequestMessage(HttpMethod.Post,"/printers/"+printer.Id);
    message.Content=new ByteArrayContent([..Write(request),..document ?? []]); message.Content.Headers.ContentType=new("application/ipp");
    if (auth is not null) message.Headers.TryAddWithoutValidation("Authorization",auth);
    var response=await http.SendAsync(message);
    return (response,response.Content.Headers.ContentType?.MediaType=="application/ipp" ? await ReadAsync(await response.Content.ReadAsStreamAsync()) : null);
}
try
{
    await Test("Real HTTP Get-Printer-Attributes publishes actual paper dimensions",async ()=>
    {
        var reply=await Send(Request(0x000b)); Assert(reply.Ipp?.Code==0);
        Assert(reply.Ipp!.Text("media-default")==paper.Key); Assert(reply.Ipp.Find("media-col-database")!.Values.Count==2);
        Assert(reply.Ipp.Find("media-col-database")!.Values[1].Member("media-size")!.Values[0].Member("y-dimension")!.Values[0].Integer==42000);
    });
    await Test("Validate-Job checks options without creating or printing a job",async ()=>
    {
        var reply=await Send(Request(0x0004,Text(2,IppTag.Keyword,"media",a3.Key)));
        Assert(reply.Ipp?.Code==0); Assert(httpJobs.Snapshot().Count==0 && httpBackend.Calls==0);
        Assert((await Send(Request(0x0004,Text(2,IppTag.Keyword,"media","unknown")))).Ipp?.Code==0x040b);
    });
    await Test("Real HTTP Print-Job preserves A3 through queue and backend",async ()=>
    {
        var reply=await Send(Request(0x0002,Text(2,IppTag.Keyword,"media",a3.Key),Text(1,IppTag.Mime,"document-format","application/pdf")),"%PDF-fixture"u8.ToArray());
        Assert(reply.Ipp?.Code==0); var id=reply.Ipp!.Number("job-id"); var final=await Terminal(httpJobs,id);
        Assert(final.State==9 && final.Pages==2); Assert(httpBackend.LastMedia==a3.Key);
    });
    await Test("Create-Job / Send-Document rejects duplicate document submission",async ()=>
    {
        var created=(await Send(Request(0x0005))).Ipp!; var id=created.Number("job-id"); var before=httpBackend.Calls;
        var request=Request(0x0006,Int(1,"job-id",id),Attr(1,"last-document",IppValue.Bool(true)));
        Assert((await Send(request,"%PDF-fixture"u8.ToArray())).Ipp?.Code==0); await Terminal(httpJobs,id);
        Assert((await Send(request,"%PDF-fixture"u8.ToArray())).Ipp?.Code==0x0404); Assert(httpBackend.Calls==before+1);
    });
    await Test("HTTP rejects mismatched printer URI and unsupported MIME type",async ()=>
    {
        var request=Request(0x000b); request=request with { Attributes=request.Attributes.Select(a=>a.Name=="printer-uri" ? Text(1,IppTag.Uri,"printer-uri","ipp://localhost/printers/other") : a).ToArray() };
        Assert((await Send(request)).Ipp?.Code==0x0400);
        Assert((await Send(Request(0x0004,Text(1,IppTag.Mime,"document-format","text/plain")))).Ipp?.Code==0x040a);
    });
    await Test("DPAPI secrets are encrypted and username changes require a new password",async ()=>
    {
        state.Apply(new([selected],true,"printer","fixture-password"),[printer]);
        var disk=File.ReadAllText(settingsPath); Assert(!disk.Contains("fixture-password"));
        Assert(WindowsSecurity.UnprotectSecret(state.Settings.Authentication.ProtectedHa1!)==DigestSecret.Create("printer","fixture-password"));
        await Throws<ArgumentException>(()=> { state.Apply(new([selected],false,"another"),[printer]); return Task.CompletedTask; });
        Assert(state.Settings.Authentication.Username=="printer");
    });
    await Test("Real HTTP digest challenges, valid credentials and replay rejection",async ()=>
    {
        Assert((await Send(Request(0x000b))).Ipp?.Code==0); // discovery is accessible before login
        var challenge=(await Send(Request(0x0004))).Http; Assert(challenge.StatusCode==HttpStatusCode.Unauthorized);
        var header=challenge.Headers.WwwAuthenticate.First(x=>x.ToString().Contains("SHA-256")).ToString();
        var nonce=Regex.Match(header,"nonce=\"([^\"]+)\"").Groups[1].Value; var target="/printers/"+printer.Id;
        string H(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var response=H($"{H("printer:"+Product.Realm+":fixture-password")}:{nonce}:00000001:http-fixture:auth:{H("POST:"+target)}");
        var auth=$"Digest username=\"printer\",realm=\"{Product.Realm}\",nonce=\"{nonce}\",uri=\"{target}\",algorithm=SHA-256,qop=auth,nc=00000001,cnonce=\"http-fixture\",response=\"{response}\"";
        Assert((await Send(Request(0x0004),auth:auth)).Ipp?.Code==0);
        Assert((await Send(Request(0x0004),auth:auth)).Http.StatusCode==HttpStatusCode.Unauthorized);
        state.Apply(new([selected],false,"printer"),[printer]);
    });
}
finally { await app.StopAsync(); await httpJobs.StopAsync(default); }

await Test("PDF renderer produces expected page dimensions without printing",async ()=>
{
    var file=Path.Combine(root,"fixture.pdf");
    var objects=new[]{"<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R] /Count 1 >>","<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 72] /Resources << >> /Contents 4 0 R >>"};
    var pdf=new StringBuilder("%PDF-1.4\n"); var offsets=new List<int>{0};
    foreach (var item in objects) { offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString())); pdf.Append($"{offsets.Count-1} 0 obj\n{item}\nendobj\n"); }
    const string content="q 1 0 0 rg 0 0 144 72 re f Q\n"; offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
    pdf.Append($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
    var xref=Encoding.ASCII.GetByteCount(pdf.ToString()); pdf.Append($"xref\n0 5\n0000000000 65535 f \n");
    foreach (var offset in offsets.Skip(1)) pdf.Append($"{offset:D10} 00000 n \n");
    pdf.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(file,pdf.ToString(),Encoding.ASCII);
    var work=Path.Combine(root,"render-pdf"); Directory.CreateDirectory(work); var pages=new List<string>();
    await WindowsPrintBackend.Prepare(file,"application/pdf",work,pages,default);
    Assert(pages.Count==1); using var image=new Bitmap(pages[0]); Assert(image.Width==600 && image.Height==300);
    Assert(image.GetPixel(300,150).R>240 && image.GetPixel(300,150).G<10);
});
foreach (var format in new[]{"image/png","image/jpeg"})
    await Test(format+" renderer and MIME mismatch detection",async ()=>
    {
        var file=Path.Combine(root,format.EndsWith("png") ? "fixture.png" : "fixture.jpg");
        using (var source=new Bitmap(32,16)) { using var graphics=Graphics.FromImage(source); graphics.Clear(Color.Blue); source.Save(file,format.EndsWith("png") ? ImageFormat.Png : ImageFormat.Jpeg); }
        var work=Path.Combine(root,"render-"+format[6..]); Directory.CreateDirectory(work); var pages=new List<string>();
        await WindowsPrintBackend.Prepare(file,format,work,pages,default);
        Assert(pages.Count==1); using var image=new Bitmap(pages[0]); Assert(image.Width==32 && image.Height==16);
        await Throws<InvalidDataException>(()=>WindowsPrintBackend.Prepare(file,"application/pdf",work,[],default));
    });
foreach (var apple in new[]{true,false})
    await Test((apple ? "URF" : "PWG")+" renders RGB channels in correct order",async ()=>
    {
        var file=Path.Combine(root,apple ? "fixture.urf" : "fixture.pwg"); var header=new byte[apple ? 32 : 1796];
        void N(int offset,int number)=>BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(offset),number);
        if (apple) { header[0]=24;header[1]=1;N(12,2);N(16,1);N(20,300); }
        else { N(276,300);N(280,300);N(372,2);N(376,1);N(384,8);N(388,24);N(392,6);N(400,19); }
        await using (var output=File.Create(file))
        { if (apple) output.Write([.."UNIRAST\0"u8.ToArray(),0,0,0,1]); else output.Write("RaS2"u8); output.Write(header); output.Write([0,255,255,0,0,0,0,255]); }
        var work=Path.Combine(root,apple ? "render-urf" : "render-pwg"); Directory.CreateDirectory(work); var pages=new List<string>();
        await WindowsPrintBackend.Prepare(file,apple ? "image/urf" : "image/pwg-raster",work,pages,default);
        using var image=new Bitmap(pages.Single()); Assert(image.GetPixel(0,0).R==255 && image.GetPixel(0,0).B==0); Assert(image.GetPixel(1,0).B==255);
    });
Console.WriteLine($"{count-failures}/{count} service tests passed; no system service, firewall changes or physical printing.");
return failures==0 ? 0 : 1;

sealed class FakeBackend : IPrintBackend
{
    public int Calls; public bool Fail; public string? LastMedia;
    public Task<int> PrintAsync(string filename,string format,PrinterInfo printer,PrintOptions options,string name,CancellationToken cancellation)
    { Interlocked.Increment(ref Calls); LastMedia=options.Media; return Fail ? Task.FromException<int>(new IOException("Fixture failure")) : Task.FromResult(2); }
}
sealed class GateStream : MemoryStream
{
    public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _read;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
    {
        if (_read) return 0; _read=true; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); buffer.Span[0]=42; return 1;
    }
}
