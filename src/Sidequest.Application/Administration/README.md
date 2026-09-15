# Administration and business email

These services use the existing persisted `IResourceAccess` administrator check.
They do not confer Event membership, ordinary/private Quest access, moderation or
content-edit permission. Account selection uses minimal, currently eligible
same-tenant **local account records** maintained by the existing identity/directory
workflow. It does not accept an arbitrary mailbox or provision accounts.

## Administrator assignments

`AdministrationService` provides paged lists, explicit additions and confirmed
removals. Additions check the selected account rowversion; removals check the
assignment rowversion. Serializable transactions protect the eligible administrator
set, including competing self-removals. Deadlock/conflict outcomes require a fresh
review, not an automatic retry with stale authorization. Each successful mutation
commits its audit record atomically. Bootstrap and sign-in role restoration
behavior are unchanged.

## Narrow ownership recovery

Supply one Event/Quest ID, inspect its **content-free** recovery confirmation,
select an eligible same-tenant replacement, and supply a 10–2,000 character reason.
The confirmation digest covers resource, owner-assignment and account versions.
Every current owner must have a non-future `UserAccount.DepartureVerifiedUtc`.
An empty owner set, a merely disabled account, an eligible owner, a directory
outage, absence or an unanswered message cannot qualify the operation.

Departure evidence must be written by a **separately authorized operational
verification process**. This feature never writes eligibility/departure fields
and provides no verification checkbox or user-disable command.

Deployment configuration `Administration:DepartureRecovery` contains:

- `Enabled`: defaults `false`;
- `ProcedureReference`: nonempty approved operational procedure reference, at most
  500 characters without control characters, copied into each recovery audit.

Enabling configuration is not itself proof of approval. The real verification
procedure, its approval authority, database write controls and operational owner
remain external release gates. Use only synthetic evidence until those gates pass.

Recovery resolves immutable parent IDs before its transaction, then acquires the
Event lock **before** transactional actor/ownership checks. It replaces only the
specified resource's departed owner assignments, creates/reactivates required
individual Event membership for the replacement, and commits audits plus an
ownership-change outbox intent together. It preserves lifecycle/content even for
archived/terminal resources. It does not restore attendance, following, private
invitations or any other resource's ownership. The administrator does not gain
resource content access through the confirmation operation.

## Business settings

Only these exact nonsecret keys are editable:

| Key | Effect |
| --- | --- |
| `email.message-brand` | 1–80 characters displayed inside message subjects/bodies; default `Sidequest` |
| `email.reply-to` | One validated mailbox without a display name, or empty to omit Reply-To |

Branding is **not** the ACS From display name. The ACS verified sender and calendar
organizer remain the existing deployment identity. Reply-To does not change the
destination, sender or organizer. Credentials, endpoints, arbitrary sending domains
and mandatory delivery policy are not editable. Accepted optional preference
defaults are unchanged.

Settings use rowversion/create-only checks and value-free transactional audits.
Invalid stored values fail delivery explicitly. The editor can inspect their raw
encoded values solely for repair; this does not permit successful rendering/sending.

## Closed template language

`BusinessEmailRules.TemplateKeys` is the stable allowlist. Defaults are compiled
revision zero; database overrides append positive revisions under the existing
unique `(Key, Revision)` constraint. Historical wording is never edited/deleted by
the service. Paged history includes the compiled default; restoring an old/default
wording appends a new revision. Competing saves cannot overwrite a revision.

Variables are exactly `{{Brand}}`, `{{Summary}}`, `{{CalendarGuidance}}`. Unknown,
malformed, dotted, whitespace-padded or expression-like tokens fail explicitly.
There is no Razor, evaluation, reflection-based template execution or resource
lookup. Both bodies must retain Summary and CalendarGuidance.

Subjects are single-line, 1–200 characters before and after rendering. Each
template body is 1–2,000 characters; combined rendered bodies are at most 6,500
characters. HTML is parsed as a strict XHTML fragment using a DTD-prohibiting,
resolver-free XML reader. Only `p`, `br`, `strong`, `em`, `ul`, `ol`, `li`, `h2`,
`blockquote` are allowed, with **no attributes or namespaces**. No active URLs,
links, style, scripts, images, comments, processing instructions or CDATA are
allowed. Close elements, write `<br />`, and encode literal ampersands.
Save validation checks rendered limits against the maximum permitted brand and
HTML expansion, so a later valid branding change cannot poison a saved template.

Substitutions enter parsed text nodes and are HTML-encoded exactly once.
Substitution values are never parsed again as tokens. The preview uses fixed
synthetic branding and a safe generic summary, never a private resource ID/content.
Invalid stored overrides never cause a silent default fallback. Raw encoded editor
history permits repair by appending a new validated revision; delivery still fails
until a valid current override is present.

## Actual durable email integration

`DeliveryDispatcher` applies existing recipient, permission, lifecycle, preference,
reminder and calendar-ordering checks first. It then renders current business
configuration and freezes the exact subject, HTML/text, template key/revision and
Reply-To in `DeliveryPayload.BusinessEmail`, in the same transaction that records
submission uncertainty. First-attempt legacy payloads without a snapshot render
normally. Retry attempts reuse the snapshot rather than reread changed wording or
settings, but still reauthorize every send. Malformed snapshots fail explicitly.

The captured change kind remains authoritative. No title, description, location,
recipient/owner roster, reason or private link is available to templates, including
removal/withdrawal overrides. Existing calendar rendering, UID, sequence, timestamp,
method, current-attendee checks and compensation semantics are unchanged.

ACS maps the optional `EmailMessage.ReplyTo` to its SDK `ReplyTo` list. No sender
display-name override is claimed. Missing provider configuration remains an
explicit failure. No live provider approval, mailbox arrival, exactly-once
delivery or real Outlook interoperability is asserted.

## Host integration and coordinated persistence

Call `services.AddSidequestAdministration(configuration)` once, after registering
existing identity/access, persistence, clock and change writer. The helper starts
no workers, makes no provider call, and does not enable recovery by default.
Parent-owned startup now registers this helper and navigation links `/administration`;
neither that link nor an Entra admission role grants administrator access.
The feature pages use per-page Interactive Server with prerendering retained:

- `/administration`
- `/administration/recovery`
- `/administration/email`
- `/administration/templates`

The existing `/notifications/failures` page/service remains the only delivery
diagnostics/replay boundary and is linked rather than duplicated.

`NotificationDelivery.PayloadJson` already has an explicit `nvarchar(max)` mapping
with its maximum length cleared, and the initial migration creates the unbounded
column. Frozen email/calendar snapshots and Unicode JSON escaping may exceed
10,000 characters without requiring a widening migration. The real producer/
dispatcher regression `LargeUnicodeFrozenPayloadPersistsAndRetriesExactProducerIntent`
verifies a larger SQL-reloaded payload and exact retry content/identity.

The aligned persistence boundary includes `NotificationTemplate` in its immutable-
history save guard across all save overloads. Administration and its tests append
new revisions rather than modifying/deleting old rows. No schema changes are
required by this feature.

Existing template fields fit the current column limits and unique key mapping;
business setting keys fit the current unique Key mapping. No startup database
creation/migration, account-disable bypass, new provider configuration or package
dependency is introduced.
