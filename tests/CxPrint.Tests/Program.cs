using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CxPrint.Core;
using static CxPrint.Core.IppCodec;

var tests=new List<(string Name,Func<Task> Run)>();
void Test(string name,Action run)=>tests.Add((name,()=> { run(); return Task.CompletedTask; }));
void AsyncTest(string name,Func<Task> run)=>tests.Add((name,run));
void Assert(bool condition,string message="Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T:Exception
{ try { action(); } catch (T) { return; } throw new Exception("Expected "+typeof(T).Name); }
var a4=new MediaSize("iso_a4_210x297mm","A4",21000,29700,9);
var letter=new MediaSize("na_letter_8.5x11in","Letter",21590,27940,1);
var a3=new MediaSize("iso_a3_297x420mm","A3",29700,42000,8);
var printer=new PrinterInfo(Product.PrinterId("Fixture"),"Fixture","Fixture driver","TEST",true,true,[a4,letter,a3],a4.Key);
var shared=new SharedPrinter(printer.Id,printer.Name);
IppMessage Request(params IppAttribute[] attrs)=>new(2,0,0x000b,42,[Text(1,IppTag.Charset,"attributes-charset","utf-8"),Text(1,IppTag.Language,"attributes-natural-language","en"),..attrs]);

Test("Apple display names preserve legacy settings and fit a UTF-8 DNS-SD label",()=>
{
    var legacy=System.Text.Json.JsonSerializer.Deserialize<SharedPrinter>("{\"id\":\"test\",\"name\":\"HP Laser NS MFP 1005\"}",Product.Json)!;
    Assert(legacy.DisplayName is null && legacy.EffectiveDisplayName=="HP Laser NS MFP 1005- CX Printer");
    Assert((legacy with { DisplayName="  办公室.打印机😀  " }).EffectiveDisplayName=="办公室.打印机😀");
    Assert((legacy with { DisplayName="   " }).EffectiveDisplayName==legacy.EffectiveDisplayName);
    foreach (var name in new[]{new string('A',256),string.Concat(Enumerable.Repeat("财务😀",60))})
    {
        var display=SharedPrinter.DefaultDisplayName(name);
        Assert(Encoding.UTF8.GetByteCount(display)<=SharedPrinter.MaxDisplayNameBytes && display.EndsWith("- CX Printer"));
        Assert(!display.Contains('\ufffd'),"Default truncation split a Unicode rune.");
    }
    Assert(SharedPrinter.DefaultDisplayName(new string('A',70)+"1")!=SharedPrinter.DefaultDisplayName(new string('A',70)+"2"),"Long queue names collapsed to the same default name.");
    (legacy with { DisplayName=new string('A',63) }).ValidateDisplayName();
    (legacy with { DisplayName=new string('中',21) }).ValidateDisplayName();
    Throws<ArgumentException>(()=>(legacy with { DisplayName=new string('A',64) }).ValidateDisplayName());
    Throws<ArgumentException>(()=>(legacy with { DisplayName=new string('中',22) }).ValidateDisplayName());
    Throws<ArgumentException>(()=>(legacy with { DisplayName="财务\n打印机" }).ValidateDisplayName());
    Throws<ArgumentException>(()=>new BridgeSettings { Printers=[shared with { DisplayName="Office" },new(Product.PrinterId("Other"),"Other",DisplayName:"office")] }.Validate());
});
AsyncTest("IPP names use the configured Apple name without changing printer identity",async ()=>
{
    foreach (var displayName in new string?[]{null,"财务室.打印机😀"})
    {
        var renamed=shared with { DisplayName=displayName };
        var attrs=PrinterAttributes.Build(printer,renamed,"ipp://localhost/printers/"+printer.Id,false,10,0,1);
        var reply=await ReadAsync(new MemoryStream(Write(Response(Request(),0,attrs))));
        Assert(reply.Text("printer-name")==renamed.EffectiveDisplayName && reply.Text("printer-info")==renamed.EffectiveDisplayName);
        Assert(reply.Text("printer-uri-supported")!.EndsWith("/"+printer.Id));
        Assert(reply.Text("printer-uuid")=="urn:uuid:"+PrinterAttributes.StableUuid(printer.Id));
    }
});
Test("A4, A3 and Letter keywords remain distinct",()=>
{
    Assert(MediaSize.Keyword(21000,29700)==a4.Key); Assert(MediaSize.Keyword(21590,27940)==letter.Key);
    Assert(MediaSize.Keyword(29700,42000)==a3.Key); Assert(MediaSize.Keyword(21006,29693)==a4.Key);
    var old=CultureInfo.CurrentCulture; CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");
    try { Assert(MediaSize.Keyword(12345,23456)=="custom_cx_123.45x234.56mm"); }
    finally { CultureInfo.CurrentCulture=old; }
});
AsyncTest("Nested media-col round trip preserves exact dimensions and driver selection",async ()=>
{
    var attrs=PrinterAttributes.Build(printer,shared,"ipp://localhost/printers/test",false,10,0,1);
    var bytes=Write(Response(Request(),0,attrs));
    var decoded=await ReadAsync(new MemoryStream(bytes));
    Assert(decoded.RequestId==42); Assert(decoded.Text("media-default")==a4.Key);
    var media=decoded.Find("media-col-database")!.Values;
    Assert(media.Count==3);
    Assert(media.Select(v=>v.Member("media-size")!.Values[0].Member("x-dimension")!.Values[0].Integer).SequenceEqual(new[]{21000,21590,29700}));
    var selected=PrintOptions.From(Request(Attr(2,"media-col",PrinterAttributes.MediaCollection(a3))),printer,shared);
    Assert(selected.Media==a3.Key); Assert(printer.Media.Single(m=>m.Key==selected.Media).WindowsPaperId==8);
});
Test("Unsupported paper never silently falls back to Letter",()=>
{
    Throws<UnsupportedOptionException>(()=>PrintOptions.From(Request(Text(2,IppTag.Keyword,"media","invalid")),printer,shared));
    var unknown=IppValue.Col(Attr(0,"media-size",IppValue.Col(Int(0,"x-dimension",9999),Int(0,"y-dimension",12345))));
    Throws<UnsupportedOptionException>(()=>PrintOptions.From(Request(Attr(2,"media-col",unknown)),printer,shared));
});
Test("Per-printer default override is respected",()=>
{
    var attrs=PrinterAttributes.Build(printer,shared with { DefaultMedia=a3.Key },"ipp://localhost/test",false,1,0,1);
    Assert(attrs.Single(a=>a.Name=="media-default").Values[0].Text==a3.Key);
    Assert(PrintOptions.From(Request(),printer,shared with { DefaultMedia=a3.Key }).Media==a3.Key);
});
Test("Monochrome and simplex printers do not overclaim capabilities",()=>
{
    var simple=printer with { Color=false,Duplex=false,Media=[a4] };
    var attrs=PrinterAttributes.Build(simple,shared,"ipp://localhost/test",true,1,0,1);
    Assert(attrs.Single(a=>a.Name=="sides-supported").Values.Count==1);
    Assert(attrs.Single(a=>a.Name=="media-supported").Values.Count==1);
    Throws<UnsupportedOptionException>(()=>PrintOptions.From(Request(Text(2,IppTag.Keyword,"sides","two-sided-long-edge")),simple,shared));
    Throws<UnsupportedOptionException>(()=>PrintOptions.From(Request(Text(2,IppTag.Keyword,"print-color-mode","color")),simple,shared));
    Throws<UnsupportedOptionException>(()=>PrintOptions.From(Request(Int(2,"copies",0)),simple,shared));
});
AsyncTest("Document data is not consumed by the IPP attribute parser",async ()=>
{
    var bytes=Write(Request()); using var stream=new MemoryStream([..bytes,..Encoding.ASCII.GetBytes("%PDF-fixture")]);
    await ReadAsync(stream); Assert(stream.Position==bytes.Length); Assert(stream.ReadByte()=='%');
});
AsyncTest("Repeated job groups round trip",async ()=>
{
    var data=Write(Response(Request(),0,[Int(2,"job-id",1),new(2,0,"",[]),Int(2,"job-id",2)]));
    var parsed=await ReadAsync(new MemoryStream(data)); Assert(parsed.Attributes.Count(a=>a.Name=="job-id")==2);
});
AsyncTest("Truncated and malformed IPP messages fail closed",async ()=>
{
    var bytes=Write(Request(Attr(2,"media-col",PrinterAttributes.MediaCollection(a4))));
    for (var length=0;length<bytes.Length;length++)
    {
        try { await ReadAsync(new MemoryStream(bytes[..length])); throw new Exception("Truncation accepted at "+length); }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException) { }
    }
});
AsyncTest("Bounded parser fuzz corpus completes without an unhandled exception",async ()=>
{
    var random=new Random(52781);
    for (var i=0;i<500;i++)
    {
        var data=new byte[random.Next(0,512)]; random.NextBytes(data);
        try { await ReadAsync(new MemoryStream(data)); }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or OverflowException) { }
    }
});

