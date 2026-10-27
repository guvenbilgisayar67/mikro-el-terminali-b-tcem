using System.Net;
using System.Net.Mail;

namespace Butcem.Api.Infrastructure;

/// <summary>
/// SMTP ile e-posta (sipariş gönderimi). Ayarlar "Eposta" bölümünde: Sunucu, Port, Ssl, Kullanici, Sifre, Gonderen, GonderenAd.
/// Gmail için "uygulama şifresi", Yandex / Outlook / firma sunucusu için kendi SMTP bilgileri kullanılır.
/// </summary>
public static class Eposta
{
    public static bool Hazir(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["Eposta:Sunucu"]) && !string.IsNullOrWhiteSpace(config["Eposta:Gonderen"]);

    public static void Gonder(IConfiguration config, string alici, string konu, string metin, byte[] ek, string ekAdi)
    {
        if (!Hazir(config))
            throw new IslemHatasi("E-posta ayarları yapılmamış (appsettings.json, Eposta bölümü). WhatsApp ya da PDF ile gönderebilirsiniz.");
        using var mesaj = new MailMessage
        {
            From = new MailAddress(config["Eposta:Gonderen"]!, config["Eposta:GonderenAd"] ?? config["Firma:Ad"] ?? "Bütçem"),
            Subject = konu,
            Body = metin.Replace("*", ""),
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8,
        };
        mesaj.To.Add(alici);
        mesaj.Attachments.Add(new Attachment(new MemoryStream(ek), ekAdi, "application/pdf"));
        using var smtp = new SmtpClient(config["Eposta:Sunucu"], config.GetValue("Eposta:Port", 587))
        {
            EnableSsl = config.GetValue("Eposta:Ssl", true),
            Timeout = 30000,
        };
        if (!string.IsNullOrEmpty(config["Eposta:Kullanici"]))
            smtp.Credentials = new NetworkCredential(config["Eposta:Kullanici"], config["Eposta:Sifre"]);
        try { smtp.Send(mesaj); }
        catch (SmtpException ex) { throw new IslemHatasi($"E-posta gönderilemedi: {ex.Message}"); }
    }
}
