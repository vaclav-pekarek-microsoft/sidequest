# Private Quest covers

The Media service implements validated uploads, authorized current-cover reads,
removal, and durable cleanup. Quest cards and detail views display authorized covers;
the editor composes upload/removal alongside the text form. Host composition remains
a separate integration step; registering packages or calling the service in a test
is not live Azure or complete browser acceptance.

## Composition

Call `AddSidequestMedia(configuration)` and `MapSidequestMedia()`. Register
`WorkHandlerRequirements(RequireMediaCleanup: true)` so startup requires all six
handlers before polling. `DurableWorkRunner` accepts `media.cleanup.v1` only in
the scheduled category.

`QuestCover` displays an authorized asset. `CoverEditor` reports `CoverChanged`,
`BusyChanged`, and `AccessLost`: update only the cover/version after a successful
upload, retain unsaved text, block competing mutations, and clear protected
parent content after access loss. `ConflictDetected` blocks further changes until
an explicit reload, preserving unsaved text beforehand. Stale callbacks cannot
alter another route or reauthorized editor. A new Quest must be saved before uploading.

## Storage configuration

Bind `Media:Storage` with `ContainerName`, HTTPS `ServiceUri`, optional
`ManagedIdentityClientId`, and bounded `OperationTimeout`. A secret-store
`ConnectionString` is the alternative; never put credentials in source or the UI.
Preprovision a private container and disable account-level anonymous Blob access.
The adapter checks container privacy and never returns public URLs or SAS tokens.
SDK content logging is disabled.

Missing/invalid configuration, nonprivate containers, and definite permanent
provider responses fail explicitly. Known operator-required failures retain the
`DependencyUnavailable` UI category and immediately dead-letter cleanup work;
transient failures retain queue-controlled retries. No configured provider means
no simulated upload success.

After the SDK retry budget is exhausted, valid final-response `Retry-After`
seconds/HTTP dates or Azure `x-ms-retry-after-ms` guidance survives into durable
scheduling. HTTP dates use the injected UTC clock. The queue takes the greater
of its ordinary backoff and the provider window; windows beyond 24 hours require
manual recovery instead of an early automatic retry. Raw provider details are
not persisted with this timing metadata.

## Validation and resource bounds

Input must actually decode as a nonanimated JPEG, PNG, or WebP, with at most
2,097,152 bytes and 20,000,000 pixels. Declared length and MIME are not trusted.
Normalization applies all eight EXIF orientations before lossless PNG encoding,
strips source metadata, and preserves the oriented raster/alpha.

Five admitted uploads per account per rolling minute are recorded durably.
Two nonqueued upload slots and two read slots bound process-level work.
Two decoder slots allow up to 320,000,000 native raster bytes, excluding encoded
buffers and codec working memory; normalized content is bounded at 84,000,000
bytes. Cancellation is cooperative around bounded native operations.

Pending intent exists before provider writes. Failed, cancelled, or conflicting
uploads never replace the previous cover. Final attachment rechecks permission,
lifecycle, version, and expiry. Reads authorize before and after storage access
and serve only a ready, currently assigned cover with no-store/nosniff responses.
Moderation reads require the explicit audited path; administrator status is not
content access.

## Cleanup and retained content

Pending/failed uploads expire after 24 hours. Cleanup validates immutable work,
asset, and audit identity; early execution fails for retry rather than acknowledging
unfinished work. The queue, not the handler, owns attempts, leases and work status.

Removing a cover retains its ready historical asset. Explicitly deleting an
authorized never-published Draft stages separate audited cleanup that survives
removal of Quest/asset metadata, including unfinished provider writes. This is not
a general retention policy or authorization to delete published content.

Linux codec/worker CI, composed UI/browser behavior, provider throttling/load
behavior and live Azure/device-data approvals remain separate acceptance gates.
Offline caching must exclude `/media/**` entirely.
