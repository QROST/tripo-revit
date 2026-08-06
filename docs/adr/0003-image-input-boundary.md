# ADR-0003: Opaque image staging for panel and MCP input

- Status: Accepted
- Date: 2026-08-05
- Supersedes: ADR-0001 image-input deferrals only

## Context

The Revit panel and MCP need image-to-model input without giving the Revit host
an HTTP client, sending arbitrary local paths across host-control, weakening the
paid-operation journal, or persisting sensitive image origins in recovery
evidence. Image creation has two externally observable operations—upload and
generation—so treating it like one text POST would also make ambiguous outcomes
unsafe to retry.

## Decision

1. The product exposes three generation modes: text, local image, and image URL.
   Image bytes must be signature-proven PNG, JPEG, or WebP and are capped at
   10,000,000 bytes.
2. Local input is copied by the choosing process into the private shared
   `image-transfers` root. The source path and filename never cross
   host-control. The WPF preview loads an in-memory snapshot and releases the
   source file. Its byte length and SHA-256 are shown at confirmation and must
   match the subsequently staged descriptor before any paid dispatch. Preview
   decoding bounds source dimensions, total pixels, and output dimensions.
3. URL input accepts only HTTPS and is staged by the sidecar. WPF does not fetch
   a preview. The sidecar disables proxy and cookie state, resolves and pins a
   public endpoint, revalidates every redirect, and rejects non-public,
   IPv4-compatible, and NAT64 destinations.
4. Both staging paths return only an opaque descriptor: canonical transfer UUID,
   lowercase SHA-256, byte length, and detected media type. Image creation must
   receive the descriptor unchanged and validates the returned operation UUID,
   task ID, model, and image SHA-256.
5. Host-control protocol v8 advertises explicit image-stage and image-create
   capabilities. The panel fails closed when either capability is absent.
6. Image paid work uses separate durable states for upload dispatch, file-token
   persistence, generation dispatch, task-ID persistence, definitive rejection,
   and ambiguous upload/generation outcomes. A retry is allowed only with the
   same UUID and identical parameters when the journal explicitly reports that
   creation can resume.
7. Panel recovery schema v4 has a mutually exclusive image-generation slot. It
   stores only operation/task/journal metadata—never a local path, filename,
   descriptor, bytes, URL, file token, prompt, or credential. Schemas v3 and v2
   migrate with the image slot set to null.
8. The panel owns a newly staged descriptor until dispatch. Preparation failure,
   definitive rejection, reset, and session disposal perform best-effort
   cleanup. The sidecar owns cleanup after admission; journal evidence is never
   deleted as part of transfer cleanup.

## Consequences

- The same image workflow and recovery rules serve the Revit panel and MCP.
- URL staging expands host-control by one bounded typed method, but does not add
  arbitrary network access to the Revit host or host bridge.
- Recovery evidence can identify and reconcile a paid image operation but is
  intentionally insufficient to recreate image input after a crash.
- Portable tests can prove state, protocol, fetch-policy, and redaction
  contracts. Windows WPF compilation, real Revit interaction, and a paid
  provider canary remain separate acceptance gates.
