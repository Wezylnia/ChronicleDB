# Tests

Tests are organized by failure model rather than mirroring production assemblies.

- Unit tests cover pure state and codec behavior.
- Persistence tests exercise real files, boundaries, corruption, and lifecycle journals.
- Correctness tests compare logical histories with independent reference models.
- Recovery tests validate durable replay and crash boundaries.
- Architecture tests enforce project and build-policy constraints.

See `docs/TESTING.md` for the release validation sequence.