foreach (var algorithm in new[]{"SHA-256","MD5"})
{
    Test(algorithm+" digest: valid password, wrong target, wrong password, replay, expiry",()=>
    {
        var clock=new TestClock(); var digest=new DigestAuthentication(clock); var secret=DigestSecret.Create("printer","password-for-fixture");
        var nonce=Regex.Match(digest.Challenge(algorithm),"nonce=\"([^\"]+)\"").Groups[1].Value;
        string Make(string target,string password,string count="00000001")
        {
            string H(string x)=>Convert.ToHexString(algorithm=="MD5" ? MD5.HashData(Encoding.UTF8.GetBytes(x)) : SHA256.HashData(Encoding.UTF8.GetBytes(x))).ToLowerInvariant();
            var answer=H($"{H("printer:"+Product.Realm+":"+password)}:{nonce}:{count}:fixture-client:auth:{H("POST:"+target)}");
            return $"Digest username=\"printer\",realm=\"{Product.Realm}\",nonce=\"{nonce}\",uri=\"{target}\",algorithm={algorithm},qop=auth,nc={count},cnonce=\"fixture-client\",response=\"{answer}\"";
        }
        const string target="/printers/test";
        Assert(!digest.Verify(Make(target,"wrong-password"),"POST",target,"printer",secret));
        Assert(!digest.Verify(Make(target,"password-for-fixture"),"POST","/other","printer",secret));
        var valid=Make(target,"password-for-fixture"); Assert(digest.Verify(valid,"POST",target,"printer",secret));
        Assert(!digest.Verify(valid,"POST",target,"printer",secret),"Replay accepted");
        Assert(digest.Verify(Make(target,"password-for-fixture","00000002"),"POST",target,"printer",secret));
        clock.Now=clock.Now.AddMinutes(6);
        Assert(!digest.Verify(Make(target,"password-for-fixture","00000003"),"POST",target,"printer",secret));
    });
}
Test("Credential validation rejects empty or ambiguous identities",()=>
{
    Throws<ArgumentException>(()=>DigestSecret.Create("a:b","password"));
    Throws<ArgumentException>(()=>DigestSecret.Create("printer",""));
    Throws<ArgumentException>(()=>DigestSecret.Create("printer","line\nbreak"));
});

