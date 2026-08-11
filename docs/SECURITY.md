# Security Model

## Scope

ChronicleDB is an embedded storage engine. Security is therefore split between engine-level input hardening and the host application's operating-system boundary.

The engine treats persistent files as untrusted binary input when parsing them. It validates framing, lengths, counts, checksums, identities, sequence continuity, lifecycle transitions, and cross-history relationships before durable data is admitted into logical state.

## What ChronicleDB protects against

The v1.0 implementation is designed to fail closed on common accidental or malformed-storage conditions, including:

- truncated or internally inconsistent headers and records;
- CRC32C failures in durable formats;
- impossible length/count claims before large allocations;
- WAL LSN discontinuity and malformed transaction structure;
- replay into the wrong database, branch, or history domain;
- configured key/value limit violations during recovery;
- invalid snapshot/root/branch lifecycle transitions;
- cyclic or inconsistent branch ancestry;
- corruption inside a durably published physical prefix;
- incomplete crash tails where the format can prove that classification.

In-memory binary-key hashing is computed once per immutable key and uses the runtime's process-seeded `HashCode` implementation. Full key bytes remain the equality authority. This reduces repeated hashing cost and avoids exposing a fixed public hash function to adversarial key distributions.

## What ChronicleDB does not provide

ChronicleDB v1.0 does not implement:

- authentication or authorization;
- encryption at rest;
- encrypted WAL or metadata;
- cryptographic signatures or MACs for persistent files;
- secure deletion;
- tenant isolation;
- protection from an attacker with arbitrary write access to the database directory.

CRC32C detects accidental corruption; it is not a cryptographic integrity mechanism. An attacker who can replace database files can also recompute CRC32C fields.

The host application must protect the database directory with appropriate operating-system permissions, storage encryption where required, backup controls, and process isolation.

## Resource-exhaustion policy

Persistent parsers validate declared sizes against format limits and remaining file length before allocating variable-size buffers. Recovery then applies the opened database's configured logical key/value limits before replay or physical reconstruction.

The engine does not impose application-level quotas on branch count, snapshot count, total database size, or transaction rate. Applications that expose ChronicleDB operations to untrusted users must enforce their own quotas and admission controls.

## Diagnostic and terminal output

Branch and snapshot names are metadata, not shell commands. Tools escape terminal control and Unicode format characters before printing names so persisted metadata cannot inject new terminal lines, ANSI control sequences, or bidirectional-formatting controls through the Inspector.

## Dependency surface

Production assemblies use the .NET base class libraries and ChronicleDB projects; third-party NuGet packages are confined to the test toolchain. Release engineering should still run the SDK's package-vulnerability audit in an environment with network access and preserve the result with other release evidence.

## Reporting a security issue

For a private project or academic repository, report suspected security issues directly to the repository owner rather than publishing exploit details before a fix is available. A public deployment should replace this paragraph with the project's actual disclosure channel and response policy.
