using System.Net.Mail;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Administration;

/// <summary>Closed, non-executable business-email language with parsed inert HTML and no content-resource variables.</summary>
public static class BusinessEmailRules
{
    /// <summary>Branding displayed inside message subject/body, not the ACS verified sender's display name.</summary>
    public const string BrandKey = "email.message-brand";

    /// <summary>Single response mailbox; does not change the service sender or organizer.</summary>
    public const string ReplyToKey = "email.reply-to";

    private const string CalendarGuidance = "Calendar clients may require acceptance of updates. Declining in Outlook does not change attendance; leave in Sidequest.";
    private static readonly HashSet<string> Elements = ["p", "br", "strong", "em", "ul", "ol", "li", "h2", "blockquote"];
    private static readonly HashSet<string> Variables = ["Brand", "Summary", "CalendarGuidance"];
    private static readonly IReadOnlyDictionary<NotificationKind, string> Keys = new Dictionary<NotificationKind, string>
    {
        [NotificationKind.QuestPublished] = "quest.published",
        [NotificationKind.EventInvitation] = "event.invitation",
        [NotificationKind.QuestInvitation] = "quest.invitation",
        [NotificationKind.MembershipRequested] = "event.membership-requested",
        [NotificationKind.MembershipDecided] = "event.membership-decided",
        [NotificationKind.MembershipAdded] = "event.membership-added",
        [NotificationKind.AccessRemoved] = "access.removed",
        [NotificationKind.Joined] = "quest.joined",
        [NotificationKind.Left] = "quest.left",
        [NotificationKind.AttendeeRemoved] = "quest.attendee-removed",
        [NotificationKind.QuestUpdated] = "quest.updated",
        [NotificationKind.QuestSuspended] = "quest.suspended",
        [NotificationKind.QuestReinstated] = "quest.reinstated",
        [NotificationKind.QuestCancelled] = "quest.cancelled",
        [NotificationKind.EventCancelled] = "event.cancelled",
        [NotificationKind.Reminder] = "quest.reminder",
        [NotificationKind.OwnershipChanged] = "ownership.changed",
        [NotificationKind.SuspendedQuestEdited] = "quest.moderation-update",
        [NotificationKind.BulkCompleted] = "event.bulk-completed"
    };

    /// <summary>Returns the closed list of administrator-editable template keys, independent of enum renaming.</summary>
    public static IReadOnlyList<string> TemplateKeys => Keys.Values.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Resolves a known business trigger without guessing from arbitrary template keys.</summary>
    /// <param name="kind">Captured business notification kind.</param>
    /// <returns>The stable template key.</returns>
    /// <exception cref="DomainException">The kind is not supported.</exception>
    public static string KeyFor(NotificationKind kind) => Keys.TryGetValue(kind, out var key) ? key : throw Invalid("Unknown notification kind.");

    /// <summary>Returns compiled wording with mandatory safe summary and calendar guidance tokens.</summary>
    /// <param name="key">Allowlisted category.</param>
    /// <returns>Revision-zero inert HTML and plain text templates.</returns>
    /// <exception cref="DomainException">The key is unknown.</exception>
    public static EmailTemplate Default(string key)
    {
        RequireKey(key);
        return new(key, 0, "{{Brand}} notification", "<p><strong>{{Brand}}</strong></p><p>{{Summary}}</p><p>{{CalendarGuidance}}</p>",
            "{{Brand}}\n{{Summary}}\n{{CalendarGuidance}}");
    }

    /// <summary>Validates and normalizes only allowlisted nonsecret settings; accepted notification defaults are not editable.</summary>
    /// <param name="key">Exact brand or reply-to key.</param>
    /// <param name="value">Submitted nonsecret value; empty reply-to removes that header.</param>
    /// <returns>Trimmed validated value.</returns>
    /// <exception cref="DomainException">Unknown key, control characters, overlong brand or invalid mailbox.</exception>
    public static string ValidateSetting(string key, string value)
    {
        if (value?.Any(IsHeaderControl) == true)
            throw Invalid("Business settings cannot contain control characters.");
        value = value?.Trim() ?? "";
        VerifyCharacters(value);
        if (key == BrandKey && value.Length is >= 1 and <= 80)
            return value;
        if (key == ReplyToKey && (value.Length == 0 || IsMailbox(value)))
            return value;
        throw Invalid(key is BrandKey or ReplyToKey
            ? "Use a 1–80 character message brand or one valid reply-to mailbox without a display name."
            : "That setting is not administrator-editable. Sender, organizer, credentials and mandatory policies are deployment-controlled.");
    }

