using Sidequest.Application.Administration;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.SecondaryAdministration;

/// <summary>Exercises the closed template language, exact rendering and nonsecret setting boundaries without providers.</summary>
public sealed class BusinessEmailRulesTests
{
    /// <summary>Every compiled category retains exact business intent, plain text, revision zero and explicit calendar guidance.</summary>
    /// <param name="kind">Notification trigger selecting the compiled template.</param>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void CompiledDefaultsPreserveBusinessIntent(NotificationKind kind)
    {
        var template = BusinessEmailRules.Default(BusinessEmailRules.KeyFor(kind));
        var result = BusinessEmailRules.Render(template, kind, "Team & <Friends>", "help@example.invalid");
        Assert.Equal("Team & <Friends> notification", result.Subject);
        Assert.Equal(0, result.Revision);
        Assert.Equal(template.Key, result.Key);
        Assert.Equal("help@example.invalid", result.ReplyTo);
        Assert.StartsWith("<p><strong>Team &amp; &lt;Friends&gt;</strong></p><p>", result.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(NotificationRules.Summary(kind), result.TextBody, StringComparison.Ordinal);
        Assert.Contains("Declining in Outlook does not change attendance; leave in Sidequest.", result.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", result.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", result.TextBody, StringComparison.Ordinal);
    }

    /// <summary>Supplies every supported notification trigger, not a hand-picked subset that could miss a new default.</summary>
    /// <returns>One row per enum member.</returns>
    public static IEnumerable<object[]> Kinds() => Enum.GetValues<NotificationKind>().Select(x => new object[] { x });

    /// <summary>Unknown and malformed variables fail explicitly rather than remaining unresolved or executing expressions.</summary>
    /// <param name="token">Invalid token syntax.</param>
    [Theory]
    [InlineData("{{Quest.Title}}")]
    [InlineData("{{GetType()}}")]
    [InlineData("{{Unknown}}")]
    [InlineData("{{ Brand }}")]
    [InlineData("{{Brand}")]
    [InlineData("{Brand}")]
    [InlineData("Brand}}")]
    [InlineData("{{{Brand}}}")]
    public void RejectsUnknownAndMalformedTokens(string token)
    {
        var template = BusinessEmailRules.Default("quest.invitation") with { Subject = token };
        Assert.Equal(ErrorCode.Validation, Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateTemplate(template)).Code);
    }

