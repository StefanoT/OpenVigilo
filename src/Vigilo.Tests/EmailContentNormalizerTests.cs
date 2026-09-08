using Vigilo.Classification;

namespace Vigilo.Tests;

public sealed class EmailContentNormalizerTests
{
    private readonly EmailContentNormalizer _normalizer = new();

    [Fact]
    public void Plain_text_email_is_normalized_without_html_flags()
    {
        var result = _normalizer.Normalize(new EmailContentInput("Hello,\r\n\r\nPlease respond by Friday.", null));

        Assert.Equal("Hello,\n\nPlease respond by Friday.", result.PlainText);
        Assert.False(result.WasHtml);
        Assert.True(result.UsedPlainTextAlternative);
        Assert.Empty(result.Links);
    }

    [Fact]
    public void Simple_html_email_converts_to_readable_text()
    {
        var result = _normalizer.Normalize(new EmailContentInput(null, "<h1>Invoice</h1><p>Please pay by Friday.</p>"));

        Assert.True(result.WasHtml);
        Assert.False(result.UsedPlainTextAlternative);
        Assert.Contains("Invoice", result.PlainText);
        Assert.Contains("Please pay by Friday.", result.PlainText);
        Assert.DoesNotContain("<p>", result.PlainText);
    }

    [Fact]
    public void Html_removes_scripts_styles_tracking_pixels_and_hidden_elements()
    {
        var html = """
            <style>.x{display:block}</style>
            <script>alert('x')</script>
            <p>Visible request</p>
            <p style="display:none">Hidden deadline</p>
            <div aria-hidden="true">Hidden aria text</div>
            <img width="1" height="1" alt="tracker">
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Visible request", result.PlainText);
        Assert.DoesNotContain("alert", result.PlainText);
        Assert.DoesNotContain("Hidden deadline", result.PlainText);
        Assert.DoesNotContain("Hidden aria text", result.PlainText);
        Assert.DoesNotContain("tracker", result.PlainText);
    }

    [Fact]
    public void Html_keeps_content_when_body_carries_font_size_zero_wrapper()
    {
        // Production failure: 1440 Daily Digest emails set font-size:0px on the body (an
        // anti-preheader reset) and re-enable sizes on every child. Removing the body as a
        // "hidden element" detached all children and normalized 90 KB emails to zero text.
        var html = """
            <body style="font-family:Arial;font-size:0px;margin:0">
                <span style="font-size:0px">Invisible preheader text</span>
                <table><tr><td>
                    <p style="font-size:14px;color:#333">Register your apps before September 30.</p>
                    <a href="https://example.test/act">Open the console</a>
                </td></tr></table>
            </body>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Register your apps before September 30.", result.PlainText);
        Assert.DoesNotContain("Invisible preheader text", result.PlainText);
    }

