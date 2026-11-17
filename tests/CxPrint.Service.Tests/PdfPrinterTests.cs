using System.Drawing;
using System.Drawing.Imaging;
using CxPrint.Service;
using CxPrint.Windows;
using Windows.Data.Pdf;

internal static class PdfPrinterTests
{
    // Opt-in: exercises the real Microsoft virtual printer, never a physical queue.
    public static Task RunAsync(Func<string,Func<Task>,Task> test,string root)=>test(
        "Microsoft Print to PDF creates a valid file without a save dialog",async ()=>
        {
            var printer=await Task.Run(()=>PrinterCatalog.Read("Microsoft Print to PDF")).WaitAsync(TimeSpan.FromSeconds(45));
            if (!printer.IsPdfTest || !printer.Usable) throw new Exception("Microsoft Print to PDF is not available for testing: "+printer.Error);
            var paper=printer.Media.First(m=>m.Width==21000 && m.Height==29700);
            var work=Path.GetFullPath(Path.Combine(root,"real-pdf-test")); Directory.CreateDirectory(work);
            var imagePath=Path.Combine(work,"source.png");
            using (var image=new Bitmap(240,120))
            {
                using var graphics=Graphics.FromImage(image); graphics.Clear(Color.CornflowerBlue);
                image.Save(imagePath,ImageFormat.Png);
            }
            var output=Path.Combine(work,"TestOutput");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var pages=await new WindowsPrintBackend().PrintAsync(imagePath,"image/png",printer,new(paper.Key),
                "CX Printer PDF regression",timeout.Token,output).WaitAsync(TimeSpan.FromSeconds(65));
            var file=Directory.EnumerateFiles(output,"*.pdf").Single();
            using var input=File.OpenRead(file);
            using var randomAccess=input.AsRandomAccessStream();
            var pdf=await PdfDocument.LoadFromStreamAsync(randomAccess);
            if (pages!=1 || pdf.PageCount!=1) throw new Exception("The PDF test lost or duplicated a page.");
            using var page=pdf.GetPage(0);
            if (Math.Abs(page.Size.Width*25.4/96-210)>1 || Math.Abs(page.Size.Height*25.4/96-297)>1)
                throw new Exception("The virtual PDF printer did not preserve A4 dimensions.");
            if (Directory.Exists(Path.Combine(work,"rendered"))) throw new Exception("Temporary rendered pages were not cleaned up.");
            Console.WriteLine("PDF test output: "+Path.GetFullPath(file));
        });
}