    /// <summary>Validates header tokens and parses strict XHTML; rejects all attributes, links, styles, scripts, namespaces and declarations.</summary>
    /// <param name="template">Submitted or stored override; database values are not implicitly trusted.</param>
    /// <exception cref="DomainException">A field, token or HTML construct violates the closed language.</exception>
    public static void ValidateTemplate(EmailTemplate template)
    {
        RequireKey(template.Key);
        if (template.Subject is null || template.Subject.Length is < 1 or > 200 || template.Subject.Any(IsHeaderControl) ||
            template.HtmlBody is null || template.HtmlBody.Length is < 1 or > 2000 ||
            template.TextBody is null || template.TextBody.Length is < 1 or > 2000 || HasInvalidTextControls(template.TextBody))
            throw Invalid("Subject must be 1–200 single-line characters; each body must be 1–2,000 characters.");
        VerifyCharacters(template.Subject);
        VerifyCharacters(template.TextBody);
        Replace(template.Subject, token => token);
        Replace(template.TextBody, token => token);
        var html = ParseHtml(template.HtmlBody);
        foreach (var text in html.DescendantNodes().OfType<XText>())
            Replace(text.Value, token => token);
        if (!template.TextBody.Contains("{{Summary}}", StringComparison.Ordinal) ||
            !template.HtmlBody.Contains("{{Summary}}", StringComparison.Ordinal) ||
            !template.TextBody.Contains("{{CalendarGuidance}}", StringComparison.Ordinal) ||
            !template.HtmlBody.Contains("{{CalendarGuidance}}", StringComparison.Ordinal))
            throw Invalid("Both bodies must retain {{Summary}} and {{CalendarGuidance}} so service intent and calendar limitations cannot be removed.");
        // Every saved template must remain valid for any allowlisted brand, including maximal HTML expansion.
        RenderValidated(template, Keys.Single(x => x.Value == template.Key).Key, new string('&', 80), null);
    }

    /// <summary>Renders only safe generic business intent; never reads names, Quest content, rosters, reasons or private links.</summary>
    /// <param name="template">Compiled or database template.</param>
    /// <param name="kind">Captured source trigger, retaining removal/cancellation intent across retries.</param>
    /// <param name="brand">Validated message branding; substitutions in HTML text nodes are encoded by the XML serializer.</param>
    /// <param name="replyTo">Optional response mailbox.</param>
    /// <returns>Exact inert rendered text and template version evidence.</returns>
    /// <exception cref="DomainException">Template, setting or rendered header validation fails explicitly.</exception>
    public static RenderedBusinessEmail Render(EmailTemplate template, NotificationKind kind, string brand, string? replyTo)
    {
        ValidateTemplate(template);
        if (template.Key != KeyFor(kind))
            throw Invalid("Template does not match the captured business intent.");
        brand = ValidateSetting(BrandKey, brand);
        replyTo = ValidateSetting(ReplyToKey, replyTo ?? "");
        return RenderValidated(template, kind, brand, replyTo);
    }

    private static RenderedBusinessEmail RenderValidated(EmailTemplate template, NotificationKind kind, string brand, string? replyTo)
    {
        string Value(string token) => token switch
        {
            "Brand" => brand,
            "Summary" => NotificationRules.Summary(kind),
            "CalendarGuidance" => CalendarGuidance,
            _ => throw Invalid("Unknown template variable.")
        };
        var subject = Replace(template.Subject, Value);
        if (subject.Length > 200 || subject.Any(IsHeaderControl))
            throw Invalid("Rendered subject exceeds 200 characters or contains a control character.");
        var html = ParseHtml(template.HtmlBody);
        foreach (var text in html.DescendantNodes().OfType<XText>().ToArray())
            text.Value = Replace(text.Value, Value);
        var htmlBody = string.Concat(html.Nodes().Select(x => x.ToString(SaveOptions.DisableFormatting)));
        var textBody = Replace(template.TextBody, Value);
        if (htmlBody.Length + textBody.Length > 6500)
            throw Invalid("Rendered email exceeds the supported body size.");
        return new(template.Key, template.Revision, subject, htmlBody, textBody, string.IsNullOrEmpty(replyTo) ? null : replyTo);
    }

