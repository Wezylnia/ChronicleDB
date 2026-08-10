# Reference Repository Architecture Review

The initial ChronicleDB structure was informed by three local repositories, but it intentionally does not copy any of them wholesale. Their problem domains differ from a storage engine, so the useful lessons are principles rather than folder names.

## MIoT

Useful patterns:

- a recognizable `src/Core`, `src/Modules`, `src/Infrastructure`, `src/Apps` top-level map;
- architecture tests that parse project references and reject cycles;
- technology-specific infrastructure separation;
- ADRs for behavior whose reliability semantics must be decided before implementation;
- an explicit warning against prematurely splitting one cohesive module into many projects.

Risks observed:

- `Application`, `Abstractions`, and `Common` can become broad convergence points;
- a central application project may reference many product modules and grow into the de facto monolith;
- source-token architecture tests are helpful guardrails but weaker than exact project dependency contracts.

ChronicleDB adoption:

- keep the architecture test and ADR discipline;
- keep a small dependency-free foundation, but do not create a generic Common project;
- use exact allowed dependency sets instead of broad prefix-only bans;
- group by storage-engine risk boundaries rather than enterprise application layers.

## RepoTrustDoctor

Useful patterns:

- strong plugin seams between analyzer abstractions and implementations;
- clear composition roots for CLI, API, and worker hosts;
- deterministic orchestration and explicit artifact dependencies;
- separate engine, infrastructure, and tool concerns.

Risks observed:

- very fine-grained analyzer projects create a large project-reference fan-out;
- composition roots and integration tests must reference many assemblies;
- adding an implementation can require solution/project edits disproportionate to its code size.

ChronicleDB adoption:

- use a separate abstraction/implementation pair only for the index, where v1.5 explicitly requires interchangeable baseline and latch-free implementations;
- do not create one assembly per page type, maintenance operation, or history feature;
- use feature folders inside cohesive assemblies until a real replacement, package, unsafe-code, or process boundary exists.

## InterviewArena

Useful patterns:

- a balanced modular-monolith layout;
- thin composition roots and provider adapters behind ports;
- feature modules kept independent from application and infrastructure;
- architecture tests based on assembly dependency direction.

Risks observed:

- generic application abstractions can obscure which module owns a port;
- domain/application/module distinctions are product-oriented and do not directly model durability, recovery, or memory ownership.

ChronicleDB adoption:

- retain the balanced project count and thin composition-root idea;
- replace provider/infrastructure language with semantic, persistence, indexing, recovery, maintenance, and observability boundaries;
- strengthen the assembly inspection with project-file validation because ChronicleDB must also police build flags, package ownership, and unsafe code.

## Resulting decision

ChronicleDB uses a modular monolith with twelve initial production assemblies. This is more segmented than InterviewArena because WAL, storage, recovery, and index replacement are independently correctness-critical, but far less fragmented than RepoTrustDoctor's one-project-per-plugin shape. Semantic features that strongly share invariants—snapshots, branches, roots, and retention—stay together in `ChronicleDB.History` and are separated by feature folders.
