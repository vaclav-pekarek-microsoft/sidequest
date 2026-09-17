# Supported-device, accessibility and Outlook evidence — NOT EXECUTED

This is the outstanding human-observed A21/A23 protocol. Existing 360px synthetic
Chromium, bUnit, calendar renderer and SQL delivery results are useful regression
support, not physical-device, screen-reader or real mailbox certification.
UI visual polish remains a later workstream; accessibility and honest calendar/
offline limitations are release requirements, not optional polish.

## Approval and evidence prerequisites

Have the product/device/identity owners approve the actual supported OS/browser/
assistive-technology and Outlook version list before testing. The handoff does
not supply a versioned support matrix; the candidate list below is a proposal,
not an assertion of approved support:

| Candidate | Record exact version/device and approval |
|---|---|
| Managed Windows + Edge + approved screen reader (e.g. Narrator/NVDA) | Hardware, OS build, browser, reader version, enterprise policies |
| Physical iPhone/iPad + Safari + VoiceOver, if supported | Model/iOS, browser mode versus installed web app |
| Physical Android + Chrome + TalkBack, if supported | Model/Android, browser mode versus installed web app |
| Outlook on the web, approved desktop Outlook variant(s), approved mobile Outlook | Exact client/build/channel, mailbox policy, organizer/sender identity |

Use approved distinct workforce accounts, least-privilege test fixtures,
synthetic Event/Quest text, approved private-cache/device policy and consenting
test-mailbox recipients. No real employee content in source/screenshots. Do not
change managed browser sign-in policy, copy cookies or use synthetic auth to
claim a live-identity result. Capture run/source SHA, environment, UTC, operator,
expected/observed result and restricted screenshot/video/log digest per case.
Stop and report blockers; do not label unsupported or untested combinations pass.

## A21: interactive, accessibility and offline cases

For every approved supported client, record:

1. Sign in through real Entra as a permitted member; test denied/excluded user
   separately. Navigate Discover→Event→create/publish Quest→join/leave. Use an
   invited private Quest and another user to verify inaccessible content is not
   disclosed. Record loading, empty, error and unavailable states.
2. At **360 CSS pixels**, exercise primary create/edit/join/confirmation flows
   without horizontal page scrolling. Check 200% text zoom and WCAG reflow
   conditions, landscape/orientation where supported, browser zoom and text
   resizing. Emulated viewport evidence supplements, not replaces, real-device
   interaction.
3. Keyboard-only: logical tab/shift-tab order, skip/navigation landmarks, visible
   unobscured focus, no keyboard trap, labeled Fluent fields, date/offset
   selectors, dialog focus entry/return, Escape/cancel semantics, submit and
   validation focus. Exercise long names, missing required fields, DST gap/
   overlap correction and concurrency conflict with unsaved input retained.
4. Screen reader: accessible names/roles/states, headings/landmarks, meaningful
   validation associations and announcements, busy/success/error/reconnect
   changes, updated participation/unread states, and confirmation consequences.
   Record what the reader actually announced; a DOM label alone is not proof.
5. Evaluate WCAG 2.2 AA applicable criteria: contrast and non-color indicators,
   target size/spacing, focus visibility/not obscured, alternatives to gestures/
   dragging, accessible authentication and error prevention. Automated scans
   may assist with approved tools but do not replace manual review; log criteria
   checked, failures, not-applicable rationale and remediation/retest.
6. Online, join an authorized private and public Quest and refresh the bounded
   offline basics. Confirm only title/location/time/last-known status and
   freshness metadata are retained; no protected pages, rosters, contacts,
   invitations, images or response bodies enter service-worker response caches.
   Inspect storage under approved device-debugging policy without exporting
   credentials/content.
7. Close the app/browser process, disconnect network, **cold launch** from the
   approved installed/browser entry point with no server circuit. Verify cached
   basics and stale/last-refreshed labels; verify uncached details and all
   mutations are unavailable, not queued or falsely reported saved.
