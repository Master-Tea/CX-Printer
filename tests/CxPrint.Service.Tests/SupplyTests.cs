using System.Xml;
using CxPrint.Core;
using CxPrint.Windows;

internal static class SupplyTests
{
    public const string Response="""
        <bidi:Get xmlns:bidi="http://schemas.microsoft.com/windows/2005/03/printing/bidi">
          <Query schema="\Printer.Consumables">
            <Schema name="\Printer.Consumables.Black:Model"><BIDI_STRING>黑色硒鼓</BIDI_STRING></Schema>
            <Schema name="\Printer.Consumables.Black:Level"><BIDI_INT>72</BIDI_INT></Schema>
            <Schema name="\Printer.Consumables.Black:Type"><BIDI_STRING>Toner</BIDI_STRING></Schema>
            <Schema name="\Printer.Consumables.Black:Color"><BIDI_STRING>Black</BIDI_STRING></Schema>
            <Schema name="\Printer.Consumables.Black:Installed"><BIDI_BOOL>true</BIDI_BOOL></Schema>
            <Schema name="\Printer.Consumables.Cyan:Type"><BIDI_STRING>Ink</BIDI_STRING></Schema>
            <Schema name="\Printer.Consumables.Cyan:Color"><BIDI_STRING>Cyan</BIDI_STRING></Schema>
            <Schema name="\Printer.Consumables.Cyan:Level"><BIDI_INT>-1</BIDI_INT></Schema>
            <Schema name="\Printer.Consumables.Missing:Installed"><BIDI_BOOL>false</BIDI_BOOL></Schema>
            <Schema name="\Printer.Consumables.Missing:Level"><BIDI_INT>100</BIDI_INT></Schema>
            <Schema name="\Printer.Consumables.Waste:Type"><BIDI_STRING>WasteInk</BIDI_STRING></Schema>
            <Schema name="\Printer.Consumables.Waste:Level"><BIDI_INT>95</BIDI_INT></Schema>
            <Schema name="\Printer.Configuration.Duplex:Installed"><BIDI_BOOL>true</BIDI_BOOL></Schema>
          </Query>
        </bidi:Get>
        """;
    private static void Assert(bool condition,string message)
    { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10,timeout.Token);
    }
    public static async Task RunAsync(Func<string,Func<Task>,Task> test,PrinterInfo fixture)
    {
        await test("Windows bidi consumes actual schema paths, typed levels and missing cartridges",()=>
        {
            foreach (var xml in new[]{Response,Response.Replace("http://schemas.microsoft.com/","https://schemas.microsoft.com/")})
            {
                var supplies=PrinterSupplyReader.ParseResponse(xml);
                Assert(supplies.Items.Count==4,"Non-consumables were returned as supplies.");
                var black=supplies.Items.Single(s=>s.Id=="Black");
                Assert(black.Percent==72 && black.Type=="toner" && black.Color=="black" && black.Name.Contains("黑色硒鼓"),"Black cartridge metadata was lost.");
                Assert(supplies.Items.Single(s=>s.Id=="Cyan").MarkerLevel==-2,"Bidi unknown was reported as empty or full.");
                Assert(supplies.Items.Single(s=>s.Id=="Missing").MarkerLevel==-1,"An absent cartridge was reported as full.");
                Assert(supplies.Items.Single(s=>s.Id=="Waste") is { IsWaste:true,Percent:95 },"Waste capacity was inverted.");
            }
            foreach (var value in new[]{"-10","101","2147483648","not-a-number"})
                Assert(PrinterSupplyReader.ParseResponse(Response.Replace("<BIDI_INT>72</BIDI_INT>",$"<BIDI_INT>{value}</BIDI_INT>")).Items.Single(s=>s.Id=="Black").Percent is null,"Invalid percentages were clamped to a fake reading.");
            Assert(PrinterSupplyReader.ParseResponse(Response.Replace("<BIDI_INT>72</BIDI_INT>","<BIDI_STRING>72</BIDI_STRING>")).Items.Single(s=>s.Id=="Black").Percent is null,"A mistyped value was accepted.");
            var error=PrinterSupplyReader.ParseResponse("<bidi:Get xmlns:bidi=\"http://schemas.microsoft.com/windows/2005/03/printing/bidi\"><Query schema=\"\\Printer.Consumables\"><Error>50</Error></Query></bidi:Get>");
            Assert(error.Items.Count==0,"Unsupported bidi data fabricated cartridges.");
            try { PrinterSupplyReader.ParseResponse("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///not-used'>]><x>&x;</x>"); throw new Exception("Expected prohibited DTD."); }
            catch (XmlException) { }
            return Task.CompletedTask;
        });
        await test("Native Windows bidi gracefully handles an unavailable queue and a virtual PDF queue",()=>
        {
            var missing=PrinterSupplyReader.Read("CX Printer unavailable "+Guid.NewGuid().ToString("N"));
            Assert(missing.Items.Count==0 && missing.Error is not null,"An unavailable queue returned fictional supplies.");
            var pdf=PrinterSupplyReader.Read("Microsoft Print to PDF");
            Assert(pdf.Items.Count==0,"A virtual PDF printer returned ink or toner.");
            return Task.CompletedTask;
        });
        await test("Slow supply readers use at most two calls and removed queues stay removed",async ()=>
        {
            using var release=new ManualResetEventSlim();
            var calls=0; var done=0;
            var monitor=new PrinterSupplyMonitor(_=>
            {
                Interlocked.Increment(ref calls);
                release.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Increment(ref done);
                return PrinterSupplyReader.ParseResponse(Response);
            });
            var printers=Enumerable.Range(0,3).Select(i=>fixture with { Id=Product.PrinterId("Supply queue "+i),Name="Supply queue "+i }).ToArray();
            try
            {
                monitor.RequestRefresh(printers);
                await Until(()=>Volatile.Read(ref calls)==2);
                for (var i=0;i<20;i++) monitor.RequestRefresh(printers);
                Assert(calls==2,"Polling started duplicate or unbounded native reads.");
                Assert(monitor.Get(printers[0].Id) is null,"An in-flight read blocks or fabricates data.");
                monitor.RequestRefresh([]); release.Set();
                await Until(()=>Volatile.Read(ref done)==2);
                Assert(printers.All(p=>monitor.Get(p.Id) is null),"A late native reply resurrected a removed queue.");
                monitor.RequestRefresh([fixture with { Driver="Microsoft Print To PDF" }]);
                Assert(calls==2,"A virtual PDF printer was queried for toner.");
            }
            finally { release.Set(); }
        });
        await test("Removing and readding a queue does not duplicate its unfinished native read",async ()=>
        {
            using var release=new ManualResetEventSlim();
            var calls=0;
            var monitor=new PrinterSupplyMonitor(_=>
            {
                Interlocked.Increment(ref calls); release.Wait(TimeSpan.FromSeconds(10));
                return PrinterSupplyReader.ParseResponse(Response);
            });
            try
            {
                monitor.RequestRefresh([fixture]); await Until(()=>Volatile.Read(ref calls)==1);
                monitor.RequestRefresh([]); monitor.RequestRefresh([fixture]);
                await Task.Delay(50);
                Assert(calls==1 && monitor.Get(fixture.Id) is null,"A removed queue lost track of its in-flight driver call.");
                release.Set();
                for (var i=0;i<100 && monitor.Get(fixture.Id) is null;i++)
                { monitor.RequestRefresh([fixture]); await Task.Delay(10); }
                Assert(monitor.Get(fixture.Id)?.Items.Count==4 && calls==2,"The readded queue did not acquire a new reading after the old call finished.");
            }
            finally { release.Set(); }
        });
        await test("Supply cache refreshes independently, expires stalled readings and clears driver failures",async ()=>
        {
            using var release=new ManualResetEventSlim();
            var clock=new SupplyClock { Now=DateTimeOffset.UtcNow }; var calls=0;
            var monitor=new PrinterSupplyMonitor(_=>
            {
                if (Interlocked.Increment(ref calls)==1) return PrinterSupplyReader.ParseResponse(Response);
                release.Wait(TimeSpan.FromSeconds(10)); throw new IOException("fixture failure");
            },clock);
            try
            {
                monitor.RequestRefresh([fixture]);
                await Until(()=>monitor.Get(fixture.Id)?.Items.Count==4);
                var initial=monitor.Get(fixture.Id)!;
                monitor.RequestRefresh([fixture]); Assert(calls==1,"Supply data did not use the one minute cache.");
                clock.Now=clock.Now.AddSeconds(61); monitor.RequestRefresh([fixture]);
                await Until(()=>Volatile.Read(ref calls)==2);
                Assert(monitor.Get(fixture.Id)!.Items.Count==4,"An active refresh erased fresh cached data.");
                clock.Now=clock.Now.AddSeconds(61);
                Assert(monitor.Get(fixture.Id) is { Items.Count:0,Error:not null },"Stalled data remained visible beyond its lifetime.");
                for (var i=0;i<20;i++) monitor.RequestRefresh([fixture]);
                Assert(calls==2,"An expired reading started duplicate native work.");
                release.Set();
                await Until(()=>monitor.Get(fixture.Id)?.Error?.StartsWith("读取耗材信息失败")==true);
                Assert(monitor.Get(fixture.Id)!.Items.Count==0,"A failed driver retained a stale percentage.");
                Assert(initial.Items[0].Percent==72,"Refreshing mutated an earlier immutable snapshot.");
            }
            finally { release.Set(); }
        });
    }
    private sealed class SupplyClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