    /// <summary>Validates durable rendered snapshots without rerendering them against changed settings, wording or resource facts.</summary>
    /// <param name="email">Previously captured message content.</param>
    /// <param name="kind">Immutable captured source trigger.</param>
    /// <exception cref="DomainException">Malformed snapshot, wrong template category, unsafe HTML or invalid header data.</exception>
    public static void ValidateSnapshot(RenderedBusinessEmail email, NotificationKind kind)
    {
        if (email.Key != KeyFor(kind) || email.Revision < 0 || email.Subject is null ||
            email.Subject.Length is < 1 or > 200 || email.Subject.Any(IsHeaderControl) ||
            string.IsNullOrWhiteSpace(email.HtmlBody) || string.IsNullOrWhiteSpace(email.TextBody) ||
            email.HtmlBody.Length + email.TextBody.Length > 6500 || HasInvalidTextControls(email.TextBody))
            throw Invalid("Malformed rendered business email snapshot.");
        ValidateSetting(ReplyToKey, email.ReplyTo ?? "");
        VerifyCharacters(email.Subject);
        VerifyCharacters(email.TextBody);
        ParseHtml(email.HtmlBody);
    }

    private static XElement ParseHtml(string fragment)
    {
        try
        {
            using var input = new StringReader("<root>" + fragment + "</root>");
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 10000,
                IgnoreWhitespace = false
            });
            var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            foreach (var node in root.DescendantNodes())
            {
                if (node is XElement element)
                {
                    if (element.Name.Namespace != XNamespace.None || !Elements.Contains(element.Name.LocalName) || element.HasAttributes)
                        throw Invalid("HTML permits only p, br, strong, em, ul, ol, li, h2 and blockquote without attributes.");
                }
                else if (node is not XText || node is XCData)
                    throw Invalid("HTML comments, declarations and executable constructs are not supported.");
            }
            return root;
        }
        catch (XmlException)
        {
            throw Invalid("Use a well-formed inert XHTML fragment; close elements, write <br />, and encode literal ampersands.");
        }
    }

    private static string Replace(string source, Func<string, string> value)
    {
        var result = new StringBuilder();
        for (var position = 0; position < source.Length;)
        {
            var character = source[position];
            if (character == '}')
                throw Invalid("Malformed template token: unexpected closing brace.");
            if (character != '{')
            {
                result.Append(character);
                position++;
                continue;
            }
            if (position + 1 >= source.Length || source[position + 1] != '{')
                throw Invalid("Malformed template token: use {{Name}}.");
            var end = source.IndexOf("}}", position + 2, StringComparison.Ordinal);
            if (end < 0)
                throw Invalid("Malformed template token: missing closing braces.");
            var token = source[(position + 2)..end];
            if (!Variables.Contains(token))
                throw Invalid("Unknown template variable. Allowed: Brand, Summary, CalendarGuidance.");
            result.Append(value(token));
            position = end + 2;
        }
        return result.ToString();
    }

    private static bool IsMailbox(string value) => value.Length <= 320 && value.Contains('@') &&
        MailAddress.TryCreate(value, out var address) && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);

    private static bool HasInvalidTextControls(string value) =>
        value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static bool IsHeaderControl(char character) => char.IsControl(character) || character is '\u2028' or '\u2029';

    private static void VerifyCharacters(string value)
    {
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException) { throw Invalid("Text contains unsupported control characters or malformed Unicode."); }
    }

    private static void RequireKey(string key)
    {
        if (!Keys.Values.Contains(key, StringComparer.Ordinal))
            throw Invalid("Unknown email template key.");
    }

    private static DomainException Invalid(string message) => new(ErrorCode.Validation, message);
}
