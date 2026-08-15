# ADR 0043: Idle native-agent checkpoints

- Status: Accepted
- Date: 2026-08-13

## Context

The native agent kernel retains a bounded, stable conversation in memory, but
process exit currently loses it. Pi's durable harness is a useful reference for
the distinction between settled transcript state and volatile provider/tool
effects. GhostSHELL does not implement Pi's durable operation interpreter,
effect-intent log, or branching lanes. Desktop recovery is deliberately
limited to the last fully committed idle conversation.

Persisting the kernel's entire object graph would be unsafe. An active provider
stream is incomplete and may still commit or fail. A pending tool proposal is
an approval decision, not settled conversation. Provider adapters, capability
leases, approval principals, policy authorities, and resolved credentials are
process-local capabilities and must never become session data.

## Decision

`NativeAgentSession.CaptureCheckpoint` captures a checkpoint only while the
session is `Ready`, with no provider operation, pending tool decision, or
compaction lease. The checkpoint contains the run ID, schema version,
generation, event revision, conversation revision, last sequence, last settled
tool generation, optional generated conversation title, complete stable
conversation, the last trusted provider/model route used for history
attribution, and deterministic provider-tool alias bindings. Stable assistant
reasoning summaries and provider token usage are part of the conversation.
Provider-private replay state may also round-trip
when it contains only signed/summarized Anthropic blocks, opaque redacted
blocks, or encrypted/finalized OpenAI Responses items. The payload stores those
bounded JSON items as base64 so credential-property scanning cannot confuse
opaque provider field names with executable configuration. The exact provider,
protocol, model, routed endpoint, and adapter/auth route binding is restored
with item order and tool slots. A future continuation must match all of it
before replay. Bounded user images retain only their plain file
name, verified media type, and copied bytes; restore reconstructs them through
the same signature-validating attachment constructor. No provider client, tool
definition, approval, policy authority, capability, secret reference, or
resolved secret is in the format.

The current schema-v2 payload is owned by `GhostShell.Agent`; storage treats it
as an opaque bounded JSON object. Schema v2 adds the optional bounded generated
title. Restore still accepts schema v1 and derives the legacy deterministic
first-user-message title. Restore rejects unknown fields, unsupported newer
schema versions, inconsistent revisions/generations, malformed conversation shapes,
duplicate or changed provider aliases, and values outside the current kernel
limits. Credential-shaped literal text and structured credential properties
fail checkpoint capture and restore instead of becoming durable data.
Provider raw reasoning that was intentionally suppressed from user-visible
reasoning summaries can exist only in volatile memory for its immediate tool
continuation. This includes Anthropic thinking and OpenAI reasoning text;
encrypted OpenAI continuity data remains eligible. Checkpoint capture keeps the
committed visible conversation but removes an assistant message's entire
provider-private replay state when that state contains suppressed raw reasoning.
The restored transcript therefore remains readable and continuable without
making hidden reasoning durable or replaying a partially stripped provider atom.
Restore decodes replay bytes with strict UTF-8, derives the unsafe
classification from the item kinds instead of trusting the serialized
compatibility bit, and revalidates each item's canonical provider shape against
the committed visible text, reasoning summary, and tool proposals. Hidden
chain-of-thought is therefore never made durable or smuggled through a
divergent replay transcript.

`IAgentSessionCheckpointStore` is the application port. Its SQLite adapter
stores one row per agent run, bound to its live workspace identity, including
redundant schema/generation/revision metadata and a SHA-256 digest covering the
workspace identity, run identity, metadata, and payload. Saves
run in an immediate transaction and compare the stored revision before an
upsert. An identical same-revision save is idempotent; a different or older
same-run save fails with a revision conflict. Load validates row types, size,
canonical UTC time, JSON shape, and the digest before returning the opaque
checkpoint. Delete and bounded newest-first list complete the initial durable
surface.

Migration 11 creates the dedicated checkpoint table. Migration 14 adds the
workspace binding and its newest-first index; pre-migration rows remain
unscoped and are not projected into any live workspace. Both migration
receipts are frozen alongside the existing historical SQLite fixtures.

## Consequences

The durable unit is a settled snapshot, not an external-effect journal.
Checkpoint persistence is crash-safe, but any provider request or tool action
that was active at process death is intentionally absent. A restored session
starts idle at the last committed conversation and does not infer or replay
unfinished work.

The desktop creates one `GovernedAgentRuntime` for every live workspace,
including Quick Terminal's independent workspace. Each runtime saves after
every fully completed provider/tool turn and loads only the newest valid
checkpoint from its own workspace when its chat is created. Switching
workspaces switches the active runtime and transcript; no active chat or
history catalog crosses that boundary. No pending
approval, queued prompt, provider request, tool action, or capability is
resumed. The first new prompt lazily rebinds the restored kernel session only
when the trusted current workspace manifest is identical; provider replay also
requires its original profile, protocol, model, endpoint, auth route, and vault
reference binding. A mismatch fails closed and requires Clear. Clear deletes
the durable checkpoint before resetting the visible run. If safe capture or
storage fails, the in-memory completed response remains visible and the UI
reports that local saving failed.
