# Sidequest — Project Handoff / Product & Technical Specification

**Status:** Accepted for implementation (D34, 2026-09-14); external approval gates remain open.
**Last revised:** 2026-09-15.
**Implementation:** M1/M2 verified; combined M3 acceptance passed. M4 release hardening remains. No production deployment.

Sections 1–46 explain the product intent. Section 47 summarizes the agreed direction.
Sections 48–61 form the **accepted V1 baseline** and are authoritative if an earlier
overview is less specific. Section 61 records the step-by-step decisions and explicit
supersessions; superseded choices are history, not implementation requirements.
Changes to accepted contracts must update this document before dependent development proceeds.

Review entry points: scope in section 48, permissions in section 49, lifecycle in
section 51, delivery plan in section 59, and remaining approval gates in section 61.
Approval of this document does not itself start implementation or launch development
agents. Wait for an explicit development instruction, then follow the foundation-first
plan and respect the remaining external gates.

## 1. Project Summary

**Sidequest** is an internal web application for organizing small social activities that happen during larger company events.

Typical example:

A company-wide event lasts one week and has around 300 attendees. During that week, people independently organize things such as:

- dinners
- board game evenings
- drinks
- sightseeing
- sports
- informal meetups
- other small activities

Sidequest provides one place to create, discover, follow, join, and manage these activities.

The application is intended for authenticated Microsoft employees/users only.

---

## 2. Terminology

Terminology should remain consistent throughout the application and documentation.

### Event

An **Event** is the large parent container.

Example:

> Microsoft Engineering Week 2026

An Event typically has:

- Name
- Description
- Start date
- End date
- Audience
- Owners (one or more, all equal)

Events mostly exist to provide:

- context
- access control
- audience definition
- grouping of Quests

Events are not intended to be the main thing users interact with daily.

### Quest

A **Quest** is a smaller activity happening during an Event.

Examples:

> Board Games Evening  
> Dinner at a local restaurant  
> Morning Run  
> Escape Room

Quests are the main user-facing entity in Sidequest.

The application should therefore generally be **Quest-first rather than Event-first**.

---

## 3. Authentication

The application is internal.

Authentication will use:

**Microsoft Entra ID**

Only authenticated users from the intended Microsoft organization/tenant should have access.

Application-specific permissions should not depend directly on Microsoft Entra roles.

Instead:

- Entra ID = authentication / identity
- Sidequest database = application roles and ownership

---

## 4. Microsoft Graph

Microsoft Graph is **not required for application roles**.

Graph is primarily useful for things such as:

- searching Microsoft users
- selecting users
- selecting Microsoft groups for bulk add/invite
- expanding a selected group into individual recipients in a background operation
- resolving individual users for Event membership

Groups are an input convenience only. Sidequest does not retain group-based Event
permissions, synchronize group membership, or call Graph to authorize Event access.

Graph should be treated as an integration, not as a fundamental dependency of the domain model.

---

## 5. Event Model

Any normal user can create an Event.

An Event should contain at least:

- Name
- Description
- Start date
- End date
- Required time zone
- Short discovery summary (all published Active Events are listed)
- Owners (one or more, all equal)
- Audience
- Status
- Created date
- Updated date

---

## 6. Event Audience

An Event defines who is allowed to participate in it.

An Event audience consists only of individual Microsoft users.

An Event manager can select a Microsoft group as a bulk add/invite convenience. A
background operation resolves its members and adds or invites each eligible person
individually. Thereafter each person either has Event membership or does not; changes
to the original Microsoft group have no effect on Sidequest access.

Section 50 defines individual membership and the accepted bulk-operation safeguards.

People belonging to the Event audience can:

- see public Quests
- create Quests
- join Quests
- follow Quests

---

## 7. Request to Join an Event

There should also be a way for users who are **not currently members of an Event** to request access.

A user can:

> Request to join Event

The Event owners receive the request.

They can:

- Approve
- Reject

After approval, the user becomes part of the Event audience.

Event owners should also be able to:

- invite users
- add users directly

---

## 8. Event Discovery

Originally the intention was not to create a major global Event browser because Quests should be the main experience.

However, because users need the ability to **request access to Events**, a lightweight Event overview is required.

It contains:

### My Events

Events the user belongs to.

### Available Events

All published Active Events the user may request access to. Only a limited discovery
summary is exposed to non-members. There is no Unlisted Event mode.

### Pending Requests

Events where their membership request is awaiting approval.

This page should remain lightweight.

It should not replace the Quest-centric main dashboard.

---

## 9. Duplicate Event Prevention

Anyone may create an Event, but duplicate Events should be discouraged.

When creating an Event, Sidequest should look for probable duplicates using:

- overlapping dates
- similar names

Example warning:

> A similar Event already exists during these dates.

Show:

- Event name
- dates
- Event owners
- ways to contact the owners

The warning does **not prevent creation** and must not reveal Events the user
is not allowed to discover. Section 52 defines the matching rule.

The system should avoid heavy AI-based duplicate detection initially.

Basic name similarity + date overlap is sufficient.

---

## 10. Event Ownership

Each Event has one or more equal owners. The creator starts as the first owner;
additional owners have the same permissions. There is no primary-owner/co-owner distinction.

Only those users can modify Event configuration.

Normal users cannot modify the Event.

Event owners **do not automatically own or control Quests created by other users**.

That separation is intentional.

---

## 11. Event Deletion / Archiving

Hard deletion should generally be avoided once an Event has meaningful activity.

Prefer states such as:

- Draft
- Active
- Completed
- Archived
- Cancelled

If Quests already exist, archiving/cancelling is safer than deletion.

This prevents accidentally deleting other people's Quests and attendance data.

---

## 12. Quest Creation

Any member of an Event can create a Quest inside it.

A Quest should contain:

- Title
- Description
- Date/time
- Time zone inherited from the parent Event (not editable on the Quest)
- Location
- Optional suggested capacity
- Owners (one or more, all equal)
- Cover image
- Visibility
- Status
- Attendee count
- Follower count
- Creation/update timestamps

---

## 13. Quest Location

Location can initially be flexible.

Examples:

- free-text address
- restaurant
- park
- Microsoft office
- office room

Later, Microsoft office locations could potentially be suggested automatically.

For V1, free-text location is sufficient.

---

## 14. Time Zones

Each Quest inherits its parent Event's time zone. There is no Quest-level time-zone
picker or override. The timing model requires both start and end times,
entered and displayed in the inherited Event zone.

Internally:

> Store timestamps in UTC.

In the UI, consider displaying:

- Quest local time
- user's local time

This is useful because participants may travel between countries during company events.

---

## 15. Quest Capacity

Capacity is optional.

It should initially be treated as:

> advisory capacity

rather than a strict hard limit.

For example:

> Suggested capacity: 12

The application does not need:

- hard blocking
- waitlists
- automatic rejection

in the initial version.

---

## 16. Quest Visibility

A Quest has two visibility modes.

### Public Quest

Visible to everyone who belongs to the parent Event.

### Private Quest

Visible only to the owners and specifically invited users who also
belong to the parent Event. An invitation alone never grants Event membership.

Private Quests should not appear in general Event discovery.

Invitations are tied to the recipient's tenant/object identity, not freely shareable
bearer links. Event managers have a separate, audited moderation view as specified
in section 49; "Private" does not mean hidden from Event moderation.
Invited members can immediately view and join the Quest. There is no separate
Accept/Decline action for private Quest invitations, and invitation alone is not attendance.

---

## 17. Quest Ownership

Every Quest has one or more equal owners. The creator starts as the first owner.

Only the Quest owners may:

- edit it
- cancel it
- manage it
- modify attendees where permitted
- change Quest settings

The parent Event owner does **not automatically gain edit permissions** for the Quest.

---

## 18. Event Owner Moderation of Quests

Although Event owners cannot directly edit someone else's Quest, they should have a moderation mechanism.

An Event owner may **suspend a Quest**.

Suspending requires a mandatory written reason.

Example:

> This Quest conflicts with company event policy.

Suspension should:

- change Quest status
- notify Quest owners
- notify joined attendees
- notify followers
- record the reason
- record who suspended it
- record when it happened

There should not be a casual:

> hide → unhide → hide

workflow.

A proper state transition and audit history should exist.

The accepted lifecycle states (with exact transitions in section 51) are:

- Draft
- Active
- Suspended
- Cancelled
- Completed
- Archived

Reinstatement is explicit, requires a reason, and triggers notifications.

---

## 19. Following vs Joining

Sidequest differentiates between **Following** and **Joining**.

### Follow

Means:

> I'm interested, but I'm not committing yet.

Followers can receive relevant updates.

### Join

Means:

> I intend to attend.

Joined users become attendees.

These are mutually exclusive participation states: a user can follow or join, not both.
Joining automatically unfollows. Leaving does not start, restore, or otherwise change
following; someone leaving a joined Quest therefore becomes neither joined nor following.
Joined users still receive attendee updates without needing to follow.
Joining does not require organizer approval. See sections 50 and 54.

---

## 20. User Dashboard

The main Sidequest dashboard should be **Quest-centric**.

Sections:

### Upcoming

Quests the user has joined.

### Following

Quests the user is watching.

### Organizing

Quests where the user is one of the owners.

Show lightweight statistics such as:

- joined count
- follower count
- invited count for private Quests

### Discover

Public Quests available inside Events the user belongs to.

---

## 21. Quest Images

A Quest can have a cover image.

V1 supports uploads only; AI generation is deferred.

### Upload

User uploads their own JPEG, PNG, or WebP image, up to 2 MiB and 20 megapixels.
The cover is optional; use a default cover when none is uploaded.

### AI Generation (Post-V1)

A later version could generate a Quest image based on details such as:

- Quest title
- description
- theme
- location

If implemented later, image generation should be abstracted so its provider can change.
No AI interface, provider, job, or generation UI is required in V1.

---

## 22. Notifications

Notifications are considered a **first-class feature**.

V1 users configure optional activity notifications and reminders; service/calendar
messages follow the mandatory delivery rules in section 54.

Examples:

- New Quest created in one of my Events
- Quest I follow was updated
- Quest I joined changed time
- Quest I joined changed location
- Quest cancelled
- Quest suspended
- Quest restored
- Upcoming Quest reminder
- Event membership request approved/rejected
- Private Quest invitation

---

## 23. Notification Channels

V1 channels:

### Email

Primary external notification mechanism.

### In-app notifications

Required in V1, including unread counts and mark-as-read.

Future possibilities:

- Microsoft Teams
- WhatsApp
- push notifications

Notification delivery should therefore be abstracted from notification generation.

Conceptually:

`Notification -> Channel -> Provider`

rather than calling SMTP directly from Quest business logic.

---

## 24. Notification Preferences

Users should be able to configure preferences.

Examples:

### New Quests

Notify me when a new Quest appears in one of my Events.

### Quest Updates

Notify me when a Quest I follow changes.

### Reminders

Reminders apply only to joined attendees, never followers. Each user can disable
reminders or configure a numeric lead time of X hours before the Quest starts, rather
than choosing from a fixed list of minute presets.

Section 54 defines the initial preferences and mandatory service messages.

---

## 25. Calendar Integration

Calendar integration should be treated as part of the main product.

Initial implementation:

**ICS / iCalendar**

When a user joins a Quest:

> send calendar invitation

When Quest date/time/location changes:

> send calendar update using the same UID

When Quest is cancelled:

> send calendar cancellation

Leaving, invitation revocation, membership loss, and suspension also require calendar
handling. Section 55 defines those cases, sequencing, and retry behavior.

The consistent calendar UID is important so Outlook updates the existing calendar entry instead of creating duplicates.

Calendar invites should apply primarily to **joined users**, not merely followers.

---

## 26. Future Calendar Integration

Direct Microsoft Graph calendar integration could later allow:

- automatically adding calendar entries
- updating entries
- deleting/cancelling entries

But this should **not be required for V1**.

ICS keeps the first implementation simpler and less tightly coupled to Microsoft Graph.

---

## 27. Email Configuration

Email configuration should use a hybrid model.

### Infrastructure configuration

Keep secrets outside the application administration UI.

Examples:

- SMTP password
- API keys
- connection strings
- provider credentials

Use:

- environment variables
- Azure configuration
- Key Vault
- deployment configuration

### Business configuration

Can be editable from a Sidequest administration page.

Examples:

- sender display name
- reply-to address
- default notification settings
- email templates
- template branding

---

## 28. Email Templates

Provide default templates in the application.

Allow database overrides from administration.

