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
        if (!Configured(occurrence.Channel, secrets)) return new(DeliveryStatus.Failed, Loc.T("Canal désactivé ou configuration incomplète."));
        using var request = occurrence.Channel == ReminderChannel.Email ? Email(occurrence, secrets.SendGrid) : Telephone(occurrence, secrets.Twilio);
        try
        {
            using var response = await http.SendAsync(request, token);
            // Never log provider response bodies: they can echo addresses or credentials.
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode >= 500 ? DeliveryStatus.Unknown : DeliveryStatus.Failed, Loc.F("Prestataire : HTTP {0}. Aucune réémission automatique.", (int)response.StatusCode));
            if (occurrence.Channel == ReminderChannel.Email) return new(DeliveryStatus.Accepted, Loc.T("Accepté par SendGrid ; réception non confirmée."));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var sid = json.RootElement.GetProperty("sid").GetString();
            if (sid is null || !Sid(sid, occurrence.Channel == ReminderChannel.Call ? "CA" : "SM")) return new(DeliveryStatus.Unknown, Loc.T("Réponse du prestataire non reconnue."));
            return new(DeliveryStatus.Accepted, Loc.T("Accepté par Twilio ; suivi en cours."), sid);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(DeliveryStatus.Unknown, Loc.T("Résultat inconnu après interruption réseau ; aucune réémission automatique.")); }
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
            // Loc.F formats with the interface culture, so the spoken dates match the voice language.
            var name = r.AccountName.Length > 60 ? r.AccountName[..60] : r.AccountName;
            var text = Loc.F("Codex Tracker. {0}. {1} prévu le {2:dd MMMM à HH:mm}. Relevé du {3:dd MMMM à HH:mm}. Vérifiez votre compte dans Codex.",
                name, ReminderPlanner.Label(r.Kind), r.At.ToLocalTime(), r.ObservedAt.ToLocalTime());
            fields["Twiml"] = new XElement("Response", new XElement("Say", new XAttribute("language", Loc.Culture.Name), text)).ToString(SaveOptions.DisableFormatting);
            fields["TimeLimit"] = "60"; fields["Timeout"] = "20";
        }
        else
        {
            // Fixed ASCII text keeps every message within one GSM-7 segment, even for long account names.
            var kind = r.Kind switch { ResetKind.Weekly => Loc.T("Reset semaine"), ResetKind.Short => Loc.T("Reset 5h"), _ => Loc.T("Expiration reserve") };
            var name = Regex.Replace(r.AccountName, "[^a-zA-Z0-9@._-]", "");
            if (name.Length > 20) name = name[..20];
            fields["Body"] = Loc.F("Codex {0}: {1} le {2:dd/MM HH:mm zzz}. Releve {3:dd/MM HH:mm zzz}. Verifiez dans Codex.", name, kind, r.At.ToLocalTime(), r.ObservedAt.ToLocalTime());
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
                "delivered" => new(DeliveryStatus.Delivered, Loc.T("SMS livré selon Twilio."), sid),
                "completed" => new(DeliveryStatus.Completed, Loc.T("Appel terminé ; écoute non confirmée."), sid),
                "failed" or "undelivered" or "busy" or "no-answer" or "canceled" => new(DeliveryStatus.Failed, Loc.F("Twilio : {0}", status), sid),
                _ => null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
