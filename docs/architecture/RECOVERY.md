# v0.3 WAL recovery

Recovery scans complete WAL records, validates transaction structure, ignores transactions that end without `Commit`, and discards explicit `Abort` transactions. Transaction IDs may appear only once and WAL LSNs must be contiguous beginning at one.

Commit records have three supported forms during the v0.2-to-v0.3 transition:

- empty payload: legacy v0.2 commit; recovery assigns the next logical sequence;
- 8-byte payload: early sequence-only v0.3 development format;
- 16-byte payload: current v0.3 commit sequence plus pre-publication data-file length.

Recovered commit sequences must be strictly increasing.

Committed mutations are folded in WAL order to one final desired current state per full binary key and reconciled into the append-only physical store. The complete committed transaction list is also returned so the facade can rebuild immutable MVCC version chains in commit order.

## Physical crash-tail repair

The storage scanner can enter recovery mode and stop at the first corrupt append page without immediately modifying the file. For current v0.3 commits, the latest Commit record contains the data-file length that existed before its physical publication began. A damaged region is considered redoable only when it begins at or after that latest recovery base. Recovery truncates back to that base, rescans the trusted prefix, and replays committed WAL state.

Corruption older than the latest commit's recovery base remains fatal. Legacy v0.2 commits, which lack a recovery base, may repair only a physically partial final page. A full-sized corrupt page remains fatal without a WAL recovery base proving that it belongs to the newest append region.

This distinction prevents arbitrary older-page corruption from being silently treated as a crash tail.