This gives:

- working defaults immediately
- flexibility later
- ability to update wording without redeploying

Typical templates:

- Quest invitation
- Quest joined
- Quest updated
- Quest cancelled
- Quest suspended
- Quest reminder
- Event membership request
- Event membership approved
- Event membership rejected

---

## 29. Global Roles

The role model should remain deliberately simple.

### Administrator

Initially there will be one administrator.

The administrator can:

- manage global application settings
- manage email/template configuration
- add/remove other administrators
- potentially manage system-level configuration

Administrator should **not be required for everyday Event/Quest management**.

### User

Everyone else is simply a User.

Any User can potentially:

- create Events
- create Quests
- join Quests
- follow Quests
- request Event membership

Ownership determines editing rights rather than complicated RBAC roles.

---

## 30. Permission Model

The simplified model is:

### Administrator

System configuration.

### Event Owner

Controls their Event.

### Quest Owner

Controls their Quest.

### User

Participates normally.

No complex permission hierarchy is currently required.

---

## 31. Important Permission Rule

**Event ownership does not imply Quest ownership.**

Example:

John creates:

> Engineering Week

Alice creates:

> Board Games Night

John cannot edit Alice's Board Games Night simply because John owns Engineering Week.

He can only use the defined Event moderation mechanism, such as suspending the Quest with a reason.

---

## 32. Ownership Continuity

Ownership is a set of equal owners, not a transferable primary role. Any owner can
add/remove owners, including themselves, while preserving at least one eligible owner.
There is no normal "transfer ownership" operation.

If an owner leaves Microsoft, the remaining eligible owners continue managing the
resource. If the last eligible owner departs, an Administrator assigns a replacement
through the audited recovery flow. Automatic departure detection/reassignment is
outside V1; an unavailable directory check is not proof of departure.

---

## 33. Technical Architecture

Accepted architecture:

### Application

**ASP.NET Core Blazor Web App**

Using:

- server-side rendering where possible
- Interactive Server where interactivity is required

Essentially modern Blazor Server architecture.

---

## 34. Why Blazor Server / Interactive Server

It fits Sidequest well because the application is:

- internal
- authenticated
- online for application operations, with a small read-only offline joined-Quest view
- form/data oriented
- not computationally heavy on the client
- Microsoft ecosystem based

There is currently little reason to move significant application logic into WebAssembly.

---

## 35. Business Logic Separation

Business logic should not live directly inside Razor components.

Recommended layering:

```text
UI
 ↓
Application services
 ↓
Domain / Business logic
 ↓
Infrastructure
 ↓
Database / Graph / Email / Storage
```

This allows future consumers such as:

- API
- different frontend
- mobile app
- background jobs

without rewriting the core business logic.

---

## 36. Application Architecture

A **modular monolith** is selected.

No need for microservices.

Project structure:

```text
Sidequest.Web
Sidequest.Application
Sidequest.Domain
Sidequest.Infrastructure
```

Use feature folders within these four projects, as described in section 56.

Avoid architecture complexity that doesn't solve an actual problem.

---

## 37. Database

Preferred:

**SQL Server / Azure SQL**

Potential high-level entities:

```text
User / Administrator
Event / EventOwner
EventMembership / BulkMembershipOperation / BulkMembershipRecipient
EventMembershipRequest / EventInvitation
Quest / QuestOwner
QuestInvitation / QuestParticipation
EventStatusHistory / QuestStatusHistory / AuditEntry
MediaAsset
Notification / NotificationDelivery / NotificationPreference / NotificationTemplate
ApplicationSetting / OutboxMessage / ScheduledWork / CalendarDeliveryState
```

Section 53 defines the baseline entities, constraints, and persistence ownership.
Physical mappings and migrations are produced in the foundation milestone.

---

## 38. Background Processing

Some functionality requires scheduled/background jobs.

Examples:

- Quest reminders
- delayed notifications
- email retries
- calendar notification delivery

A background job mechanism should therefore be included in the architecture.

Accepted V1 choice: an ASP.NET Core `BackgroundService` with a durable SQL outbox
and persisted scheduled work, running in the web deployment. In-memory-only timers
or queues are not sufficient. Section 56 defines leases, retries, recovery, and
the boundary for moving the worker to a separate host later.

---

## 39. Frontend

The application should work well on:

- desktop
- tablet
- mobile

Design should be responsive and preferably mobile-first.

---

## 40. UI Library

Preferred candidate:

**Microsoft Fluent UI Blazor**

Reasons:

- fits Microsoft ecosystem
- suitable for internal business applications
- responsive web components
- consistent design language
- avoids building every control from scratch

Alternatives discussed:

- Tailwind
- Bootstrap
- Bulma
- MudBlazor

The upstream Fluent UI Blazor repository declares an MIT license. Verify the exact
package version, transitive dependencies, and .NET 10 compatibility during the
foundation milestone; the upstream README consulted still names .NET 8 and 9.
The requirement remains:

> No commercial UI library license.

---

## 41. Mobile Installation

A native app is currently unnecessary.

Sidequest should instead be a:

**Progressive Web App / installable web application**

Users should be able to install it from the browser.

Requirements do not currently include:

- Apple App Store
- Google Play Store
- native device APIs
- offline-first editing or participation

Application operations require connectivity, but users can view cached basic details
of Quests they have joined when offline: title, location, and date/time. This applies
to joined public and private Quests. It is a limited read-only fallback, not full offline
application functionality. Section 56 defines the cache boundary and accepted safeguards.

---

## 42. Storage

Quest images will need object storage.

Likely:

**Azure Blob Storage**

Store in SQL only:

- metadata
- URL/reference
- ownership information

Do not store large image binary data directly in SQL unless there is a compelling reason.

---

## 43. AI Image Generation (Post-V1)

AI image generation is deferred entirely from V1. If added later, it should be behind
an abstraction.

Example concept:

```csharp
IQuestImageGenerator
```

This prevents the application from being tightly coupled to a specific AI provider.

---

## 44. Audit / History

Important lifecycle actions should be recorded.

Examples:

- Quest created
- Quest edited
- Quest suspended
- Quest reinstated
- Quest cancelled
- Quest owner changed
- Event owner changed
- Event membership approved

This is particularly important when users receive notifications based on changes.

---

## 45. Non-Goals for Early Version

Avoid unnecessary complexity initially.

Examples:

- Microservices
- hard Quest capacity enforcement
- waiting lists
- comments/chat
- complex social features
- native mobile apps
- direct Outlook calendar management
- elaborate Event discovery
- AI-based Event duplicate detection
- sophisticated analytics

These can be added if there is a demonstrated need.

---

## 46. Current Product Philosophy

Several principles emerged from the discussion:

**Quest-first.**  
Users should primarily interact with Quests.

**Events are containers.**  
They provide context, membership and access boundaries.

**Low administration overhead.**  
Normal users should be able to create things themselves.

**Ownership over hierarchy.**  
Users control what they created.

**Moderation without takeover.**  
Event owners may suspend inappropriate Quests but cannot silently edit them.

**Soft restrictions where possible.**  
For example, Quest capacity is guidance rather than a hard rule.

**Microsoft-integrated, but not Microsoft-dependent everywhere.**  
Use Entra and Graph where useful without coupling the whole domain model to them.

---

## 47. Decisions Already Made

The following can currently be considered agreed:

- Product name: **Sidequest**
- Parent object: **Event**
- Activity object: **Quest**
- Internal Microsoft-authenticated application
- Microsoft Entra ID authentication
- Microsoft Graph for user/group integration
- Blazor Web App
- Interactive Server where needed
- PWA / browser installation
- SQL-based persistence
- Quest-first dashboard
- One or more equal Event owners (refined in D18)
- One or more equal Quest owners (refined in D18)
- Any user can create Events
- Event members can create Quests
- Public and private Quests
- Follow vs Join
- Optional advisory capacity
- Cover image upload; AI generation deferred to post-V1 by D29
- Email notifications
- In-app notifications required in V1 (D12)
- Notification preferences
- ICS calendar integration
- Event membership request workflow
- Event owners can invite/add members
- Event owners cannot directly edit other people's Quests
- Event owners may suspend Quests with a mandatory reason
- Modular monolith preferred
- Fluent UI Blazor currently preferred for frontend

---

## 48. Accepted V1 Scope

V1 means the first production-capable release, not merely the first working demo.
The scope below is accepted through D34, incorporating the refinements in D01–D33.

| Area | V1 requirement |
|------|----------------|
| Identity | Single-tenant Entra sign-in, stable user identity, database-managed administrators |
| Events | Draft/publish, listed discovery for all Active Events, duplicate warning, membership requests, direct adds, invitations, individual membership with background group-to-user bulk add/invite, ownership, automatic completion, cancellation and archive |
| Quests | Draft/publish, Public/Private visibility, invitations, ownership, advisory capacity, follow/join/leave, moderation, history |
| Experience | Quest-first dashboard, Event overview, responsive accessible forms, explicit local times, installable web app with read-only offline basics for joined Quests |
| Delivery | Email and in-app notifications, preferences, reminders, durable retries, ICS invitations/updates/cancellations |
| Media | Optional uploaded cover (JPEG/PNG/WebP, up to 2 MiB and 20 megapixels), default cover, private storage; no AI generation |
| Administration | Administrator continuity, business email settings, safe template overrides, delivery failure visibility, audited ownership recovery |
| Operations | Azure deployment, telemetry, backups, authorization tests, accessibility and end-to-end release checks |

AI generation is entirely out of V1 scope: no implementation, provider abstraction,
generation UI, jobs, or AI approval gate is required for the release.
Graph outages must not disable existing
individual membership or access. Group-to-user bulk add/invite remains a V1 requirement;
ongoing group audiences, synchronization, and group-based authorization are not in scope.

Not in V1: the section 45 non-goals, recurring Quests, external/guest participation,
all-day or online-meeting-specific Quest types, RSVP processing from email, calendar
subscriptions, self-service hard deletion, and automatic disabled-account ownership recovery.
V1 UI and templates are English; dates and times respect user locale.

---

## 49. Authorization and Privacy Contract

**Identity:** use Entra `(tenant ID, object ID)` as the unique external key. Email,
UPN, and display name are mutable contact/display data, never authorization keys.
Only configured-tenant workforce accounts are eligible; tenant membership alone
does not establish employee eligibility. Guest/external exclusion must be enforced
through an approved Entra assignment/eligibility policy, not an email-domain check.

**Roles:** Event manager means any Event owner; Quest manager means any Quest owner.
All owners of a resource have equal permissions. Roles are additive, but lifecycle restrictions still apply. An Administrator
has no implicit Event membership, private Quest access, or content-editing privilege.
All resource reads and commands authorize server-side, including Interactive Server
callbacks, jobs, images, search results, counts, and exports. Hiding a button is insufficient.

| Action | Eligible tenant user | Event member | Event manager | Quest manager | Administrator only |
|--------|----------------------|--------------|---------------|---------------|--------------------|
| Create Event | Yes | Yes | Yes | Yes | Yes |
| View discoverable Event summary / request access | Yes, subject to discovery/state | Yes | Yes | Yes | Yes |
| View full Event / public non-draft Quests | No | Yes | Yes | Yes, with membership | No |
| Configure Event / manage audience / decide requests | No | No | Yes | No | No |
| Create Quest | No | Yes, in Active Event | Yes, in Active Event | Yes, with membership | No |
| View private non-draft Quest | No | Only with valid invitation | Moderation view only unless separately invited/owning | Yes, with membership | No |
| View draft Quest | No | No | No, unless also Quest manager | Yes | No |
| Edit / publish / cancel Quest | No | No | No, unless also Quest manager | Yes | No |
| Invite / revoke invite / remove attendee | No | No | No, unless also Quest manager | Yes | No |
| Join / leave / follow / unfollow | No | Own participation, with Quest access and allowed state | Same member rules | Same member rules | No |
| Suspend / reinstate Quest | No | No | Yes, with reason | No, unless also Event manager | No |
| View moderation history | No | Status and participant-facing reason only | Own Event moderation records | Own Quest moderation records | No |
| Add / remove equal owners | No | No | Yes, for their Event; retain at least one eligible owner | Yes, for their Quest; retain at least one eligible owner | Audited last-owner departure recovery only |
| Cancel / archive Event | No | No | Yes, with lifecycle guards | No | No |
| Manage global settings / templates / administrators | No | No | No | No | Yes |

The last column describes privileges from the Administrator role alone, not a ban
on administrators using ordinary user features.

