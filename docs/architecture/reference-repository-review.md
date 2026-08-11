# Repository-Structure Review (Archived Design Input)

## Status

This note records repository-structure observations that informed the initial ChronicleDB assembly layout. It is historical design input, not an architectural authority. `ARCHITECTURE.md` and the architecture tests define the current project graph.

## Repositories reviewed

### MIoT

The useful lesson was disciplined separation of core concepts, modules, infrastructure, and executable surfaces, backed by architecture tests and ADRs. ChronicleDB did not copy the application-layer naming because a storage engine is organized around durability, recovery, history, and ownership rather than enterprise service layers.

The review also highlighted a failure mode worth avoiding: broad `Application`, `Abstractions`, or `Common` projects can become dependency convergence points. ChronicleDB therefore keeps `Core` intentionally small and validates exact project-reference direction.

### RepoTrustDoctor

The repository demonstrated clear abstraction/implementation seams and deterministic orchestration. Its highly granular plugin project structure was not adopted because ChronicleDB does not benefit from one assembly per page type, maintenance operation, or history feature.

The replaceable index is the main deliberate abstraction/implementation pair because later latch-free research must be compared with the managed baseline under the same transaction semantics.

### InterviewArena

The useful pattern was a balanced modular-monolith shape with thin composition roots and architecture tests. Product-oriented domain/application/provider terminology was not adopted; ChronicleDB instead names boundaries after semantic, persistence, indexing, recovery, maintenance, and observability responsibilities.

## Result

ChronicleDB uses a modular monolith with assembly boundaries where they protect one of the following:

- persistent-format ownership;
- recovery or transaction semantics;
- replaceable implementation seams;
- dependency direction;
- unsafe/native-code containment;
- executable research/test boundaries.

This review should not be used to justify new projects by analogy. New assembly boundaries require a ChronicleDB-specific ownership or replacement reason and, when material, an ADR.