    /// <summary>Parsed HTML rejects active constructs, obfuscated attributes, namespaces and non-element instructions.</summary>
    /// <param name="markup">Disallowed literal HTML.</param>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<p onclick=\"alert(1)\">x</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"https://example.invalid\">x</a>")]
    [InlineData("<p style=\"background:url(https://example.invalid)\">x</p>")]
    [InlineData("<img src=\"x\" onerror=\"alert(1)\" />")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" />")]
    [InlineData("<p xmlns=\"http://example.invalid\">x</p>")]
    [InlineData("<!-- {{Summary}} -->")]
    [InlineData("<![CDATA[<script>x</script>]]>")]
    [InlineData("<?xml-stylesheet href=\"https://example.invalid\"?>")]
    [InlineData("<!DOCTYPE root [<!ENTITY secret SYSTEM 'file:///private'>]>")]
    [InlineData("<p>unclosed")]
    public void RejectsActiveOrMalformedLiteralHtml(string markup)
    {
        var template = BusinessEmailRules.Default("quest.invitation");
        template = template with { HtmlBody = markup + template.HtmlBody };
        Assert.Equal(ErrorCode.Validation, Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateTemplate(template)).Code);
    }

    /// <summary>Replacement text is encoded once and never interpreted as another variable or a literal HTML fragment.</summary>
    [Fact]
    public void SubstitutionCannotIntroduceMarkupOrSecondPassVariables()
    {
        var template = new EmailTemplate("access.removed", 7, "{{Brand}}",
            "<p>{{Brand}}</p><p>{{Summary}}</p><p>{{CalendarGuidance}}</p>",
            "{{Brand}}\n{{Summary}}\n{{CalendarGuidance}}");
        var result = BusinessEmailRules.Render(template, NotificationKind.AccessRemoved, "<script>{{Summary}}</script>", null);
        Assert.Equal("<script>{{Summary}}</script>", result.Subject);
        Assert.StartsWith("<p>&lt;script&gt;{{Summary}}&lt;/script&gt;</p>", result.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("<script>{{Summary}}</script>\nYour access has changed.", result.TextBody, StringComparison.Ordinal);
        Assert.Equal(7, result.Revision);
        Assert.Null(result.ReplyTo);
    }

    /// <summary>No header control characters, address lists, display-name mailboxes, sender keys or policy overrides are accepted.</summary>
    /// <param name="key">Submitted setting key.</param>
    /// <param name="value">Invalid setting value.</param>
    [Theory]
    [InlineData("email.message-brand", "")]
    [InlineData("email.message-brand", "Team\r\nBcc: other@example.invalid")]
    [InlineData("email.message-brand", "Team\n")]
    [InlineData("email.message-brand", "Team\u2028Spoof")]
    [InlineData("email.reply-to", "Name <help@example.invalid>")]
    [InlineData("email.reply-to", "a@example.invalid,b@example.invalid")]
    [InlineData("email.reply-to", "help@example.invalid\r\n")]
    [InlineData("email.reply-to", "not-a-mailbox")]
    [InlineData("email.sender", "other@example.invalid")]
    [InlineData("email.mandatory-enabled", "false")]
    [InlineData("email.default-new-quest", "true")]
    public void RejectsInvalidHeadersAndNonAllowlistedSettings(string key, string value) =>
        Assert.Equal(ErrorCode.Validation, Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSetting(key, value)).Code);

    /// <summary>Exact branding limits and harmless whitespace normalization are distinguished from invalid overlong values.</summary>
    [Fact]
    public void EnforcesExactBrandLimitAndAllowsClearingReplyTo()
    {
        Assert.Equal(new string('B', 80), BusinessEmailRules.ValidateSetting(BusinessEmailRules.BrandKey, new string('B', 80)));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSetting(BusinessEmailRules.BrandKey, new string('B', 81)));
        Assert.Equal("Team", BusinessEmailRules.ValidateSetting(BusinessEmailRules.BrandKey, " Team "));
        Assert.Equal("", BusinessEmailRules.ValidateSetting(BusinessEmailRules.ReplyToKey, " "));
    }

    /// <summary>Even an administrator override cannot remove required service wording or calendar limitations.</summary>
    [Fact]
    public void RejectsMissingMandatoryWordingAndWrongKind()
    {
        var template = BusinessEmailRules.Default("access.removed");
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateTemplate(template with { TextBody = "Only custom text" }));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateTemplate(template with { HtmlBody = "<p>{{Summary}}</p>" }));
        Assert.Throws<DomainException>(() => BusinessEmailRules.Render(template, NotificationKind.Joined, "Team", null));
        Assert.Throws<DomainException>(() => BusinessEmailRules.Default("arbitrary.private-resource"));
    }

    /// <summary>Malformed durable snapshots cannot bypass header/HTML rules merely because rendering has already been recorded.</summary>
    [Fact]
    public void DurableSnapshotValidationRejectsTamperedHeadersHtmlAndKind()
    {
        var snapshot = BusinessEmailRules.Render(BusinessEmailRules.Default("access.removed"), NotificationKind.AccessRemoved, "Team", null);
        BusinessEmailRules.ValidateSnapshot(snapshot, NotificationKind.AccessRemoved);
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSnapshot(snapshot with { Subject = "Subject\r\nBcc: secret@example.invalid" }, NotificationKind.AccessRemoved));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSnapshot(snapshot with { HtmlBody = "<script>run()</script>" }, NotificationKind.AccessRemoved));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSnapshot(snapshot with { ReplyTo = "one@example.invalid,two@example.invalid" }, NotificationKind.AccessRemoved));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSnapshot(snapshot with { Revision = -1 }, NotificationKind.AccessRemoved));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSnapshot(snapshot, NotificationKind.Joined));
    }

    /// <summary>Template validation considers rendered limits for every allowed brand, not just the short synthetic preview label.</summary>
    [Fact]
    public void TemplateSaveValidationRejectsExpansionThatCouldPoisonDelivery()
    {
        var template = BusinessEmailRules.Default("quest.invitation");
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateTemplate(template with
        {
            Subject = "{{Brand}}{{Brand}}{{Brand}}"
        }));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateTemplate(template with
        {
            HtmlBody = "<p>" + string.Concat(Enumerable.Repeat("{{Brand}}", 20)) + "</p><p>{{Summary}}</p><p>{{CalendarGuidance}}</p>"
        }));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateSetting(BusinessEmailRules.BrandKey, "\ud800"));
        Assert.Throws<DomainException>(() => BusinessEmailRules.ValidateTemplate(template with { TextBody = template.TextBody + "\0" }));
    }
}