    [Fact]
    public void Html_keeps_body_children_regardless_of_body_style()
    {
        var html = """
            <body style="display:block;width:100%">
                <p style="font-size:12px">Confirm your payment method by Friday.</p>
            </body>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Confirm your payment method by Friday.", result.PlainText);
    }

    [Fact]
    public void Html_removes_media_queries_css_rules_and_keeps_visible_text()
    {
        var html = """
            <style>
            @media only screen and (max-width:639px){.hide{display:none!important}}
            a.cta_button{-moz-box-sizing:content-box!important;color:#fff;padding:12px;}
            .hse-body-wrapper-table {background-color: #FFFFFF;}
            </style>
            <p>Your AI roundup is here</p>
            <a href="https://example.com/start">Start Learning</a>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Your AI roundup is here", result.PlainText);
        Assert.Contains("Start Learning", result.PlainText);
        Assert.DoesNotContain("@media", result.PlainText);
        Assert.DoesNotContain("cta_button", result.PlainText);
        Assert.DoesNotContain("background-color", result.PlainText);
    }

    [Fact]
    public void Html_removes_inline_css_fragments_embedded_in_marketing_text()
    {
        var html = """
            <p>Email de coches.net 96 /*Sans-Serif 4 Outlook with special font*/ body, table, td {font-family: Arial, Helvetica, sans-serif!important;} td.serif, span.serif {font-family: 'Times New Roman', Times, serif!important;} Coches.net Vota y gana un coche</p>
            <a href="https://ablink.sugerencias.coches.net">clic aqui</a>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Email de coches.net", result.PlainText);
        Assert.Contains("Coches.net Vota y gana un coche", result.PlainText);
        Assert.Contains("clic aqui", result.PlainText);
        Assert.DoesNotContain("Sans-Serif", result.PlainText);
        Assert.DoesNotContain("body, table, td", result.PlainText);
        Assert.DoesNotContain("font-family", result.PlainText);
        Assert.DoesNotContain("Times New Roman", result.PlainText);
    }

    [Fact]
    public void Html_removes_hidden_preview_and_invisible_filler_characters()
    {
        var html = """
            <div style="display:none">Hidden preview</div>
            <div style="mso-hide:all">Outlook hidden preview</div>
            <p>Visible&nbsp;message&#8203;&#8204;&#8205;&#8206;&#8207;&#847;&#173;&#65279;</p>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Equal("Visible message", result.PlainText);
        Assert.DoesNotContain("Hidden preview", result.PlainText);
        Assert.DoesNotContain("Outlook hidden preview", result.PlainText);
        Assert.DoesNotContain('\u200b', result.PlainText);
        Assert.DoesNotContain('\u00ad', result.PlainText);
        Assert.DoesNotContain('\u034f', result.PlainText);
    }

    [Fact]
    public void Html_tables_preserve_row_and_cell_order()
    {
        var html = """
            <table>
              <tr><th>Item</th><th>Due</th></tr>
              <tr><td>Invoice 42</td><td>Friday</td></tr>
            </table>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Item | Due", result.PlainText);
        Assert.Contains("Invoice 42 | Friday", result.PlainText);
    }

    [Fact]
    public void Action_links_are_extracted_and_suspicious_text_is_flagged()
    {
        var html = """
            <p>Please approve the request.</p>
            <a href="https://portal.example.com/review/123">Review document</a>
            <a href="https://evil.example.net/pay">https://bank.example.com</a>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Equal(2, result.Links.Count);
        Assert.Contains(result.Links, x => x.VisibleText == "Review document" && x.Kind == LinkKind.Https && x.Domain == "portal.example.com");
        Assert.Contains(result.Links, x => x.IsSuspicious && x.Domain == "evil.example.net");
    }

    [Fact]
    public void Plain_text_alternative_is_preferred_when_it_is_complete()
    {
        var result = _normalizer.Normalize(new EmailContentInput(
            "Please review the attached agreement by Friday.",
            "<p>Please review the attached agreement by Friday.</p><a href=\"https://example.com/review\">Review</a>"));

        Assert.Equal("Please review the attached agreement by Friday.", result.PlainText);
        Assert.True(result.UsedPlainTextAlternative);
        Assert.Single(result.Links);
    }

    [Fact]
    public void Plain_text_alternative_is_preferred_over_noisy_html()
    {
        var result = _normalizer.Normalize(new EmailContentInput(
            "Plain body: please reply by Friday.",
            """
            <style>@media only screen and (max-width:639px){.x{display:none}}</style>
            <p>HTML body with layout noise.</p>
            """));

        Assert.Equal("Plain body: please reply by Friday.", result.PlainText);
        Assert.True(result.UsedPlainTextAlternative);
    }

    [Fact]
    public void Suspiciously_short_plain_text_falls_back_to_html_text()
    {
        var html = "<p>Please approve the renewal document before Friday and complete the verification form.</p>";

        var result = _normalizer.Normalize(new EmailContentInput("Hi", html));

        Assert.Contains("Please approve the renewal document", result.PlainText);
        Assert.False(result.UsedPlainTextAlternative);
        Assert.Contains(result.Warnings, x => x.Contains("suspiciously short", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Quoted_replies_are_split_from_current_message()
    {
        var body = """
            Please send the signed copy today.

            On Thu, Jul 2, 2026 at 4:00 PM Client wrote:
            Previous thread content
            """;

        var result = _normalizer.Normalize(new EmailContentInput(body, null));

        Assert.Equal("Please send the signed copy today.", result.PlainText);
        Assert.Contains("Previous thread content", result.PreviousConversationText);
    }

    [Fact]
    public void Unsubscribe_heavy_marketing_email_keeps_visible_text_without_raw_footer_links()
    {
        var html = """
            <p>Summer sale starts now.</p>
            <a href="https://shop.example.com/sale">Shop now</a>
            <a href="https://mailer.example.com/unsubscribe">Unsubscribe</a>
            <p>Manage preferences</p>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Summer sale starts now.", result.PlainText);
        Assert.Contains("Manage preferences", result.PlainText);
        Assert.DoesNotContain("https://mailer.example.com/unsubscribe", result.PlainText);
        Assert.DoesNotContain(result.Links, x => x.VisibleText.Contains("Unsubscribe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Multilingual_content_preserves_unicode_and_action_language()
    {
        var result = _normalizer.Normalize(new EmailContentInput(
            "Ciao, conferma l'appuntamento entro venerdi. Gracias.",
            null));

        Assert.Contains("conferma l'appuntamento", result.PlainText);
        Assert.Contains("Gracias", result.PlainText);
    }

    [Fact]
    public void Long_inline_urls_are_compacted_to_domain_placeholders()
    {
        var longUrl = "https://email.n.dribbble.com/c/" + new string('a', 180);
        var result = _normalizer.Normalize(new EmailContentInput(
            $"Read the full article ( {longUrl} ) and then decide.",
            null));

        Assert.Contains("[link: email.n.dribbble.com]", result.PlainText);
        Assert.DoesNotContain(longUrl, result.PlainText);
        Assert.Contains("Read the full article", result.PlainText);
    }

    [Fact]
    public void Short_inline_urls_are_preserved()
    {
        var result = _normalizer.Normalize(new EmailContentInput(
            "Open https://example.com/pay by Friday.",
            null));

        Assert.Contains("https://example.com/pay", result.PlainText);
    }

    [Fact]
    public void Malformed_html_is_handled_without_throwing()
    {
        var result = _normalizer.Normalize(new EmailContentInput(null, "<div><p>Please verify&nbsp;the form"));

        Assert.Contains("Please verify the form", result.PlainText);
    }

    [Fact]
    public void Html_entities_and_non_breaking_spaces_are_decoded()
    {
        var result = _normalizer.Normalize(new EmailContentInput(null, "<p>Tom&nbsp;&amp;&nbsp;Jerry&nbsp;LLC</p>"));

        Assert.Contains("Tom & Jerry LLC", result.PlainText);
    }

    [Fact]
    public void Html_only_newsletter_normalizes_to_readable_body_without_layout_noise()
    {
        var html = """
            <html>
            <head>
            <style>
            @media only screen and (max-width:639px){.mobile{display:block}}
            a.cta_button{-moz-box-sizing:content-box!important;color:#fff;}
            .hse-body-wrapper-table {background-color: #FFFFFF;}
            </style>
            </head>
            <body>
            <div style="display:none">͏ ͏ ͏ ͏ ͏ ­ ­ ­ ­ ­</div>
            <h1>Your AI roundup is here</h1>
            <p>Note: You are receiving this platform-related update because you have an active Educative account.</p>
            <p>Stefano,</p>
            <p>Another week, another batch of updates to one of our most popular courses.</p>
            <p>Featured course:</p>
            <p>Building with OpenAI: From APIs to Agents</p>
            <a href="https://educative.io">Start Learning</a>
            <p>Build intelligent, multimodal, and agentic apps using OpenAI APIs.</p>
            <p>This week's newsletter:</p>
            <p>OpenAI Drops Voice Intelligence API: Here's What It Changes</p>
            <a href="https://educative.io/newsletter">Read Newsletter</a>
            <p>Happy learning!</p>
            <p>The Educative Team</p>
            <p>educative.io | fenzo.ai</p>
            <p>Email Preferences</p>
            </body>
            </html>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Your AI roundup is here", result.PlainText);
        Assert.Contains("Building with OpenAI: From APIs to Agents", result.PlainText);
        Assert.Contains("Start Learning", result.PlainText);
        Assert.Contains("Read Newsletter", result.PlainText);
        Assert.DoesNotContain("@media", result.PlainText);
        Assert.DoesNotContain("cta_button", result.PlainText);
        Assert.DoesNotContain("background-color", result.PlainText);
        Assert.DoesNotContain('\u034f', result.PlainText);
        Assert.DoesNotContain('\u00ad', result.PlainText);
    }

    [Fact]
    public void Classifier_payload_preserves_marketing_cta_text_without_tracking_url()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <a href="https://click.educative.io/track/click?campaign=abc&utm_source=email">Start Learning</a>
            """);

        Assert.Contains("Start Learning", payload);
        Assert.DoesNotContain("https://", payload);
        Assert.DoesNotContain("click.educative.io", payload);
        Assert.DoesNotContain("utm_source", payload);
        Assert.DoesNotContain("campaign=", payload);
    }

    [Fact]
    public void Classifier_payload_preserves_newsletter_anchor_text_without_tokens()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <a href="https://example.com/newsletter?id=123&utm_campaign=weekly">Read Newsletter</a>
            <a href="https://example.com/preferences?user=abc">Email Preferences</a>
            <a href="https://example.com/unsubscribe?token=secret">Unsubscribe</a>
            """);

        Assert.Contains("Read Newsletter", payload);
        Assert.Contains("Email Preferences", payload);
        Assert.Contains("Unsubscribe", payload);
        Assert.DoesNotContain("https://", payload);
        Assert.DoesNotContain("token=secret", payload);
        Assert.DoesNotContain("utm_campaign", payload);
        Assert.DoesNotContain("user=abc", payload);
    }

    [Fact]
    public void Classifier_payload_keeps_only_domain_for_invoice_action_link()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <a href="https://pay.stripe.com/invoice/acct_123/session_456?token=secret">Review invoice</a>
            """);

        Assert.Contains("Review invoice \u2192 stripe.com", payload);
        Assert.DoesNotContain("acct_123", payload);
        Assert.DoesNotContain("session_456", payload);
        Assert.DoesNotContain("token=secret", payload);
        Assert.DoesNotContain("https://", payload);
    }

    [Fact]
    public void Classifier_payload_keeps_domain_for_password_security_link()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <a href="https://accounts.google.com/reset/password?token=abc">Reset password</a>
            """);

        Assert.Contains("Reset password \u2192 accounts.google.com", payload);
        Assert.DoesNotContain("token=abc", payload);
    }

    [Fact]
    public void Classifier_payload_extracts_destination_domain_from_tracking_redirect()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <a href="https://email.vendor.com/click?redirect=https%3A%2F%2Fdocusign.net%2Fsign%2Fabc123">Sign document</a>
            """);

        Assert.Contains("Sign document \u2192 docusign.net", payload);
        Assert.DoesNotContain("email.vendor.com/click", payload);
        Assert.DoesNotContain("abc123", payload);
        Assert.DoesNotContain("redirect=", payload);
        Assert.DoesNotContain("https://", payload);
    }

    [Fact]
    public void Classifier_payload_numbers_links_and_reuses_numbers_for_duplicate_anchors()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <p>Opciones disponibles</p>
            <a href="https://ablink.example.com/click/used?token=secret">Segunda mano</a>
            <a href="https://ablink.example.com/click/used?token=secret">Ver segunda mano</a>
            <a href="https://ablink.example.com/click/km0?token=secret">Km 0</a>
            """);

        Assert.Contains($"Important links:{Environment.NewLine}1. Segunda mano", payload);
        Assert.Contains("2. Km 0", payload);
        Assert.Equal(2, payload.Split("[link:1]", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, payload.Split("[link:2]", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("[link-source:", payload);
        Assert.DoesNotContain("[link: ablink.example.com]", payload);
        Assert.DoesNotContain("token=secret", payload);
    }

    [Fact]
    public void Classifier_payload_removes_references_for_links_filtered_from_important_links()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <p>Weekly news</p>
            <a href="https://example.com/article">Read article</a>
            <a href="https://example.com/unsubscribe?token=secret">Unsubscribe</a>
            """);

        Assert.Contains($"Important links:{Environment.NewLine}1. Read article", payload);
        Assert.Contains("Read article [link:1]", payload);
        Assert.DoesNotContain("[link:2]", payload);
        Assert.DoesNotContain("[link-source:", payload);
        Assert.DoesNotContain("token=secret", payload);
    }

    [Fact]
    public void Classifier_payload_numbers_matching_links_in_plain_text_alternative()
    {
        const string usedCarsUrl =
            "https://ablink.example.com/click/used-cars?campaign=newsletter&subscriber=secret-token";
        const string zeroKilometreUrl =
            "https://ablink.example.com/click/zero-kilometre?campaign=newsletter&subscriber=secret-token";
        var plainText = $"""
            Here are this week's vehicle categories and editorial selections. Browse the available listings using the category links below.
            Segunda mano {usedCarsUrl}
            Km 0 {zeroKilometreUrl}
            More automotive news and buying guides are available in this edition.
            """;
        var html = $"""
            <p>Here are this week's vehicle categories and editorial selections.</p>
            <a href="{usedCarsUrl}">Segunda mano</a>
            <a href="{zeroKilometreUrl}">Km 0</a>
            """;

        var content = _normalizer.Normalize(new EmailContentInput(plainText, html));
        var payload = _normalizer.BuildLlmPayload(
            new EmailContentMetadata("Cars", "Sender <sender@example.com>", DateTimeOffset.Parse("2026-07-06T10:00:00Z")),
            content,
            4_000);

        Assert.Contains($"Important links:{Environment.NewLine}1. Segunda mano", payload);
        Assert.Contains("2. Km 0", payload);
        Assert.Contains("Segunda mano [link:1]", payload);
        Assert.Contains("Km 0 [link:2]", payload);
        Assert.DoesNotContain("[link: ablink.example.com]", payload);
        Assert.DoesNotContain("subscriber=secret-token", payload);
    }

    [Fact]
    public void Classifier_payload_ignores_malformed_action_link_url()
    {
        var payload = BuildClassifierPayloadFromHtml("""
            <a href="not a valid url">Submit form</a>
            """);

        Assert.Contains("Submit form", payload);
        Assert.DoesNotContain("not a valid url", payload);
    }

    [Fact]
    public void Html_deadline_email_preserves_deadline_text()
    {
        var html = """
            <style>.layout{display:grid;color:#333}</style>
            <p>Please submit the signed renewal form by July 15, 2026.</p>
            <a href="https://example.com/renew">Submit form</a>
            """;

        var result = _normalizer.Normalize(new EmailContentInput(null, html));

        Assert.Contains("Please submit the signed renewal form by July 15, 2026.", result.PlainText);
        Assert.Contains("Submit form", result.PlainText);
        Assert.DoesNotContain("display:grid", result.PlainText);
    }

    [Fact]
    public void Hidden_attribute_content_is_removed()
    {
        var result = _normalizer.Normalize(new EmailContentInput(null, "<p>Visible</p><div hidden>Secret approval</div>"));

        Assert.Equal("Visible", result.PlainText);
    }

    [Fact]
    public void Llm_payload_includes_metadata_links_attachments_and_warnings()
    {
        var content = new NormalizedEmailContent
        {
            PlainText = "Please sign the document.",
            Links =
            [
                new ExtractedLink("Sign document", "https://example.com/sign", "https://example.com/sign", "example.com", false, LinkKind.Https)
            ],
            Attachments =
            [
                new AttachmentSummary("contract.pdf", "application/pdf", 1234)
            ],
            Warnings = ["HTML parsing failed; plain text fallback was used when available."]
        };

        var payload = _normalizer.BuildLlmPayload(
            new EmailContentMetadata("Contract", "A Sender <a@example.com>", DateTimeOffset.Parse("2026-07-06T10:00:00Z")),
            content,
            2_000);

        Assert.Contains("Subject: Contract", payload);
        Assert.Contains("Body:", payload);
        Assert.Contains("Important links:", payload);
        Assert.Contains("contract.pdf", payload);
        Assert.Contains("Normalization warnings:", payload);
    }

    [Fact]
    public void Is_llm_payload_accepts_windows_line_endings_and_leading_bom()
    {
        var payload = "\ufeff  Subject: Example\r\nFrom: Sender <sender@example.com>\r\nDate: 2026-07-08T10:00:00.0000000+00:00\r\nBody:\r\nPlease reply by Friday.";

        Assert.True(EmailContentNormalizer.IsLlmPayload(payload));
    }

    [Fact]
    public void Llm_payload_builder_terminates_when_fixed_sections_exceed_budget()
    {
        var content = new NormalizedEmailContent
        {
            PlainText = "",
            Links =
            [
                new ExtractedLink(
                    "Open the extremely long generated customer portal link",
                    "https://example.com/" + new string('a', 500),
                    "https://example.com/" + new string('a', 500),
                    "example.com",
                    false,
                    LinkKind.Https)
            ],
            Attachments =
            [
                new AttachmentSummary("large-contract-with-a-very-long-file-name.pdf", "application/pdf", 123456)
            ],
            Warnings =
            [
                "This warning is intentionally long enough to make the fixed payload exceed a small budget."
            ]
        };

        var payload = _normalizer.BuildLlmPayload(
            new EmailContentMetadata(
                "A very long subject that leaves little room for optional payload sections",
                "A Sender With A Long Name <sender@example.com>",
                DateTimeOffset.Parse("2026-07-06T10:00:00Z")),
            content,
            120);

        Assert.True(payload.Length <= 120);
    }

    [Fact]
    public void Llm_payload_keeps_body_when_link_section_exceeds_budget()
    {
        var content = new NormalizedEmailContent
        {
            PlainText = "Please approve the quarterly invoice by Friday. " + new string('x', 1_000),
            Links = Enumerable.Range(1, 12)
                .Select(index => new ExtractedLink(
                    $"Review invoice in the generated customer payment portal with reference {index:D2} and confirm the balance",
                    $"https://pay.stripe.com/invoice/acct_{index}/session_{index}?token=secret",
                    $"https://pay.stripe.com/invoice/acct_{index}/session_{index}?token=secret",
                    "pay.stripe.com",
                    false,
                    LinkKind.Https))
                .ToArray()
        };

        var payload = _normalizer.BuildLlmPayload(
            new EmailContentMetadata(
                "Invoice approval",
                "Billing <billing@example.com>",
                DateTimeOffset.Parse("2026-07-06T10:00:00Z")),
            content,
            500);

        Assert.True(payload.Length <= 500);
        Assert.Contains("Please approve the quarterly invoice", payload);
        Assert.DoesNotContain("Body:\n(empty)", payload);
        Assert.DoesNotContain("Important links:", payload);
    }

    [Fact]
    public void Text_normalizer_removes_html_quoted_reply_signature_and_collapses_whitespace()
    {
        var body = """
            <p>Hello&nbsp;<strong>team</strong>,</p>

            Please review the attached agreement.

            --
            Sender signature

            On Thu, Jul 2, 2026 at 4:00 PM Client wrote:
            > Previous thread content
            """;

        var normalized = EmailTextNormalizer.Normalize(body);

        Assert.Equal("Hello team, Please review the attached agreement.", normalized);
    }

    [Fact]
    public void Text_normalizer_returns_empty_for_blank_input_and_caps_long_text()
    {
        var longBody = new string('x', 12_050);

        var blank = EmailTextNormalizer.Normalize(" \r\n\t ");
        var capped = EmailTextNormalizer.Normalize(longBody);

        Assert.Equal("", blank);
        Assert.Equal(12_000, capped.Length);
        Assert.All(capped, character => Assert.Equal('x', character));
    }

    private string BuildClassifierPayloadFromHtml(string html)
    {
        var content = _normalizer.Normalize(new EmailContentInput(null, html));
        return _normalizer.BuildLlmPayload(
            new EmailContentMetadata("Test", "Sender <sender@example.com>", DateTimeOffset.Parse("2026-07-06T10:00:00Z")),
            content,
            4_000);
    }
}