### Visibility and disclosure

- All published Active Events are listed to eligible signed-in users and expose only name, dates, time zone, short discovery summary,
  and owners' directory contacts to eligible tenant users. Full descriptions, audiences,
  Quests, member lists, and counts are member-only.
- There is no Unlisted Event mode. A signed-in user with a direct Active Event URL
  sees the same discovery summary and can request access; knowing the URL is not a
  membership grant.
- Private Quest URLs confer no access. Unauthorized Quest IDs, images, counts,
  notification links, and calendar downloads must not disclose resource existence
  or content. Draft Events are visible only to their managers.
- A dedicated Event moderation screen includes private non-draft Quests and exposes
  title, description, time, location, cover, owners, and moderation history, but not
  invitation, attendee, or follower rosters. Record each private moderation detail
  access. Explain this exception on the private Quest creation form.
- Authorized ordinary Quest viewers see current attendee names and attendee/follower
  counts. Only Quest managers see follower names and invitation status/rosters.
  Email addresses are never a public roster field.
- Public/Private visibility and parent Event cannot change after publication.
  This avoids silently exposing private content or carrying participants across
  access boundaries. A new Quest is required instead.

### Ownership continuity

Every Event and Quest has one or more equal owners. Creation atomically assigns the
creator as the first owner. Any owner may add/remove owners, including themselves,
but ordinary owner-list changes must retain at least one eligible owner. Targets must
be eligible users and, for Quests, current Event members. There is no primary owner,
co-owner rank, transfer command, or special creator privilege after creation.
A Quest ownership assignment also grants named private Quest access;
removing that role removes role-derived access, not any separate invitation.
Assignment never automatically joins or follows the Quest.

Owner-list changes are atomic, audited, and notify added/removed and remaining owners.
Serialize competing removals so two owners cannot concurrently remove the last eligible
owner. A manager cannot lose Event membership through normal removal until they have
been removed from the Event and child Quest owner lists, appointing another owner first
where necessary.
Event managers must have individual Event membership. Quest managers must already be
Event members when appointed. There are no group-derived manager memberships.

If an owner leaves Microsoft, they lose account eligibility and remaining eligible owners
continue without a transfer. Departure can leave a resource with no eligible owner;
do not keep a departed account authorized merely to satisfy an ownership invariant.
If the last eligible owner departs, an Administrator performs narrowly scoped ownership
recovery with a resource ID, verified departure, eligible replacement, and mandatory reason.
Recovery may remove departed owners and create required individual Event membership.
It is audited and notifies the replacement and any remaining eligible owners, but grants
no general content-reading/editing rights to the Administrator.
V1 does not automatically detect departures or reassign ownership. A Graph outage, mere
absence, or an unanswered message is not evidence that someone left the organization.
The operational process for verifying departure must be approved before live recovery.

Keep at least one administrator; seed the first through deployment configuration,
not a public "first user wins" flow.

---

## 50. Membership and Participation Contract

### Individual Event membership

One individual membership record per Event/user is the source of truth: Active or
Removed, with audit history. Authorization checks this record and account eligibility,
not group grants, membership assertions, exclusions, or Graph membership queries.
Pending requests and invitations are not membership. Draft Event access remains
manager-only even if its individual audience has been configured.

"Remove member" deactivates that person's membership; no group exclusion or grant
precedence is necessary. Ordinary members can leave through the same deactivation
path. Re-adding or accepting a new invitation explicitly restores membership, never
prior Quest participation. Removal applies on the next server authorization check,
including operations in an existing Blazor circuit. A Graph outage or subsequent
change to a source Microsoft group has no effect on existing access.

Accepted continuity guard: remove the user's Event/Quest owner assignments before
ordinary membership removal or leaving, adding another eligible owner first if necessary.
Audited administrator recovery handles the last eligible owner's organizational departure.

On membership removal, invalidate Quest invitations and end active attendance and
follows across that Event, enqueue calendar withdrawals, and suppress further content
delivery. Persist history. Rejoining does not silently restore attendance, follows,
or invitations.

### Background group-to-user bulk operations

Selecting a group initiates a one-time background expansion into individual users.
"Add group members" creates individual memberships; "Invite group members" creates
individual invitations, which still require acceptance under D14. Group changes after
expansion never automatically add or remove anyone. Retrying is not ongoing synchronization.
There is no Event-to-group audience relation, periodic group refresh, or group-based
authorization. A source group ID may appear only in operational/audit records for the
bulk action, never as an access rule.

The following execution safeguards are accepted in D16:

- Support same-tenant, Graph-resolvable security and Microsoft 365 groups. Resolve
  eligible users, including supported nested-group membership; reject unsupported
  group types or incomplete/unauthorized expansion explicitly.
- Complete paginated directory expansion and persist a deduplicated recipient snapshot
  before applying any membership changes. That snapshot reflects the enumeration,
  not an atomic directory point-in-time guarantee. An expansion failure adds nobody
  and leaves a visible retryable/failed operation.
- Apply each snapshot recipient through the ordinary individual add/invite use case,
  with fresh actor authorization and Event lifecycle checks. Persist per-recipient
  outcomes and resume unfinished recipients without duplicating memberships,
  invitations, or notifications. Display progress, skips, and partial failures.
- Graph throttling honors `Retry-After`. A retry uses the saved recipient snapshot;
  fetching current group members again requires a new explicit bulk action.
- Skip existing members and matching pending invitations as appropriate. Never
  automatically reactivate a previously removed member through bulk add; require
  an explicit individual restore. A bulk invitation can invite them to consent again.
  A removal/revocation after the batch begins must win over stale batch work or retry.
- Store group selection only for the bulk workflow/audit, not on individual membership
  as an ongoing group grant. Enforce configurable recipient limits and rate limits;
  notify the initiating manager of completion or failure.

### Requests and Event invitations

| Workflow | States and behavior |
|----------|---------------------|
| Membership request | Pending -> Approved, Rejected, or Withdrawn; approval atomically activates individual membership |
| Event invitation | Pending -> Accepted, Declined, Revoked, or Expired; acceptance atomically activates individual membership |
| Direct add | Event manager activates individual membership immediately and notifies the user |

Only Active Events accept requests, invitations, direct adds, or audience expansion.
Draft configuration may select managers and individual audience members, but sends no invitations
or audience notifications before publication. One pending request and one pending
invitation per Event/user; repeated identical actions are idempotent. Rejecting a request
requires a reason visible to the requester. A user may submit a new request after
rejection; retain history and rate-limit submission.

Invitations expire after seven days or at the Event's local end-of-day, whichever
is earlier. No request/invitation can be accepted once the Event has ended or is not
Active. End dates do not revoke existing membership or historical access.
Successful membership activation resolves any pending request/invitation for that user;
concurrent approval/acceptance must not create duplicate memberships.
Use Approved for a pending request and Accepted for a pending Event invitation resolved
by a direct add, recording the manager as actor and the direct-add reason. Event end
or cancellation expires pending Event invitations and rejects pending requests with
a system reason; the user is not left with a permanently pending item.

### Quest invitations, attendance, and following

Quest invitations are for private Active Quests and must target current Event members.
They are identity-bound access grants plus notifications. An invited member can
immediately view the private Quest and choose Join or Follow using ordinary participation
controls. There is no Accept/Decline step and no pending/accepted invitation workflow.
Issuing an invitation never joins or follows on the person's behalf.

Quest invitation states are Active and Revoked. Active invitations retain read access,
including historical access after Quest completion/cancellation, while Event membership
remains. There is no automatic invitation expiration or acceptance deadline in this
simplified model. Joining/leaving/following/unfollowing does not change the access grant.
Revocation or Event membership loss removes invitation-derived access, attendance, and
following, with calendar withdrawal where applicable. Independent owner-derived access
is unaffected. Reinvitation is explicit and never restores prior participation.
The acceptance/expiry workflow in this section applies to Event invitations only.

Public Quests need no invitation. Users can join/follow only while the Event and Quest
are Active and before the Quest ends. Joining after the start is allowed. Participation
is one mutually exclusive state per Quest/user: None, Following, or Joined.

| Command | From | Result |
|---------|------|--------|
| Follow | None / Following | Following; repeat is a no-op |
| Follow | Joined | Conflict; show that the user already joined and receives attendee updates; never silently leave |
| Join | None / Following | Joined; atomically remove Following and update both counts |
| Join | Joined | No-op; no duplicate calendar invitation or notification |
| Leave | Joined | None; withdraw calendar, do not start or restore Following |
| Leave | None / Following | No-op; following is unchanged |
| Unfollow | Following | None |
| Unfollow | None / Joined | No-op; attendance is unchanged |

All operations are repeat-safe. Joining from Following is one participation transition,
not separate user-visible join/unfollow notifications. A user may leave/unfollow while
suspended or after cancellation/completion, without erasing historical participation.
Quest ownership and private invitation access remain separate from participation.

Quest managers may remove an attendee with a mandatory reason; notify the removed user
and withdraw their calendar invitation. This is not a permanent ban on rejoining a
public Quest. On private Quests, managers can additionally revoke the invitation.
No organizer can silently add an attendee on their behalf.

---

## 51. Lifecycle and State Transitions

Only the following transitions are allowed; all others return an explicit conflict.
Persist actor, previous/next state, UTC timestamp, reason where required, and correlation
ID. Archived content is read-only; ownership recovery, access revocation, a user's own
leave/unfollow, audit, and required delivery bookkeeping remain possible. Archiving must
never freeze an access grant permanently. There is no unarchive or restart of
cancelled/completed Quests.

### Event

| From | To | Actor and guards | Effects |
|------|----|------------------|---------|
| Draft | Active | Event manager; valid dates, zone, at least one eligible owner, audience configuration | Lists Event to eligible signed-in users; membership becomes usable |
| Draft / Active | Cancelled | Event manager; reason required; show impact confirmation | Applies the cancellation rule below; closes pending requests/invitations |
| Active | Completed | System at midnight after the Event's inclusive end date in its time zone | Stops new activity/discovery, resolves pending membership work, preserves historical access |
| Completed | Archived | Event manager; all Quests are Completed, Cancelled, or Archived | Preserves authorized historical access |
| Cancelled | Archived | Event manager | Preserves cancellation history |

Draft Events cannot contain Quests. Hard deletion is permitted only for an unpublished
Draft Event with no requests, invitations, or Quests; record the deletion in the audit log.
A Draft Event is never disclosed to a non-manager. An Event cancelled before publication
retains owner-only access after cancellation and archival; draft audience membership does
not become usable through cancellation. Retained Draft-to-Cancelled Event history identifies
that restriction, and every direct Event/child/delivery query must honor it.
Completed/Cancelled/Archived Events are excluded
from non-member discovery; an already-known link gives only a minimal unavailable-state
response, with no Event content.

**Event cancellation is an explicit system cascade, not general Quest edit permission.**
In the same transaction, cancel all Draft, Active, and Suspended child Quests, retain
their prior states/history, close pending Event invitations and membership requests, and write
notification/calendar work to the outbox. Completed/Archived Quests are unchanged.
The confirmation names the number of affected Quests and explains calendar withdrawals.
Quest managers cannot restore those Quests while the parent is Cancelled.

At midnight after an Active Event's inclusive end date, the system changes it to
Completed. New Quests, new membership, joining, and following stop at that instant even
if the completion job is late. Reads show the effective Completed state; commands
reconcile an overdue completion before applying lifecycle guards. Existing members
retain historical access. Completion does not send cancellation emails or withdraw
historical calendar entries. There is no automatic Event archiving or reopening of
Completed Events in V1.

Completion cleanup: complete any overdue Active/Suspended child Quests and
cancel unpublished Draft Quests with a system reason, without participant notifications.
Resolve pending membership requests/invitations as in section 50. All child intervals
are contained within Event dates, so no valid published Quest extends beyond completion.
Persist lifecycle history and make cleanup repeat-safe and recoverable after restart.
Completion cleanup is accepted as part of D13.

Event configuration is editable only while Draft or Active and before its end.
Completion freezes dates and content, but managers may still perform access revocation,
ownership continuity, and archive tasks. Before completion, Event dates cannot be changed
to exclude an existing non-cancelled Quest interval. The Event time zone is fixed after
publication. A date change must invalidate/reschedule the Event completion job, and
stale jobs must check the current end date before transitioning.

### Quest

