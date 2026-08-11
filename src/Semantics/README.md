# Semantics

Semantics defines logical database behavior independently of file layouts and concrete indexes. MVCC owns visibility rules; History owns snapshots, roots, branch ancestry, history-domain identity, and retention meaning.

Persistence records these concepts, but it does not redefine them.
