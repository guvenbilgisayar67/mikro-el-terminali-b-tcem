using System.Drawing.Printing;
using Butcem.Yazdirma;

// "ButcemYazdirma.exe yazicilar" : bu bilgisayardaki yazıcı adlarını listeler (yönetim ekranına aynen yazılır).
if (args.Contains("yazicilar"))
{
    foreach (string ad in PrinterSettings.InstalledPrinters) Console.WriteLine(ad);
    return;
}

// Servis olarak çalışırken çalışma klasörü System32 olur; appsettings.json exe'nin yanından okunur.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = "ButcemYazdirma");
var ayar = builder.Configuration.GetSection("Ajan").Get<AjanAyarlari>()
           ?? throw new InvalidOperationException("appsettings.json içinde Ajan bölümü eksik.");
builder.Services.AddSingleton(ayar);
builder.Services.AddHostedService<Ajan>();
builder.Build().Run();