| From | To | Actor and guards | Effects |
|------|----|------------------|---------|
| Draft | Active | Quest manager; parent Active, valid future end time and required fields | Public discovery/new-Quest notifications become eligible; invitations may be issued |
| Draft | Cancelled | Quest manager, or parent cancellation | No participant delivery unless previously published |
| Draft | Cancelled | System on parent completion | Record system reason; no participant delivery |
| Active | Suspended | Event manager; reason required | Freeze new participation, not organizer editing; notify managers/attendees/followers and withdraw calendars |
| Suspended | Active | Event manager; reason required; parent Active and end time in future | Explicit reinstatement; notify affected users and restore current attendees' calendars |
| Active / Suspended | Cancelled | Quest manager, or parent cancellation; reason required | Notify affected users, preserve invited users' historical read access, withdraw calendars |
| Active / Suspended | Completed | System when end time is reached | Stops new participation/reminders; keeps suspension history; no automatic reinstatement or calendar cancellation |
| Completed / Cancelled | Archived | Quest manager | Hide from default lists while retaining authorized history |

The parent cancellation cascade may also cancel Draft Quests. Quest managers can edit
Draft, Active, and Suspended Quest content while the parent is Active and has not ended.
Editing a Suspended Quest never changes its status or restores participation/calendars;
only an Event manager can explicitly reinstate it. Record edits in history so moderators
can evaluate changes. Reinstatement uses the latest validated Quest details.
Completed/Cancelled Quests cannot be edited. Draft deletion is allowed only if never
published and without participation; no self-service deletion otherwise.
Publishing or editing an Active/Suspended Quest requires an end time in the future.
Cancelling a Quest does not revoke its private access invitations. Invited Event members
retain authorized historical read access, but cannot join a cancelled Quest. Invitation
revocation and Event membership removal remain separate access-removal actions.

Suspension preserves attendance/follows and private invitations; those users retain
read access with a prominent status banner. Publication never implicitly joins the owner.
Managers cannot use an edit operation to set a lifecycle status directly.
Commands and queries enforce time cutoffs even if the completion job is late.

---

## 52. Validation and UX Contract

### Data and time

- Event dates are inclusive local dates in a required IANA time zone, with end date
  on or after start date; publication requires that the Event has not ended. Quests store
  `StartUtc` and `EndUtc`, with end strictly after start; their time zone comes from
  the parent Event's IANA zone ID. Do not store a separately editable Quest zone.
  Validate the Quest interval against midnight at Event start through the exclusive
  midnight after Event end in that same inherited zone. Use a maintained time-zone library
  capable of mapping IANA zones consistently across Windows and Azure.
- Reject nonexistent daylight-saving local times. For ambiguous local times, require
  the user to choose the intended offset before conversion. Never guess silently.
- Display Quest-local date/time in the inherited Event zone first, with a labeled user-local equivalent
  when different. Prefer a saved user zone, otherwise browser zone; fall back to the
  Quest zone with an explicit label. Calendar files use UTC start/end.
- Accepted limits: Event name and Quest title 3–120 characters; discovery summary
  0–300; descriptions 0–10,000; location 1–500; required reasons 10–2,000, all after
  trimming. A Quest needs a location to publish. Descriptions are plain text in V1,
  never arbitrary HTML. Cover image and advisory capacity are optional.
- Capacity, when supplied, is an integer 1–10,000. Capacity overflow is labeled
  visually but never blocks joining. Counts are derived from current active records.
- Use paginated lists (default 25, maximum 100) with deterministic tie-breaking by ID.
  Default upcoming order is start time ascending. History is separate from upcoming.

### Duplicate Event warning

Consider only Events discoverable by the creator with inclusive overlapping date ranges.
Normalize names by invariant case folding, punctuation-to-space, and whitespace collapse.
Warn for identical normalized names or a Jaccard similarity of at least 0.6 between
distinct normalized word sets; empty sets never match. Show at most five candidates,
highest similarity first, with owners' contacts. An explicit "Create anyway" continues.
Completed/Cancelled/Archived Events are not candidates. No AI or hidden Event hints.

### Required screens

| Screen | Required behavior |
|--------|-------------------|
| Home / My Quests | Upcoming joined and Following tabs are mutually exclusive per user/Quest; Organizing may overlap either; Event filter and history link |
| Discover | Public Active Quests in the user's Events; Event/date filters; no private items or private-count hints |
| Events | My Events, Available Events that are open to requests, Pending Requests; create Event action and duplicate warning |
| Event detail | Member context, public Quests, create Quest; manager-only settings/membership/moderation tabs |
| Quest detail | Status, time in the inherited Event zone and user-local equivalent, location, owners' contacts, advisory capacity, accessible cover, attendees; Join replaces Following; Joined state offers Leave, not Follow; leaving never restores Following |
| Quest editor | Draft/save/publish flow, inherited Event time zone shown read-only, private-moderation disclosure, validation, pending upload indicators; no AI controls |
| Invitations | Pending Event invitations with accept/decline/expiry; private Quest invitations link directly to details with Join/Follow, never Accept/Decline; private cards require current access |
| Offline joined Quests | Cached title, location, date/time and last-known status; visible last-refreshed/stale warning; no mutations or access to uncached details |
| Notifications | Unread count, mark one/all read, authorized deep links, preferences |
| Administration | Business email settings/templates with preview, administrators, failed delivery/replay, ownership recovery |

Private invitations appear in the Invitations screen, not Discover. All accessible invited
private Quests also appear in an "Invited" filter even if not joined or followed.
Default lists exclude ended/archived Quests, which remain available in history while authorized.

Each screen has loading, empty, error, and forbidden/unavailable states. Confirm
cancellation, suspension, membership removal, attendee removal, and owner-list changes
with their consequences. Do not silently overwrite an edit after a concurrency conflict.
On access revocation, clear protected visible state on the next server authorization
check; reauthorize after reconnect and on every subsequent data request. The explicit
offline joined-Quest cache is the limited exception described in section 56; do not
promise immediate remote revocation of data on a disconnected device.

Target WCAG 2.2 AA: keyboard navigation, visible focus, labeled inputs, meaningful
validation, sufficient contrast, non-color status indicators, and screen-reader feedback.
Primary flows must work at 360 CSS pixels without horizontal page scrolling.

---

## 53. Data and Application Contracts

Use application-generated GUID identifiers and UTC audit timestamps. Entra identity
is an alternate key, never the internal primary key. Persist aggregates and their audit,
outbox, and schedule changes transactionally in Azure SQL through EF Core.

| Records | Required contents / constraints |
|---------|--------------------------------|
| User, Administrator | Unique tenant/object pair; contact data, first sign-in and last resolution times; unique administrator user ID |
| Event, EventOwner | Creator ID for audit only, dates, zone, discovery summary, status, rowversion; unique Event/owner pair in owner relation; no primary-owner or discovery-mode field |
| EventMembership | Unique Event/user pair, Active/Removed status, concurrency version, activation/removal timestamps and actor; no group-based access |
| BulkMembershipOperation, BulkMembershipRecipient | Event, actor, add/invite mode, operational source group ID, expansion state, frozen recipient IDs, per-recipient outcome/idempotency key, progress/failure details; no authorization role |
| EventMembershipRequest, EventInvitation | Recipient, status, decision actor/reason, timestamps/expiry; at most one pending record per Event/user/type |
| Quest, QuestOwner | Event ID, creator ID for audit only, fields, UTC interval, visibility/status, rowversion, calendar UID/version; zone inherited through Event ID; unique Quest/owner pair in owner relation; no primary-owner or independent time-zone field |
| QuestInvitation | Unique Quest/recipient pair, Active/Revoked state and timestamps; no acceptance/expiry fields; reinvitation/revocation cycles retained in history |
| QuestParticipation | Unique Quest/user pair with None/Following/Joined state, concurrency version, and state-change timestamps; history retained; no simultaneous follow and attendance rows |
| EventStatusHistory, QuestStatusHistory, AuditEntry | Immutable lifecycle/actions; actor (user/system), resource IDs, safe change summary, reason, correlation ID |
| MediaAsset | Blob reference, Quest/creator, content metadata, pending/ready/failed upload state; never a public permanent URL; no AI generation job entity |
| Notification, NotificationDelivery | Recipient, type, source change ID, resource reference, read state; per-channel delivery status/idempotency key |
| NotificationPreference, NotificationTemplate, ApplicationSetting | Per-user/per-event overrides where specified; allowlisted settings/template variables; template version |
| OutboxMessage, ScheduledWork | Versioned payload/reference, stable recipient set or recipient-resolution basis, due time, attempts, next attempt, lease, completion/dead-letter state |
| CalendarDeliveryState | Quest/user, latest intended/sent sequence and method, desired participation state, prior delivery outcome |

`EventMembership` is the authoritative individual access record, not a projection from
directory groups. Group expansion is a bulk command input only. Do not add group grants,
membership-assertion caches, exclusion precedence, or periodic membership synchronization.
`EventOwner` and `QuestOwner` relations are the ownership source of truth. Do not store
a competing primary `OwnerId` on Event/Quest. Creator IDs are historical metadata only,
not authorization grants. Enforce creation and last-eligible-owner guards transactionally.
Foreign keys must not cascade-delete user content when identity/contact records change.
Use filtered unique indexes for pending records and database uniqueness for repeat-safe
participation; application prechecks alone are insufficient.
Derive attendee and follower counts from `QuestParticipation` state; joining from
Following changes both atomically. Record prior states in history for attendance audit.

### Shared application boundary

Razor components call application use cases, not `DbContext`, Graph, email, or Blob SDKs.
The domain contains no infrastructure or UI dependencies. Application depends on Domain;
Infrastructure implements Application ports; Web composes the application and infrastructure.
The diagram in section 35 describes runtime flow, not project-reference direction.

Before parallel feature work, establish shared contracts for current identity and access,
time (`TimeProvider`), directory queries, transaction/outbox writing, media storage,
and email delivery. AI contracts are deferred. Also freeze:

- Lifecycle/visibility enums, command/query names, DTO ownership, paging and validation.
- Expected outcomes: validation errors with field names, not-found/unavailable, forbidden,
  conflict, and dependency-unavailable. Unexpected faults carry a correlation ID and are
  logged centrally without exposing details to the user.
- Commands for create/edit/publish/cancel, membership decisions, invitations, ownership,
  join/leave/follow, suspension/reinstatement, and notification preferences.
- Versioned domain-event envelopes: event ID, type/schema version, aggregate ID/version,
  actor, occurred-at UTC, and correlation ID. Persist events with the state change.

Do not invent a public REST API, generic repository framework, distributed message bus,
or mediator dependency merely to enable parallel work. In-process interfaces are sufficient.
Optimistic concurrency applies to configuration and lifecycle edits; serialize/conflict
competing participation, revocation, and lifecycle actions as needed to preserve invariants.

---

## 54. Notification Contract

In-app notifications are mandatory for the targeted recipients below. Email is also
mandatory for service messages; optional activity email follows user preferences.
Mandatory means delivery is durably attempted and failures surfaced, not guaranteed
mailbox arrival. Joining clearly explains that service/calendar messages will follow.
Registered members means individual Event members who have signed in at least once.
Individual invitations/direct adds, including those created through a bulk group
operation, may target directory-resolved users before their first sign-in.

| Trigger | Recipients | Email / calendar behavior |
|---------|------------|---------------------------|
| Public Quest published | Registered effective Event members, excluding actor | Optional new-Quest email; no calendar |
| Private Quest / Event invitation | Named recipient | Mandatory invitation email; no calendar until joined |
| Membership request submitted | Event managers | Mandatory action-required email |
| Request decided / direct add / Event invitation accepted or declined | Requester or added user; managers get in-app decision visibility | Mandatory decision/access email to affected user |
| User joins | Joining user; Quest managers get in-app attendance change | Mandatory joining/calendar invitation to user; no manager email by default |
| User leaves / attendee removed / access revoked | Affected user; Quest managers get in-app attendance change | Calendar withdrawal if previously invited; mandatory reason/access email for removal |
| Quest time / location change | Current attendees and followers, plus other Quest managers | Mandatory attendee email/calendar update; optional follower/manager activity email; Quest has no independently changeable zone |
| Title / description / cover / capacity change | Current attendees and followers, plus other Quest managers | Optional activity email; title/description changes also update attendees' calendars |
| Content edited while Suspended | Event moderators and other Quest managers | In-app moderation update; no participant update email or active calendar request until explicit reinstatement |
| Quest suspended / reinstated / cancelled | Quest managers, current attendees/followers, valid private invitees | Mandatory status email; attendee calendar withdrawal/restoration as appropriate |
| Event cancelled | Effective registered Event members and affected Quest recipients | Mandatory Event status email; affected attendee calendar withdrawals |
| Reminder due | Current joined attendees with reminders enabled; never followers | Optional email/in-app reminder X configured hours before start; one per user/Quest/start revision |
| Owner added, removed, or recovered | Added/removed and remaining eligible owners | Mandatory service email and audit; no content delivery to departed/ineligible accounts |
| Event or Quest completed / resource archived | None by default | No email or calendar cancellation merely because time passed |

