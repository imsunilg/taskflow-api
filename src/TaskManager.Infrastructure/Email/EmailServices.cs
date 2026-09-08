using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskManager.Application.Interfaces;

namespace TaskManager.Infrastructure.Email;

public class EmailOptions
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 25;
    public bool UseSsl { get; set; }
    public bool UseAuthentication { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "TaskFlow";
}

/// <summary>
/// Plain SMTP sender (System.Net.Mail). Supports the freesmtpservers.com relay used for local
/// dev: host smtp.freesmtpservers.com, port 25, no SSL, no auth. Never logs the message body.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.SmtpHost)
        && !string.IsNullOrWhiteSpace(_options.FromEmail);

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new EmailDeliveryException(
                "Email delivery is not configured. Set Email:Enabled=true and Email:SmtpHost / Email:FromEmail " +
                "(env vars Email__SmtpHost etc. or user-secrets).");
        }

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = _options.UseAuthentication
                ? new NetworkCredential(_options.Username, _options.Password)
                : CredentialCache.DefaultNetworkCredentials,
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName),
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsBodyHtml = true,
        };
        mail.To.Add(message.ToEmail);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            message.TextBody, null, "text/plain"));

        try
        {
            await client.SendMailAsync(mail, ct);
            _logger.LogInformation(
                "Sent '{Subject}' email to {Recipient} via {Host}:{Port}",
                message.Subject, Mask(message.ToEmail), _options.SmtpHost, _options.SmtpPort);
        }
        catch (SmtpException ex)
        {
            // The status code often tells us the relay rejected the From address / recipient.
            _logger.LogError(
                "SMTP delivery failed ({Status}) via {Host}:{Port} from {From}: {Reason}",
                ex.StatusCode, _options.SmtpHost, _options.SmtpPort, Mask(_options.FromEmail), ex.Message);
            throw new EmailDeliveryException(
                $"The SMTP relay {_options.SmtpHost} rejected the message ({ex.StatusCode}). " +
                "Verify it permits the configured From address and outbound delivery.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError("Email delivery failed via {Host}:{Port}: {Reason}", _options.SmtpHost, _options.SmtpPort, ex.Message);
            throw new EmailDeliveryException("Email could not be delivered. See server logs.", ex);
        }
    }

    private static string Mask(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***";
        return $"{email[0]}***{email[at..]}";
    }
}
