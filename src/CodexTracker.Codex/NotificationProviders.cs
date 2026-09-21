using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed class NotificationProviders(HttpClient http)
{
    private static bool Sid(string value, string prefix) => Regex.IsMatch(value, "^" + prefix + "[a-fA-F0-9]{32}$");
    private static bool Phone(string value) => Regex.IsMatch(value, @"^\+[1-9]\d{7,14}$");
    public static bool Configured(ReminderChannel channel, NotificationSecrets secrets) => channel switch
    {
        ReminderChannel.Windows => true,
        ReminderChannel.Email => secrets.SendGrid.Enabled && !string.IsNullOrWhiteSpace(secrets.SendGrid.ApiKey) && !secrets.SendGrid.ApiKey.Any(char.IsWhiteSpace) && Mail(secrets.SendGrid.From) && Mail(secrets.SendGrid.To),
        _ => secrets.Twilio.Enabled && Sid(secrets.Twilio.AccountSid, "AC") && Sid(secrets.Twilio.KeySid, "SK") && !string.IsNullOrWhiteSpace(secrets.Twilio.Secret)
             && !secrets.Twilio.Secret.Any(char.IsWhiteSpace) && Phone(secrets.Twilio.To) && (channel == ReminderChannel.Call ? Phone(secrets.Twilio.CallFrom) : Phone(secrets.Twilio.SmsFrom) || Regex.IsMatch(secrets.Twilio.SmsFrom, @"^(?=.*[a-zA-Z])[a-zA-Z0-9 ]{1,11}$"))
    };
    private static bool Mail(string value) => System.Net.Mail.MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;
    private static HttpRequestMessage TwilioRequest(TwilioSettings settings, HttpMethod method, string resource)
    {
        var request = new HttpRequestMessage(method, $"https://api.twilio.com/2010-04-01/Accounts/{settings.AccountSid}/{resource}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(settings.KeySid + ":" + settings.Secret)));
        return request;
    }
    public async Task<DeliveryResult> SendAsync(ReminderOccurrence occurrence, NotificationSecrets secrets, CancellationToken token)
    {
        if (!Configured(occurrence.Channel, secrets)) return new(DeliveryStatus.Failed, "Canal désactivé ou configuration incomplète.");
        using var request = occurrence.Channel == ReminderChannel.Email ? Email(occurrence, secrets.SendGrid) : Telephone(occurrence, secrets.Twilio);
        try
        {
            using var response = await http.SendAsync(request, token);
            // Never log provider response bodies: they can echo addresses or credentials.
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode >= 500 ? DeliveryStatus.Unknown : DeliveryStatus.Failed, $"Prestataire : HTTP {(int)response.StatusCode}. Aucune réémission automatique.");
            if (occurrence.Channel == ReminderChannel.Email) return new(DeliveryStatus.Accepted, "Accepté par SendGrid ; réception non confirmée.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var sid = json.RootElement.GetProperty("sid").GetString();
            if (sid is null || !Sid(sid, occurrence.Channel == ReminderChannel.Call ? "CA" : "SM")) return new(DeliveryStatus.Unknown, "Réponse du prestataire non reconnue.");
            return new(DeliveryStatus.Accepted, "Accepté par Twilio ; suivi en cours.", sid);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(DeliveryStatus.Unknown, "Résultat inconnu après interruption réseau ; aucune réémission automatique."); }
    }
    private static HttpRequestMessage Email(ReminderOccurrence r, SendGridSettings settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = JsonContent.Create(new { personalizations = new[] { new { to = new[] { new { email = settings.To } } } },
            from = new { email = settings.From, name = "Codex Tracker" }, subject = "Codex Tracker · " + ReminderPlanner.Label(r.Kind),
            content = new[] { new { type = "text/plain", value = ReminderPlanner.Body(r) } } });
        return request;
    }
    private static HttpRequestMessage Telephone(ReminderOccurrence r, TwilioSettings settings)
    {
        var call = r.Channel == ReminderChannel.Call;
        var request = TwilioRequest(settings, HttpMethod.Post, call ? "Calls.json" : "Messages.json");
        var fields = new Dictionary<string, string> { ["To"] = settings.To, ["From"] = call ? settings.CallFrom : settings.SmsFrom };
        if (call)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var name = r.AccountName.Length > 60 ? r.AccountName[..60] : r.AccountName;
            var text = $"Codex Tracker. {name}. {ReminderPlanner.Label(r.Kind)} prévu le {r.At.ToLocalTime().ToString("dd MMMM à HH:mm", culture)}. Relevé du {r.ObservedAt.ToLocalTime().ToString("dd MMMM à HH:mm", culture)}. Vérifiez votre compte dans Codex.";
            fields["Twiml"] = new XElement("Response", new XElement("Say", new XAttribute("language", "fr-FR"), text)).ToString(SaveOptions.DisableFormatting);
            fields["TimeLimit"] = "60"; fields["Timeout"] = "20";
        }
        else
        {
            // Fixed ASCII text keeps every message within one GSM-7 segment, even for long account names.
            var kind = r.Kind switch { ResetKind.Weekly => "Reset semaine", ResetKind.Short => "Reset 5h", _ => "Expiration reserve" };
            var name = Regex.Replace(r.AccountName, "[^a-zA-Z0-9@._-]", "");
            if (name.Length > 20) name = name[..20];
            fields["Body"] = $"Codex {name}: {kind} le {r.At.ToLocalTime():dd/MM HH:mm zzz}. Releve {r.ObservedAt.ToLocalTime():dd/MM HH:mm zzz}. Verifiez dans Codex.";
        }
        request.Content = new FormUrlEncodedContent(fields); return request;
    }
    public async Task<DeliveryResult?> PollAsync(ReminderDelivery delivery, NotificationSecrets secrets, CancellationToken token)
    {
        if (delivery.ProviderId is not { } sid || !Configured(delivery.Occurrence.Channel, secrets)) return null;
        var call = delivery.Occurrence.Channel == ReminderChannel.Call;
        if (!Sid(sid, call ? "CA" : "SM")) return null;
        using var request = TwilioRequest(secrets.Twilio, HttpMethod.Get, $"{(call ? "Calls" : "Messages")}/{sid}.json");
        try
        {
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var status = json.RootElement.GetProperty("status").GetString();
            return status switch {
                "delivered" => new(DeliveryStatus.Delivered, "SMS livré selon Twilio.", sid),
                "completed" => new(DeliveryStatus.Completed, "Appel terminé ; écoute non confirmée.", sid),
                "failed" or "undelivered" or "busy" or "no-answer" or "canceled" => new(DeliveryStatus.Failed, "Twilio : " + status, sid),
                _ => null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