Coalesce overlapping recipient roles so one source change produces at most one in-app
notification and one email per recipient/channel purpose. Do not suppress necessary
calendar messages to the actor. Self-generated optional activity messages may be skipped.
Capture attendance, follow, invitation, and ownership recipient IDs with the source change.
For new public Quest fan-out, resolve registered members when processing publication,
then persist that recipient set once; retries do not re-expand it to newly registered users.
Email delivery is per recipient, never a To/CC roster exposing other users.
Bulk Event cancellation coalesces status email where possible, but preserves a distinct
calendar cancellation for each affected Quest.

### Preferences

Accepted defaults: new-Quest email off; joined/followed Quest activity email on; attendee
reminders enabled with a lead time of 1 hour. Each user can disable reminders or enter
a positive numeric lead time in hours; there is no fixed preset-only list. Accepted
input bounds are 0.01 through 168 hours, permitting up to two decimal places (for example
0.5 hours), with explicit validation instead of silent rounding.
Store the normalized duration, not a reminder-option enum.
Users can override new-Quest email per Event. Preferences never disable required invitations,
membership/ownership decisions, material attendee changes, moderation/cancellation, or
calendar withdrawals/updates. In-app activity records remain available even when optional
email is off; disabling reminders disables both reminder channels.

Evaluate optional preferences and access again immediately before delivery. Never send
stale queued content after access is revoked. Required minimal cancellation/access-loss notices and calendar
withdrawals are the exception: use minimal historical identifiers, not newly fetched
private details. Recipient authorization uses individual Event membership, not Graph
group verification.
Existing in-app items must not expose protected text after access loss.
Resolve recipient email from trusted tenant directory data, never a user-supplied address.
Missing/unusable addresses create a visible failed email delivery; retain the in-app
notification and do not report email success or infer an address from the display name.

Reminders are eligible only for current Joined participation while both resources are
Active and start is still in the future. Schedule at start minus that attendee's
configured X-hour lead time. Following alone never schedules or receives a reminder.
Leaving/removal invalidates pending reminders; delivery rechecks attendance.
Rescheduling or changing the lead time replaces obsolete scheduled work. If an attendee
joins, enables/changes reminders, or a Quest moves inside the reminder window, enqueue
one immediate reminder if start has not passed. Deduplicate by recipient/Quest/start-time
revision; preference toggling and leave/rejoin do not produce repeat reminders for the
same revision.

Templates use an allowlisted, non-executable placeholder syntax, HTML-encoded user data,
and plain-text alternatives. Overrides are versioned, previewable, validated before save,
and audited. An invalid override is rejected rather than silently replacing a working
template. Sender identity stays deployment-allowlisted; administrators can edit display
name and validated reply-to, not credentials or arbitrary sending domains.

---

## 55. Calendar Contract

Use standards-compliant iCalendar/iTIP generation through a maintained library, with
one stable globally unique UID per Quest and one stable organizer sending identity.
The organizer is the Sidequest service mailbox, not an individual Quest owner's email.
Every invitation/update/cancellation is addressed only to its recipient; never expose
the private attendee roster. Calendar files contain no authorization tokens.

| Action | Calendar result |
|--------|-----------------|
| Join Active Quest | `METHOD:REQUEST`, current start/end/location/title/description |
| Calendar-relevant edit | `REQUEST` with same UID and higher `SEQUENCE` |
| Leave / removal / confirmed access loss | Recipient-only `METHOD:CANCEL` with same UID and higher sequence |
| Suspension / cancellation / parent cancellation | `CANCEL` to previously invited attendees |
| Reinstatement / rejoin after withdrawal | Fresh `REQUEST`, same UID, sequence greater than previous cancellation |
| Edit while Suspended | Retain withdrawn calendar state; send latest details only after explicit reinstatement |
| Follow only / invitation not joined | No calendar entry |
| Completion / archive | No new calendar message; historical appointment remains |

Persist a monotonically increasing Quest calendar revision allocated transactionally
for each calendar-affecting change, including recipient-specific withdrawal/rejoin.
Gaps in an individual recipient's sequences are fine. `UID`, `SEQUENCE`, `DTSTAMP`,
`DTSTART`, `DTEND`, `ORGANIZER`, recipient `ATTENDEE`, and method/status must be consistent
with the relevant protocol. A retried logical delivery reuses the exact sequence,
timestamp, and payload; it is not a new calendar change.

Serialize delivery per Quest/recipient. Do not let a delayed older request be sent after
a newer cancellation. Supersede obsolete queued work, retaining a cancellation if an
earlier request may already have reached the provider. Persist provider-acceptance and
uncertain outcomes; network timeouts cannot prove the recipient received nothing.
Exactly-once email delivery and client-side ordering cannot be guaranteed.

Outlook/other clients may ask the user to accept updates; do not promise automatic calendar
insertion or removal. Declining in Outlook does not change Sidequest attendance in V1.
Explain that users must leave in Sidequest. Authorized joined users can download the
current invitation ICS only while the Event and Quest are Active and the Quest has not
ended; suspended/cancelled Quests must not offer a new active invitation. Download is a
recovery action, not a replacement for sending updates/withdrawals.
Previously downloaded or emailed content cannot be remotely erased when access is revoked.

---

## 56. Technical and Deployment Baseline

| Concern | Accepted selection |
|---------|--------------------|
| Runtime | .NET 10 LTS, ASP.NET Core Blazor Web App; pin the SDK and compatible packages during foundation |
| UI | Fluent UI Blazor, subject to the compatibility gate in section 40; static SSR plus Interactive Server where needed |
| Persistence | EF Core with SQL Server/Azure SQL; one database and migration stream |
| Authentication | Microsoft.Identity.Web / standard Entra OIDC integration; server-held tokens and application authorization |
| Directory | Graph adapter for user/group selection and background group expansion only; least-privilege approved tenant permissions, no group authorization |
| Jobs | ASP.NET Core `BackgroundService`, durable SQL outbox/scheduled work and leased claiming |
| Images | Private Azure Blob Storage, mediated authorized delivery |
| Email | Azure Communication Services Email adapter, subject to tenant/provider approval; local capture provider in development |
| Hosting | Azure App Service with WebSockets and Always On, Azure SQL, Blob, Key Vault, Application Insights |
| Deployment | Infrastructure as code using Bicep; GitHub Actions with federated Azure identity, no stored cloud credentials |

Start with the four projects in section 36 and feature folders for Identity, Events,
Quests, Notifications, Media, and Administration. Add unit, integration, and browser
test projects. A separate worker project is unnecessary for V1; extraction must not
change domain contracts.

### Durable work

Commit domain changes, audit, and outbox entries in one database transaction; never
send Graph/email/blob requests inside that transaction. Dispatchers use expiring leases
and atomic database claims so multiple instances cannot intentionally process the same
work concurrently. Delivery is at least once, with idempotent consumers, stable delivery
keys, and provider idempotency when available.

Poll due work at least every 30 seconds while healthy. Retry transient failures with
bounded exponential backoff/jitter and `Retry-After`; after eight failed attempts move
work to a visible dead-letter state. Permanent invalid-recipient/configuration errors
dead-letter immediately and raise an actionable alert. An administrator can inspect a
redacted error and replay after correction using the same logical delivery key. Replays
still apply current authorization, lifecycle, preference, and calendar ordering rules.
Provider adapters can mark a known operator-correctable dependency failure with
`DomainException.IsPermanentDependencyFailure` while retaining `DependencyUnavailable`
for HTTP/UI presentation. The marker is valid only for that category and defaults to
false, preserving ordinary transient outage retries. The queue dead-letters marked
failures on their first failed claim without persisting raw provider messages; handlers
still do not own lease, attempt or work-status updates.

Leases expire within two minutes and are renewed for long-running jobs. On restart,
claim expired work and reconcile missed completion/reminder jobs against current time;
never send a reminder after its Quest starts. Job adapters must expose failures rather
than replacing them with successful placeholders.

### Blazor, installation, and scaling

The Blazor WebAssembly PWA template is not the selected application architecture.
For the server-rendered app, provide HTTPS, a web manifest, icons, install guidance,
and browser-required installation assets. Provide a dedicated static offline fallback
page with a small client-side renderer for a narrowly scoped local joined-Quest snapshot;
it must work on an offline cold launch without a Blazor Server circuit.
This does not require migrating the main application to WebAssembly.

Only the following Quest data is stored in that snapshot: identifiers needed to associate
records with the last signed-in account, title, location, UTC start/end, inherited Event
time zone, last-known lifecycle status, and last successful refresh timestamp. No
descriptions, images, participant/owner contacts or rosters, invitation lists, notification
bodies, or authentication tokens. Cache only joined Quests, not followed, invited-only,
discovered, or owned-only Quests.

The service worker may cache versioned public static assets and the offline fallback,
but must not generically cache authenticated HTML, API responses, images, or tokens.
Use an explicit per-account IndexedDB snapshot written only from an authorized online
joined-Quest query. Treat local storage as untrusted display data, never authorization.
All creation/editing/participation requires online server authorization; no offline
mutation queue. Do not promise installation on every browser or guaranteed browser storage.

Cache safeguards accepted in D27:

- Refresh the snapshot on successful online joined-Quest loads and after changes.
  Successful leave/removal or authorization denial removes affected cached records
  on the next contact; explicit sign-out/account switching clears the prior snapshot.
  A failed refresh never pretends that cached data is current.
- Show "Offline - last refreshed ..." and state that times, locations, status, and
  access may have changed. A cancellation learned online removes the Quest from the
  actionable upcoming list and either removes or clearly marks its cache entry.
- Allow only the last signed-in account's snapshot in the browser profile; do not
  provide an offline account switcher. On shared devices, anyone able to use that
  browser profile could read cached details. Do not claim account partitioning is
  encryption or offline authentication.
- Maximum cache age is 24 hours after the last successful authorized refresh.
  Expired snapshots are not rendered and are purged when the app next runs. The browser
  may evict data sooner. Production use still requires organizational data-handling approval.
- Inform users that basic joined private Quest details are stored on their device.
  Revocation/logout on a different device cannot immediately erase an offline copy.
  Online revocation guarantees elsewhere apply to server access, not to these past copies.

Use short-lived EF Core contexts per operation (for example via `IDbContextFactory`),
not one tracked context per long-lived Blazor circuit. Revalidate sessions/resource
permissions server-side; reconnect does not restore stale authorization.

V1 begins on one always-on web instance with durable shared state. Before scaling out,
verify session affinity/SignalR requirements, shared Data Protection keys, worker leases,
and reconnect behavior. Background work must not depend on a browser circuit staying open.

### Authoritative technical references

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core):
  .NET 10 is LTS, supported through November 14, 2028; use current supported patches.
