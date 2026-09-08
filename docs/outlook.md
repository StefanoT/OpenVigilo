# Classic Outlook setup

[Back to Vigilo](../README.md) · [Integration architecture](../DESIGN.md#19-classic-outlook-integration)

## Classic Outlook category tagging

Outlook integration is disabled by default and supports only **Classic Outlook for Windows**. It does not support new Outlook for Windows, Outlook on the web, macOS or mobile Outlook, Microsoft Graph, or web/VSTO add-ins. Vigilo never starts Outlook. If Classic Outlook is closed or busy, classification and all other Vigilo behavior continue normally while durable category work waits for a later retry.

This COM integration exists because Outlook can display Yahoo and other IMAP mailboxes but Microsoft Graph cannot modify those non-Microsoft mailboxes. All matching and category updates happen locally. Vigilo does not read bodies or attachments through Outlook, request cloud permissions, move or delete messages, set flags or reminders, send mail, create folders, or create Outlook rules.

Setup:

1. Start Classic Outlook and make sure the mailbox is visible there.
2. In **Settings > Classic Outlook**, choose **Test connection**.
3. On the same Classic Outlook page, select the Vigilo email account and its corresponding Outlook store.
4. Enable category tagging, choose **Create or repair categories**, and save settings.
5. Use **Synchronize currently tracked messages** for an explicit backfill. New successful classifications are queued automatically while enabled.

Vigilo uses the same exclusive category in Outlook that it displays as the message's section in the app: `Due today`, `Waiting for my reply`, `Needs review`, `Upcoming`, `Snoozed`, `Commercial offers`, `Dismissed`, or `Done`. Expiration is shown by the row background and does not replace that category, and an escalation never changes the category — it is a row badge in the app only. Existing categories with these names are reused and their colors are preserved; missing categories receive stable default colors. Unrelated user categories are always preserved. Legacy Outlook categories, including `Expired`, `Possible escalation`, `Due soon`, and `Open`, are removed on the next synchronization.

### Filter Outlook by a Vigilo category

In Classic Outlook, select the Inbox and click the search box to display the **Search** ribbon. Open **Categorized** and choose the Vigilo category to display, such as **Commercial offers**, **Due today**, or **Waiting for my reply**. Outlook enters the corresponding category search automatically; the equivalent manual query is `category:"Commercial offers"`.

Choose **Current Folder** in the Search ribbon to restrict results to the selected Inbox. **Current Mailbox** searches every folder in that Outlook mailbox, while **All Mailboxes** searches every configured mailbox. Use **Close Search** or the **X** beside the search box to return to the normal Inbox view.

For a reusable view, press **Ctrl+Shift+P**, choose **Categorized mail**, select the Vigilo category and the correct email account, and save the Search Folder. A Search Folder is only a filtered view: it does not move or duplicate messages.

Legacy category names from earlier Vigilo versions can remain visible in Outlook's **Categorized** menu because Outlook retains its master category definitions. Vigilo no longer assigns those categories, and their presence in the menu does not indicate a synchronization mismatch. They can be removed manually through Outlook's category-management dialog if no other workflow uses them.

The generic names create an unavoidable ownership limitation: Vigilo cannot distinguish a managed category from the same exact category assigned manually or by another application. Every exact registry match is treated as managed, so manual removal or reassignment may be corrected on the next synchronization. Similar names are never removed.

Message lookup first validates a saved Outlook `EntryID` plus `StoreID`, then searches the configured Inbox by normalized RFC Message-ID (`PR_INTERNET_MESSAGE_ID`), and finally examines at most 500 recent candidates in a narrow timestamp window using sender and subject only as supporting evidence. Multiple plausible matches are reported as ambiguous and no message is changed.

Troubleshooting: keep Classic Outlook running and responsive, confirm the correct store is selected, then use **Test connection**, **Create or repair categories**, and the privacy-safe **Export diagnostics** action. New Outlook is a different product and exposes no compatible running COM session.

Developer smoke test (manual only): with a non-production test message selected by RFC Message-ID in a running Classic Outlook profile, record its exact category string and separator; run the client lookup against the chosen store; apply only the category matching that message's current Vigilo UI section; save and verify it; then restore the exact original category string and verify again. Do not use a message whose identity is ambiguous. The test must not move, flag, delete, archive, reply to, or send the message. Automated CI never requires Outlook.