8. Exercise no cache, storage denial/quota/failure and exactly 24-hour expiry
   under a reviewed test procedure. Do not change production server time.
   Record actual device/browser cache eviction behavior and explain limitations.
9. Reconnect after leave/cancel, private invitation revoke and Event membership
   removal. Verify next authorization refresh removes no-longer-authorized
   protected visible/cache data. A disconnected device cannot receive immediate
   revocation; state that limitation explicitly.
10. Explicit logout and account switch, including multiple tabs and delayed
    in-flight sign-in completion: old basics cleared; delayed old responses
    cannot restore them; old circuit stays unavailable until identity
    revalidation; storage-clear failure is visible without preventing sign-out.
    Keep original inputs through recoverable reconnect, not across account
    identity boundaries.

Log each case as observed-pass, observed-fail, blocked or not-tested, with defect/
retest links. A passing join-only test does not cover create validation or
screen-reader feedback. Accessibility reviewer and device-policy owner must
review evidence and support limitations before A21 can close.

## A23: actual Outlook mailbox/client sequence

Use a real approved ACS sender/domain/organizer identity, approved recipients and
healthy-provider configuration. Begin with an empty test calendar or uniquely
identified test Quest; preserve mailbox/calendar state between steps. One
recipient joins, another follows only, and a third has a private invitation but
has not joined. Do not forward real private content outside approved recipients.

| Step | Action | Required observation |
|---|---|---|
| 1 | Joined recipient joins published Quest | Real message and actual client prompt/entry; recipient-specific content and stable UID; followers/invitee-only users do not get attendance calendar |
| 2 | Owner edits time/location/title | Same UID, increased SEQUENCE, actual update behavior without unintended duplicate entries; exact displayed local/Event time including DST example |
| 3 | Controlled transient retry | Same durable revision/payload on retry; provider receipts and actual client duplicate behavior recorded; no claim of guaranteed exactly-once email |
| 4 | Joined recipient leaves, rejoins | Withdrawal then restoration with same UID and advancing sequence; observe prompts, retained/removed entry and limitations |
| 5 | Moderator suspends, owner edits while suspended, moderator reinstates | Withdrawal, no restoration during suspended edit, restoration carrying latest details and same UID; moderator cannot ordinarily edit content |
| 6 | Private access removed / Event membership removed | Pending private content suppressed; prior attendance calendar withdrawn without protected-content disclosure |
| 7 | Cancel Quest; separately cancel parent Event | Correct affected-recipient withdrawals and no unrelated calendar changes; preserve histories |
| 8 | Complete/archive a separate Quest/Event | No completion/archive withdrawal; record client calendar retention |
| 9 | Decline in Outlook without changing Sidequest | Sidequest participation unchanged; no RSVP ingestion or direct Graph calendar-management promise |
| 10 | Organizer missing/recipient failure/unapproved sender (controlled, approved) | Truthful visible failure and recovery path, no false “calendar updated” claim |

For each message preserve restricted original MIME/ICS, provider request/receipt
ID, logical delivery/correlation/revision IDs, UTC submission and received times,
UID/SEQUENCE/METHOD, client/build, action taken by recipient and before/after
calendar observation. Keep only sanitized references/hashes in release summaries.
Compare recipients to ensure no roster leakage. A downloaded `.ics`, SDK mock
receipt or provider 202 alone does not establish a client update/withdrawal.

Some supported clients may ask users to accept updates or retain a cancelled
entry. Record the behavior, user action required and limitations in UI/help
through the product workstream; do not promise automatic insertion/removal.
If a required client cannot satisfy the agreed behavior, keep the gate open
pending remediation or explicit product support/requirement decision.

## Completion state

No approved support list, real-device observations, screen-reader audit or
approved Outlook mailbox results were supplied for this slice. **A21 and A23
live gates remain OPEN.** Approval of a future test window is not proof of its
successful execution.