- [Fluent UI Blazor](https://github.com/microsoft/fluentui-blazor): MIT license;
  verify the selected release against .NET 10 rather than assuming compatibility.
- [Blazor PWA guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/progressive-web-app?view=aspnetcore-10.0):
  WebAssembly-oriented guidance is not a ready-made Interactive Server PWA template.
- [RFC 5545](https://www.rfc-editor.org/rfc/rfc5545), [RFC 5546](https://www.rfc-editor.org/rfc/rfc5546),
  and [RFC 6047](https://www.rfc-editor.org/rfc/rfc6047): calendar format, scheduling, and email transport.

---

## 57. Security, Data Handling, and Operational Requirements

Use HTTPS, secure server-side authentication cookies, antiforgery protection for
state-changing HTTP endpoints, restrictive cross-origin policy, output encoding, and
server-side validation. Enforce tenant/resource boundaries for every application entry
point. Rate-limit invitations, requests, directory search, and uploads.
Directory search returns only minimal identity/contact data to authorized users.

Images accept JPEG, PNG, and WebP only, at most 2 MiB (2,097,152 bytes) and 20 megapixels; validate actual
decoded content, not filename/MIME alone. Re-encode to strip metadata and reject
malformed/decompression-bomb inputs. No SVG, arbitrary remote URL import, or executable
content. Pending/unvalidated media is not served. Authorize every read via the app;
Blob containers are not public. The upload workflow records pending work,
handles partial failure, and removes orphaned temporary assets after 24 hours.

No Quest content is sent to an AI provider in V1. Any future AI feature requires a new
scope decision plus approved provider, data handling, explicit user action, and cost limits.

Secrets remain in deployment configuration/Key Vault; prefer managed identities.
Logs exclude tokens, full email bodies, member rosters, and private Quest descriptions.
Audit sensitive actions without copying complete private content into globally readable
logs. Administrative delivery diagnostics are redacted and do not confer Quest access.

Before production, approve data residency and retention for user/contact data, Quest
content, audit history, notifications, media, and the local joined-Quest cache.
Local caching of private Quest basics needs explicit employee-data/device-policy approval.
Archive is not a retention policy.
Do not implement arbitrary irreversible retention cleanup until that policy is approved.
Use synthetic data outside production until a non-production data policy is agreed.

Accepted release targets: 300 concurrent signed-in users; p95 application query/command
latency below two seconds excluding interactive sign-in and external providers; 95% of
eligible notification work submitted to a healthy provider within two minutes; reminders
submitted within two minutes of their due time when healthy. These are acceptance targets,
not promised production SLAs. Include database size, test mix, and host sizing in results.

Expose liveness/readiness without sensitive data. Monitor failed authorization spikes,
application errors, SQL availability, queue age/dead letters, Graph throttling, image
failures, and email rejection. Assign an operational owner before launch.
Accepted recovery targets are RPO <= 1 hour and RTO <= 4 hours; verify SQL/Blob restore
and document which external effects (such as already-sent email) cannot be rolled back.

---

## 58. Acceptance Scenarios

These are release requirements, not optional examples. Feature tasks reference these IDs
and add focused tests for their own edge cases. Use a controllable clock and deterministic
directory/provider fakes, plus real SQL Server integration tests for SQL behavior.

| ID | Given / when | Required outcome |
|----|--------------|------------------|
| A01 | Wrong-tenant or excluded guest signs in | No application access or local administrator bootstrap |
| A02 | User knows another Event's private Quest/image URL | No content, roster, count, or existence disclosure |
| A03 | Event manager opens another owner's Quest | Can moderate with reason; cannot edit; private moderation access is audited |
| A04 | Administrator without membership accesses content | Denied; reasoned ownership recovery works without granting content-reading privilege |
| A05 | Source group changes or Graph fails after a bulk add/invite | Existing individual memberships/invitations are unchanged; access performs no group verification; pending directory expansion reports failure without changing permissions |
| A06 | Individual Event membership removal or private invite revocation occurs | Subsequent authorization denies access; participation ends; pending content suppressed; prior calendar withdrawn |
| A07 | Concurrent request approvals or repeated Event invitation acceptance occur | One individual membership, one logical decision notification, complete history |
| A08 | Follow then join, leave, repeat commands, or race join/follow beyond suggested capacity | Mutually exclusive None/Following/Joined state; joining unfollows atomically; leaving never restores following; correct counts, no duplicate delivery, no hard capacity block; Follow cannot silently leave a joined Quest |
| A09 | Private Quest invitation issued, forwarded, revoked, or accessed by a non-member | Named Event member can view and Join/Follow immediately without Accept/Decline; no automatic attendance; forwarded/non-member access denied; revocation removes invitation access; historical access otherwise persists |
| A10 | Event/Quest transition or edit violates state/date rules | Explicit conflict/validation; no partial state/audit/outbox changes |
| A11 | Event with active private/public Quests is cancelled | Atomic child cancellation, preserved history, correct notifications and calendar withdrawals |
| A12 | Quest is suspended, edited by its organizer, reinstated, then completed | Organizer edits are audited without enabling joining or restoring calendars; only Event moderator reinstates; latest details in same-UID calendar restoration; no completion cancellation |
| A13 | DST gap/overlap, midnight boundary, or differing user/Event zone | Quest inherits Event zone with no override; gap rejected, overlap explicitly resolved, containment and UTC/user-local display correct |
| A14 | Join, edit, leave, rejoin, cancellation, and retries | Same UID; increasing sequence for changes; exact retry payload; no roster leakage or intentionally stale later send |
| A15 | Process dies after DB commit or provider acceptance; two workers race | Outbox survives, leases recover, logical notifications deduplicate; uncertain email delivery documented |
| A16 | Attendee configures X hours, Quest reschedules, attendance changes, or user joins inside reminder window | Joined users only; numeric hour lead time respected; obsolete work replaced; no follower, departed-attendee, duplicate-revision, stale, or post-start reminder |
| A17 | Old edit races with suspension, revocation, or another edit | Invariants maintained; conflict shown instead of silently overwriting |
| A18 | User searches duplicates or opens discovery as non-member | All published Active Events are discoverable; summary only, no draft/private content leaks; similarity threshold and overlap tested |
| A19 | Equal owners manage ownership, two owners concurrently remove themselves, or last eligible owner leaves Microsoft | Equal permissions; ordinary changes retain an eligible owner transactionally; departed accounts lose access; audited administrator replacement only after verified last-owner departure; preserve at least one administrator |
| A20 | Image exceeds 2 MiB/20 megapixels, is malformed/unsupported, or template contains injection | Explicit safe failure and exact size-boundary enforcement; no public unvalidated asset or executable template; no AI generation path in V1 |
| A21 | Mobile/keyboard/screen-reader user creates/joins a Quest and cold-launches offline | Accessible 360px primary flows; cached joined-Quest basics render without server circuit; no other protected content cached or offline mutations; stale labels, expiry, logout/account-switch clearing, and refresh revocation behavior verified |
| A22 | Email/Graph unavailable, poison job, or deployment restart | UI/domain results remain truthful; retries/dead letters visible; recovery does not replay obsolete content |
| A23 | Supported Outlook client receives invitation/update/withdrawal/restoration | Calendar behavior recorded through a real approved test mailbox; limitations reflected in UI |
| A24 | Representative 300-user load and backup-restore exercise run | Section 57 latency, queue, and recovery targets measured and met or explicitly renegotiated before release |
| A25 | Event reaches its local end boundary, is rescheduled, or completion processing restarts | Automatic Completed state at the current date boundary; no new activity even with late jobs; repeat-safe cleanup/history; no completion calendar withdrawals; manager may archive after child cleanup |
| A26 | Bulk group expansion encounters pagination, duplicates, ineligible users, throttling, partial application, retry, or concurrent removal | Frozen eligible recipient list; visible failures/progress; ordinary individual add/invite semantics; no duplicate effects, stale reactivation, ongoing group grant, or automatic group synchronization |

At minimum, automate all allow/deny cells in section 49 across representative lifecycle
states, not merely the happy path. Use xUnit for domain/integration tests, bUnit for
components, and Playwright for browser journeys. Tests must not depend on live Graph,
or email except clearly separated deployment smoke tests with approved test accounts.

---

## 59. Multi-Agent Development Plan

Multiple development agents should work on bounded work packages, not independently
invent the architecture or each build a complete application. This section is a
development coordination contract, not a request to launch agents now.

### Pull requests and GitHub identity

All repository changes must be delivered through pull requests, including documentation,
configuration, infrastructure, and agent-authored work. Create a dedicated task branch;
do not commit or push directly to the default or integration branches. Integrate feature
branches through pull requests, not direct pushes or unreviewed local merges.

Use the GitHub account `vaclav-pekarek-microsoft` for commits, pushes, and pull requests.
Before publishing, verify the authenticated account and use a Git author/committer
identity associated with it. Do not substitute a bot, service account, or another user.
Missing or mismatched authentication blocks publishing, not a reason to bypass this rule.
Preserve required assistant attribution in commit trailers.

All sub-agents inherit this policy. The integration owner coordinates PRs and shared-file
changes; that role does not permit direct changes to the default/integration branches.
Automatic PR merging is authorized after verification and configured review/CI requirements
pass. Never bypass PRs or push directly to main. Root `AGENTS.md` makes these rules
discoverable to later agents.

### Foundation gate and dependency graph

| Milestone | Work / exit gate | Parallelism |
|-----------|------------------|-------------|
| M0: Specification approval | Accept the reconciled baseline and record open external gates; completed by D34, with operational/deployment owner assignment still required before provisioning | Documentation only |
| M1: Foundation | Solution scaffold, .NET/Fluent spike, auth/policy contracts, data model, baseline migration, error/outbox envelopes, CI and test fixtures | One integration owner; no competing schema or shared-interface edits |
| M2: Core modules | Identity/Events, Quests, and notification delivery foundation against frozen M1 contracts | Up to three independent work packages |
| M3: Integration and secondary features | Wire membership/Quest events to delivery; complete uploads, admin/templates, dashboard/PWA/offline basics | Parallel only where file ownership and dependency gates permit |
| M4: Release hardening | Cross-feature authorization, concurrency/recovery, Outlook, accessibility, load, restore, deployment | Coordinated integration; no independent contract changes |

Dependency graph: M0 -> M1 -> M2 -> M3 -> M4. Within M2, Quests depend on the frozen
Event-access interface, not an unfinished Event implementation; messaging depends on
frozen event envelopes, not direct calls into feature internals. Stubs/fakes are for
contract tests only. M2 is not complete until real module implementations are integrated.

M1 verification (2026-09-14): the strict Release build and
[CI run 34861029210](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34861029210)
passed 1,298 unit cases, 324 real migrated-SQL cases, and 35 browser-project cases,
including seven actual Linux Chromium journeys. All discovered cases executed and
passed. The browser journeys verify synthetic admission, Fluent binding/dialog content,
360px keyboard interaction without horizontal overflow, protected navigation, and logout.
Public XML documentation is build-enforced. This establishes the M1 foundation gate,
not M2 business workflows, M4 full-product acceptance, or live provider approval.

M2 combined acceptance subsequently passed in Linux CI
[34896985551](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34896985551):
1,549 unit, 850 real-SQL, and 45 browser-project cases, including 17 actual Chromium
journeys, all executed with zero failures/skips. The 104 composition cases connect
actual Event/Quest producers, lifecycle reconciliation, outbox expansion, queue
ownership, reminder freshness, calendar rendering and delivery. An unchanged regression
exposed and verifies the correction for premature Quest-completion acknowledgement;
the original durable job now completes at its immutable cutoff. Real UI acceptance
also verifies private access/revocation, calendar download, stale-editor input retention,
and 360px keyboard/no-overflow behavior without lost prerender clicks. This establishes
the M2 core integration gate, not completion of M3 secondary features, M4 full-product
acceptance, or any live-provider/release approval.

M3 combined acceptance passed in Linux CI
[34967281852](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34967281852):
1,845 unit, 1,019 real-SQL, 96 browser-project cases and 54 Node regressions,
all executed with zero failures/skips. The composed host includes private covers
and cleanup, administration/template delivery, the dashboard and bounded offline
basics. Browser acceptance includes real transport reconnect, current-cookie
identity checks, unsaved-input preservation, private-access refresh and the
controlled native-sign-in missing-generation continuation. Administration and
notification projections also reauthorize before becoming usable after reconnect.
SQL regressions prove completion work reserves its key range before insertion,
preserving the existing Event-first Serializable transaction and explicit conflict
policy without automatic mutation replay.

This is application-level combined acceptance, not a live tenant/provider approval,
supported Outlook or physical-device certification, measured 300-user release load,
SQL/Blob restore evidence, operational-owner assignment or production deployment.
M4 must still establish those release requirements; bounded provisioning conflicts
are reported, not claimed to have been eliminated.

Shared M2 integration contracts:

- `ChangeEnvelope.AffectedUserIds` captures the action targets separately from the actor,
  observing recipients, and prior attendees. Membership additions/decisions, joins/leaves,
  attendee removals, and access removals require nonempty targets included in `RecipientIds`.
  Delivery must never infer targets by joining timestamps or mutable membership/participation
  state. Targeted legacy payloads without this metadata fail explicitly rather than guessing;
  other change kinds may omit it. One UTC operation instant remains required for audit
  consistency, not as a recipient-identity key.
- `ScheduledWork.DueUtc` is the mutable next execution/retry instant, not a checksum
  for the original business deadline. Completion payloads retain their captured end
  instant; handlers validate the work/resource identity and current resource deadline,
  then apply the current-time guard. Retry backoff and administrator replay may change
  `DueUtc` without invalidating an otherwise valid completion payload. A premature
  claim of a still-current completion intent must fail explicitly, not return success:
  success would let the dispatcher acknowledge unfinished work and lose its future
  completion. The queue retains bounded retry/dead-letter ownership; handlers never
  alter leases, attempts or retry scheduling themselves.
- Event cancellation emits one Event-level status audience plus attendee-only child
  withdrawal envelopes using `EventCancelled` with a Quest identifier. The parent
  audience includes registered effective members and affected Quest recipients.
  Each affected attendee retains a distinct per-Quest calendar withdrawal, while
  followers, invitees, and non-attending owners do not receive redundant per-child
  status email. Direct Quest cancellation keeps its full Quest-specific status audience.
- `ISidequestDbContext.LockEventAsync` acquires the parent Event lock first within
  an explicit Serializable transaction, before transactional authorization or child
  reads. Resolve immutable parent IDs before starting that transaction. Provider-specific
  lock hints and SQL deadlock-to-Conflict translation stay in Infrastructure. Translation
  covers result-reader consumption as well as command execution, saves, and transaction
  completion: SQL can report a deadlock while rows are read after execution has returned.
  Preserve provider cancellation and unrelated failures; never retry a caller-owned
  transaction or hide its rollback behind a successful result.
- Pending completion scheduling uses
  `ISidequestDbContext.HasPendingScheduledWorkForUpdateAsync` to reserve the
  deduplication-prefix range with write intent in that same Serializable transaction.
  A shared existence read followed by insertion can deadlock independent Quest
  publications on an empty or sparsely populated schedule index. The persistence
  boundary owns SQL lock hints; it neither saves nor commits. Pending/Processing
  matching, immutable completion deadlines, and explicit conflict recovery remain
  unchanged. This does not serialize tests or automatically replay user commands.
- The Event-owned `IEventLifecycleReconciler` stages overdue parent completion,
  pending membership cleanup, and Quest-side lifecycle effects in the caller's locked
  transaction. It never saves or commits. Persist system reconciliation separately
  before a requested command's lifecycle rejection can roll it back; never commit an
  unvalidated user mutation. Quest-side cascades do not depend on the reconciler.
- Cancelled unpublished Quests retain draft privacy after cancellation or archival:
  only their current eligible member owners may read them, and moderation never exposes
  them. A retained Draft-to-Cancelled history record identifies this case without
  a new schema field; every direct content/delivery query must honor it.
- `QuestDateFilter` supplies optional inclusive lower/exclusive upper UTC start-instant
  bounds to an additional `IQuestService.ListAsync` overload. Apply the same predicates
  before count and paging. Convert date controls using the selected Event zone, or
  visibly labeled UTC for cross-Event lists; do not filter an already-paged result.
- The calendar recovery HTTP boundary uses authenticated
  `GET /notifications/calendar/{questId:guid}` and delegates current authorization to
  `INotificationService.DownloadCalendarAsync`. Return exact UTF-8 calendar content as
  `text/calendar; charset=utf-8; method=REQUEST`, attachment `sidequest.ics`, with
  `Cache-Control: no-store` and no range/cached-version processing. Initialize only the
  HTTP request's scoped authentication provider from its middleware-validated principal;
  never capture an HTTP principal in an interactive circuit or renew its session deadline.
  Forward request cancellation and retain safe error responses. Startup now maps this
  endpoint and registers the real notification service, recipient calendar renderer,
  ACS adapter and durable queue dependencies. Passing isolated endpoint or composition
  tests does not establish full Event/Quest integration. Startup registers real Event
  and Quest services and lifecycle adapters, then verifies exactly one handler for each
  supported work type before starting durable background processing. Combined acceptance
  remains an integration milestone, separate from feature-level evidence.
- Delivery configuration uses `Delivery:Email` (verified `SenderAddress`, secret-store
  `ConnectionString` or managed-identity HTTPS `Endpoint`, optional
  `ManagedIdentityClientId`, and `SubmissionTimeout`) and `Delivery:Work`
  (`PollInterval`, `LeaseDuration`, `ReminderLateness`, `Concurrency`). Defaults are
  60-second submissions, 10-second polling, 90-second leases, two-minute maximum reminder
  lateness and four worker loops. Invalid timing/concurrency settings fail registration.
  Missing provider configuration never reports successful delivery; secrets belong in
  user secrets or the deployment secret store, not committed configuration files.
- Event resource limits bind from `Events:Limits`. The real Graph adapter binds
  `Directory:Graph` (tenant, approved workforce extension/value/policy and bounded
  expansion settings) and `Directory:Credentials` (tenant, client ID and protected client
  secret). Any configured directory tenant must match the authenticated tenant.
  Missing policy or credentials fail directory operations explicitly; merely starting
  the app never approves workforce policy or makes a provider call. No synthetic directory
  adapter is installed for development.

### Ownership map

M3 starts from the merged M2 baseline. Media uses `IMediaService` for authorized
cover upload/removal/read operations, `IImageSanitizer` for bounded actual decode and
metadata-free re-encoding, and `IPrivateMediaStorage` for private provider I/O outside
SQL transactions. Uploads retain the previous cover on failure or stale-editor
conflict. Reads serve only ready, currently assigned covers; explicit moderation
reads retain the same authorization and audit requirements as Quest content.
Pending/failed uploads receive durable `media.cleanup.v1` work for their immutable
24-hour expiry; final attachment must reject an expired upload. Separately identified
and audited cleanup intent also handles media removed by an explicitly authorized
unpublished-Draft deletion, so adding a cover does not permanently disable the
accepted deletion workflow. Such intent must survive deletion of the Quest and asset
metadata, rather than relying on foreign keys or later lookup of a removed Blob key.
Cleanup never removes a currently attached cover or implements unapproved retention
of ready historical assets. The cleanup handler is added to startup verification only
when the feature is composed. The integration owner then registers
`WorkHandlerRequirements(RequireMediaCleanup: true)` alongside the Media handler:
startup requires exactly one of all six handlers. Core-only composition still
requires its original five and rejects an undeclared Media handler; declaring Media
without its handler also fails before polling. Validation resolves and disposes an
isolated scope without executing work.

The media implementation dependency baseline is Azure.Storage.Blobs 12.29.2 and
SkiaSharp 4.152.0 with matching SkiaSharp.NativeAssets.Linux.NoDependencies 4.152.0;
all three published NuGet packages declare MIT licenses. These provide
.NET 10-compatible private storage and actual cross-platform image
decoding, not upload validation or provider approval by themselves. Keep image
processing and Blob SDKs in Infrastructure; no AI package or interface is introduced.
The integration owner retains startup, navigation, shared contracts, migrations and
dependency ownership while Media, Administration/templates, and Experience work in
separate task worktrees. Do not infer completion from the presence of interfaces.

| Work package | Owns | Must not independently change |
|--------------|------|-------------------------------|
| Integration owner | Solution/project/package configuration, shared contracts, DbContext composition, migrations, app startup, navigation/layout, CI/Bicep, cross-feature tests | Accepted product behavior without updating this specification |
| Identity and Events | Feature folders for identity adapters/policies, Event lifecycle, audience/membership, Event screens and tests | Quest internals or delivery provider implementation |
| Quests | Quest lifecycle/participation/moderation, Quest screens and tests | Event membership source of truth, global roles, shared calendar delivery pipeline |
| Notifications and calendar | Outbox dispatcher, recipient policy against shared access queries, preferences, templates, calendar sequencing, delivery screens/tests | Feature aggregate transitions or authorization shortcuts |
| Media | Validated 2 MiB uploads, Blob adapter, cover components/tests | Quest ownership policy, global provider configuration format, or deferred AI features |
| Administration and experience | Admin feature screens, dashboard composition, install/reconnect/accessibility flows, narrowly scoped offline joined-Quest cache | Shared shell/startup/navigation without integration-owner coordination |

Work packages are not a requirement to run six agents simultaneously. Start with two
or three feature agents after M1; the coordinating developer/agent owns integration.
Only assign work when its contracts and dependencies are ready.

### Agent task contract

Each assignment must include:

- Objective and bounded completion criteria, relevant specification sections and Axx IDs.
- Base branch/commit, allowed files or feature directories, and explicitly forbidden shared files.
- Input/output interfaces, event versions, authorization/lifecycle assumptions, and dependencies.
- Required focused tests and the commands established in M1; no invented success claims.
- Handoff containing changed files, behavior, migration needs, test results, unresolved blockers,
  and integration steps.

All C# work follows pragmatic SOLID principles, cohesive responsibilities, dependency
inversion, clear naming, nullable safety, cancellation-aware async, and explicit failures.
Every public C# type/member must have meaningful XML documentation, including public
constructors, properties, methods, fields/constants, enum members, interfaces, and
handwritten test APIs. Document parameters, returns, relevant exceptions, units,
nullability, state transitions, and side effects; use inherited documentation only
where it genuinely describes the implementation. Missing public documentation fails
the build through XML generation and warnings-as-errors. Comment quality remains a
review requirement, not something a passing compiler check alone establishes.
The user-selected C# guide is pinned and linked in
`.github\instructions\csharp.instructions.md`; all agents follow it alongside these
requirements. Apply `.editorconfig`, keep handwritten public types in matching
individual files, and review conformance before integrating the foundation.

Give each concurrent agent a separate branch/worktree where available. Do not allow
unsynchronized edits in a shared working tree. Never let two agents own the same file
at once; serially integrate changes to shared pages/components.
Only the integration owner generates/applies migrations and merges the EF model snapshot.
Feature agents supply mapping changes and migration requirements for coordinated integration.
Notification template revisions are append-only through every synchronous/asynchronous
EF save overload, including calls that disable state acceptance; correction appends a
new revision rather than rewriting history. Delivery payloads already use
`nvarchar(max)` in both the model and initial migration. Larger rendered snapshots
need real persistence evidence, not an unnecessary widening migration inferred from
the model's earlier blanket string-length default.

Contract changes are proposed to the integration owner, recorded here or in a directly
related architecture decision, and accepted before dependents change. No speculative
interface renaming, alternate enums, duplicated access checks, or new infrastructure
packages per agent. A blocked agent reports the dependency rather than bypassing it.

Integrate small vertical slices regularly through pull requests into the integration branch; rerun the affected
contract and integration tests after each merge. Do not defer all integration to M4.
Never give agents production secrets or unrestricted production deployment/migration duties.

---

## 60. Definition of Ready and Done

**Ready for a feature agent:** accepted scope and relevant baseline, stable interfaces,
assigned file ownership, dependency milestone complete, concrete acceptance IDs, and a
testable outcome. A screen alone is not a complete feature if its domain, authorization,
audit, delivery, or persistence path is unwired.

**Done for a feature:** end-to-end implementation, server-side permission/state validation,
concurrency and idempotency behavior, required audit/outbox effects, accessible success/
empty/error states, focused passing tests, and updated directly related documentation.
No silent fallback providers, TODO authorization, fake deliveries, or unused adapters.
Public C# APIs are XML-documented and the change meets the C# standards in `AGENTS.md`.
Submit the change through a pull request under `vaclav-pekarek-microsoft`; integration
is complete only after the PR is merged under the agreed review process.
Record any unavailable external verification honestly.

**Done for V1:** all in-scope work integrated; solution builds and relevant full checks
pass; all section 58 scenarios have evidence; no unresolved blocking defects; exact package
versions/licenses and deployment configuration recorded; production approval gates met;
recovery and operations responsibilities assigned. AI implementation and approval are
not V1 release requirements.

---

## 61. Decisions Requiring Acceptance or External Approval

### Accepted decision log

| ID | Accepted on | Decision |
|----|-------------|----------|
| D01 | 2026-09-14 | Listed-only Event discovery. All published Active Events are listed to eligible signed-in users. Non-members see name, dates, time zone, discovery summary, and owner contact and may request access. No Unlisted mode; discovery/direct links do not grant membership or reveal Quests/member lists. |
| D02 | 2026-09-14 | Private Quest invitations may target existing Event members only. Non-members must first obtain Event membership through the normal Event access process. A Quest invitation never grants Event membership or bypasses that boundary. |
| D03 | 2026-09-14 | Event owners may inspect published private Quests through an audited moderation view exposing content, Quest owners, and moderation history, but no attendee/follower/invitation rosters. They may suspend with a reason, not edit. Disclose this exception during private Quest creation. Drafts remain visible only to Quest managers. Owner terminology refined in D18. |
| D04 | 2026-09-14 | Quest visibility can change while drafting but is fixed at publication. Switching Public/Private afterward requires a new Quest; no in-place audience conversion in V1. |
| D05 | 2026-09-14 | Event cancellation requires a reason and impact confirmation, then atomically cancels Draft/Active/Suspended child Quests, preserves history, notifies affected users, and withdraws attendee calendars. Completed/Archived Quests are unchanged. This is an explicit exception to ordinary Quest ownership separation. |
| D06 | 2026-09-14 | Events have an explicit Completed state, set automatically when the Event ends. New participation stops and historical access remains; managers can archive afterward. Child Quest cleanup subsequently accepted in D13. |
| D07 | 2026-09-14 | Superseded/refined by D18: all owners have equal owner-list management authority; no primary/co-owner distinction or normal transfer operation. The earlier exactly-one-primary-owner rule no longer applies. |
| D08 | 2026-09-14 | Administrators may perform narrow audited ownership recovery without general content access/editing. D18 narrows this to verified departure of the last eligible owner; recovery can establish the replacement's required Event membership. |
| D09 | 2026-09-14 | Superseded by D15. Previously accepted 15-minute group-membership verification; no longer applies because groups are not used for authorization. |
| D10 | 2026-09-14 | Confirmed loss of effective Event membership ends attendance/follows, revokes private Quest invitations, suppresses content delivery, and withdraws previously sent calendars while retaining history. Readmission does not restore participation or invitations: explicit rejoin/refollow/reinvitation is required. |
| D11 | 2026-09-14 | Service email/in-app messages and applicable calendar invitations/updates/withdrawals are mandatory; users may disable optional activity email and reminders. Explain required messages before joining. No global opt-out from all service email. |
| D12 | 2026-09-14 | V1 must include an in-app notification inbox and administrator-editable email templates with safe overrides, preview, and version history. Group-audience scope revised by D15 to one-time background group-to-individual add/invite, not ongoing group membership. |
| D13 | 2026-09-14 | Accept the section 51 Quest lifecycle and Event-completion child cleanup, with organizer editing permitted while Suspended. Suspension still blocks new participation and withdraws calendars; editing does not reinstate. Only an Event manager reinstates with a reason. Completion retains historical calendars; no reopening/unarchiving in V1. |
| D14 | 2026-09-14 | Accept Event membership requests, invitations, and direct adds as specified in section 50: manager approval/direct add grants immediately; invitations require acceptance and expire after seven days or Event end; rejection requires a visible reason; retry requests are rate-limited; no new membership after Event end; draft audience setup is notification-free; actions are repeat-safe and audited. |
| D15 | 2026-09-14 | Event access depends only on individual membership. Selecting a group for add/invite expands its members in the background into ordinary individual memberships/invitations. No ongoing group audience, synchronization, group authorization, or membership-freshness checks. Source group changes do not affect existing access. Supersedes D09 and the group-audience portion of D12. Bulk safeguards subsequently accepted in D16; manager-removal guard accepted in D17. |
| D16 | 2026-09-14 | Accept section 50 one-time bulk-operation safeguards: eligible nested-group users, complete deduplicated recipient snapshot before applying changes, visible progress/failures, repeat-safe retries, ordinary individual add/invite semantics, skip existing members, no bulk-add reactivation of removed members, and concurrent removal taking precedence over stale work. No ongoing synchronization. |
| D17 | 2026-09-14 | Block ordinary Event membership removal or voluntary leaving while the member still owns that Event or any child Quest. Remove those assignments first, adding an eligible owner where necessary. D18 replaces the earlier primary-owner transfer rule with equal owner sets. |
| D18 | 2026-09-14 | Every Event/Quest has one or more equal owners; creator is initially one owner, without continuing special rank. Any owner may add/remove owners, including themselves, provided at least one eligible owner remains. No normal ownership transfer. If an owner leaves Microsoft, remaining eligible owners continue; if the last eligible owner departs, an Administrator assigns a replacement through audited recovery. Refines D07/D08/D17. |
| D19 | 2026-09-14 | Private Quest invitations simply give named Event members access to view and optionally Join/Follow. No Accept/Decline action or automatic attendance. Use Active/Revoked access grants, not a pending/accepted workflow; retain authorized historical access until revocation or Event membership loss. Event invitation acceptance remains unchanged. |
| D20 | 2026-09-14 | Following and Joined are mutually exclusive: joining automatically unfollows; leaving has no effect on following and never restores it. Accept the remaining participation rules: join until end without approval/hard capacity, owners not automatically attending, owner removal requires reason/calendar withdrawal but is not a public-Quest rejoin ban, and owners cannot add attendees on their behalf. |
| D21 | 2026-09-14 | Quest time zone is inherited from the parent Event, with no per-Quest picker, override, or independently stored zone. Remaining date/time defaults subsequently accepted in D22. |
| D22 | 2026-09-14 | Accept remaining date/time rules: inclusive Event dates, required Event zone fixed after publication, Quest start/end within Event dates in that inherited zone, UTC instants, Event-local plus differing user-local display, explicit DST handling, lifecycle-gated time edits with attendee updates, and no all-day/recurring Quests in V1. Completes D21 timing review. |
| D23 | 2026-09-14 | Send reminders only to joined attendees, never followers. Reminder lead time is configurable as a numeric X hours, not limited to fixed presets. Other notification defaults and numeric input bounds subsequently accepted in D24. |
| D24 | 2026-09-14 | Accept remaining notification defaults: new-Quest email off, joined/followed activity email on, attendee reminders on at 1 hour; per-user numeric lead time 0.01–168 hours with at most two decimals and optional disable. One reminder per start-time revision, immediate if joining inside the window, none after start, no repeated sends from preference toggling/rejoin. In-app activity persists when optional email is off; disabling reminders disables both reminder channels. Completes D23 defaults review. |
| D25 | 2026-09-14 | Accept section 54 recipient rules and section 55 calendar contract: durable per-recipient delivery, same UID with ordered revisions, calendar withdrawals/restoration for participation/lifecycle changes, no completion/archive cancellation, no follower/invitee-only calendar, no roster leakage, explicit delivery failures, current-access checks, and no Outlook RSVP ingestion or direct Graph calendar editing in V1. |
| D26 | 2026-09-14 | Store basic joined-Quest details (title, location, date/time) on the user's device for read-only offline use. Replaces the prior no-authenticated-offline-data assumption. No offline mutations or full content cache. Cache safeguards and remaining visibility/UX defaults subsequently accepted in D27/D28. |
| D27 | 2026-09-14 | Accept section 56 offline cache safeguards with a maximum age of 24 hours from the last authorized refresh. Show stale/last-refreshed warnings, clear on local sign-out/account switch, remove revoked/left entries on next contact, and disclose shared-device/private-data exposure and inability to revoke an offline copy immediately. Organizational data-handling approval remains a production gate. |
| D28 | 2026-09-14 | Accept remaining section 52 UX/validation rules and roster privacy: viewers see attendee names/counts, only Quest owners see follower/invitation rosters, no roster email addresses; Quest-first screens, English/locale-aware dates, 360px/WCAG 2.2 AA, plain text/field limits, advisory capacity, and non-blocking overlap/name-similarity duplicate warnings. |
| D29 | 2026-09-14 | V1 cover images are uploads only, limited to 2 MiB (2,097,152 bytes); retain optional/default covers, JPEG/PNG/WebP validation, 20-megapixel limit, metadata stripping and private authorized storage. Defer AI generation entirely, including interface/provider/jobs/UI; it is not a V1 approval or release gate. |
| D30 | 2026-09-14 | Accept .NET 10 LTS, Blazor Web App with Interactive Server, Fluent UI subject to compatibility verification, four-project modular monolith, EF Core/SQL Server/Azure SQL with one migration stream, Entra identity, Graph directory/bulk-expansion-only use, private Azure Blob uploads, and a static offline joined-Quest fallback rather than migrating to WebAssembly. |
| D31 | 2026-09-14 | Accept Azure App Service (WebSockets/Always On, initially one instance), SQL/Blob/Key Vault/Application Insights, Bicep and GitHub Actions with federated identity, in-host BackgroundService plus durable SQL outbox/schedule, leases/idempotency/retries/visible failures, and Azure Communication Services Email behind an adapter with local capture. Live resource/provider approval and Outlook checks remain gates. |
| D32 | 2026-09-14 | Accept section 57 release/operations targets (300 concurrent users, p95 app latency under 2 seconds, 95% healthy-provider submissions within 2 minutes, reminders within 2 minutes, RPO <= 1 hour/RTO <= 4 hours), monitored operations/restore evidence, section 58 acceptance scenarios, and xUnit/bUnit/Playwright strategy. Organizational retention/residency/device-data approval remains required before production data. Targets are not a promised SLA. |
| D33 | 2026-09-14 | Accept sections 59–60: foundation/shared contracts first, then 2–3 bounded feature agents in separate worktrees, integration-owner control of shared configuration/contracts/migrations/merges, frequent vertical integration, dependency-gated secondary features, acceptance-ID-based task handoffs, and end-to-end definition of done. No agents or implementation started by this decision. |
| D34 | 2026-09-14 | Approve the reconciled full V1 baseline, including supporting authorization, administrator, schema, durable job, security, and non-goal contracts. Earlier supersessions remain effective. Mark Accepted for implementation; external evidence/approvals remain open. This approval does not start implementation or launch agents; wait for a separate development instruction. |
| D35 | 2026-09-14 | All repository changes must go through pull requests using the GitHub account vaclav-pekarek-microsoft, including documentation and sub-agent work. Use task branches and matching Git author/committer identity; no direct default/integration-branch changes or alternate/bot publishing identity. |
| D36 | 2026-09-14 | Implementation is authorized. Automatically merge verified PRs when configured review/CI requirements pass, while retaining PR-only changes and the vaclav-pekarek-microsoft identity. M1 foundation begins before parallel feature work; external approval gates still apply. |
| D37 | 2026-09-14 | C# implementation must follow good engineering standards, including pragmatic SOLID. All public classes/types and members, methods, properties, constructors, fields, and enum values require proper XML documentation; enforce missing-documentation failures in builds and review semantic quality before merging. |
| D38 | 2026-09-14 | Adopt PlagueHO/github-copilot-assets-library's csharp-best-practices.instructions.md as the C# baseline for all development agents, pinned at commit ea4125167b98053f083cffcc79883221f872da30. Record its application in repository instructions and EditorConfig; retain D37's public XML documentation requirement and apply the baseline before foundation integration. |

The full reconciled baseline is accepted in D34. Superseded decisions remain documented
for traceability and must not be reintroduced as requirements.

### Baseline approval

All step-by-step topic reviews and final whole-baseline approval are recorded above.
The document is **Accepted for implementation** as of 2026-09-14.
Approval includes the supporting engineering contracts in sections 48–60:
permission enforcement, global administrator safeguards, field/schema constraints,
durable job defaults, secure configuration, and the early-version non-goals.
It does not substitute for the external evidence/approvals below or authorize starting
implementation in this documentation session.

### External gates

| Gate | Needed by | Required evidence |
|------|-----------|-------------------|
| Entra tenant/app registration, eligible-account policy, administrator bootstrap identity | Live authentication integration | Tenant owner approval; configured IDs and redirect URIs outside source-controlled secrets |
| Organizational departure verification | Live last-owner recovery | Approved process/evidence for confirming the last eligible owner left Microsoft; outages or inactivity are not sufficient |
| Graph permission set, consent, supported group expansion strategy | Live directory integration / V1 release | Least-privilege permission mapping for user/group search and one-time background expansion; pagination, nested groups and eligible-user filtering proof; no group authorization dependency |
| Fluent UI/.NET 10 compatibility and dependency licenses | M1 exit | Working SSR/Interactive Server form/dialog/validation spike and recorded package versions/licenses |
| Azure subscription, region, budget, deploy identity, operational owner | Infrastructure provisioning | Approved hosting and access configuration |
| Email provider/domain, organizer mailbox identity, test recipients | Live notification/calendar integration | Verified sender and Outlook delivery/update/cancellation smoke-test results |
| Retention, residency, employee-data/device access and recovery targets | Before production data | Organizational approval including offline joined/private Quest basics, shared-device disclosure, and documented cleanup/restore procedure |

All external gates above remain open/unverified. No live tenant/provider resources,
compatibility evidence, operational owner, or organizational policy approval is implied
by product acceptance. Record each gate's actual evidence when it is satisfied.

Unresolved external gates do not justify guessing tenant IDs, committing credentials,
or claiming integrations work. After product acceptance, core development can use
synthetic identities and deterministic fakes while approvals are obtained. Production
release remains blocked by its relevant gates.
