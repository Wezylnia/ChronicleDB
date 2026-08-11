# Foundation

`ChronicleDB.Core` contains small, dependency-light concepts shared across the engine: identifiers, binary-key ownership/equality, sequences, and invariant-oriented primitives.

Foundation is not a general helper library. A type belongs here only when its meaning is stable across semantics, persistence, indexing, and orchestration.