byte[] Raster(bool apple,int width,int height,int bpp,byte[] encoded,int pageCount=1)
{
    var header=new byte[apple ? 32 : 1796];
    void N(int offset,int value)=>BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(offset),value);
    if (apple) { header[0]=(byte)bpp; header[1]=(byte)(bpp==24 ? 1 : 0); N(12,width);N(16,height);N(20,300); }
    else { N(276,300);N(280,300);N(372,width);N(376,height);N(384,8);N(388,bpp);N(392,width*bpp/8);N(396,0);N(400,bpp==24 ? 19 : 18); }
    using var output=new MemoryStream();
    if (apple) { output.Write("UNIRAST\0"u8); var pages=new byte[4];BinaryPrimitives.WriteInt32BigEndian(pages,pageCount);output.Write(pages); }
    else output.Write("RaS2"u8);
    output.Write(header);output.Write(encoded);return output.ToArray();
}
foreach (var apple in new[]{true,false})
{
    Test((apple ? "URF" : "PWG")+" row repeats, RGB literals and white-run decoding",()=>
    {
        using var file=new MemoryStream(Raster(apple,2,2,24,[1,255,255,0,0,0,0,255]));
        var reader=new RasterReader(file); var page=reader.NextPage()!; var rows=new List<byte[]>();
        page.DecodeRows((_,row)=>rows.Add(row.ToArray()));
        Assert(rows.Count==2 && rows.All(r=>r.SequenceEqual(new byte[]{255,0,0,0,0,255})));
        Assert(reader.NextPage() is null);
        using var white=new MemoryStream(Raster(apple,2,1,8,[0,128]));
        new RasterReader(white).NextPage()!.DecodeRows((_,r)=>Assert(r.SequenceEqual(new byte[]{255,255})));
    });
    Test((apple ? "URF" : "PWG")+" rejects oversized pages, RLE overflows and truncation",()=>
    {
        Throws<InvalidDataException>(()=>new RasterReader(new MemoryStream(Raster(apple,20000,20000,24,[]))).NextPage());
        Throws<InvalidDataException>(()=>new RasterReader(new MemoryStream(Raster(apple,2,1,8,[1,128]))).NextPage()!.DecodeRows((_,_)=>{}));
        Throws<InvalidDataException>(()=>new RasterReader(new MemoryStream(Raster(apple,2,1,8,[0,2,0]))).NextPage()!.DecodeRows((_,_)=>{}));
        Throws<EndOfStreamException>(()=>new RasterReader(new MemoryStream(Raster(apple,2,1,8,[0,255,0]))).NextPage()!.DecodeRows((_,_)=>{}));
    });
}
Test("Invalid Apple page count is detected",()=>
{
    var reader=new RasterReader(new MemoryStream(Raster(true,1,1,8,[0,0,0],2)));
    reader.NextPage()!.DecodeRows((_,_)=>{}); Throws<InvalidDataException>(()=>reader.NextPage());
});

var failures=0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS "+test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL "+test.Name+": "+ex); }
}
Console.WriteLine($"{tests.Count-failures}/{tests.Count} passed");
return failures==0 ? 0 : 1;

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }=new(2026,11,17,0,0,0,TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow()=>Now;
}
