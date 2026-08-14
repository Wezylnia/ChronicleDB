namespace ChronicleDB.BranchCheck;

public sealed record HistoricalIssueCase(
    string System,
    int IssueNumber,
    string Title,
    string SourceUrl,
    string Disposition,
    BranchScenario Scenario,
    string EvidenceNote);

public static class HistoricalIssueCampaign
{
    public static IReadOnlyList<HistoricalIssueCase> Create()
        =>
        [
            MatrixOneAutoIncrement(),
            MatrixOneHistoricalIdentity(),
            YugabyteOidCollision(),
            YugabyteVectorRestart(),
            DoltCloneDrop(),
            NeonOldLsnBoundary(),
            SlateDbReaderDependency(),
        ];

    private static HistoricalIssueCase MatrixOneAutoIncrement()
    {
        var boundary = new BranchBoundary("matrixone-source", 0);
        CanonicalState creation = State(
            [("rows", "1,2,3")],
            schema: "id BIGINT AUTO_INCREMENT PRIMARY KEY; v INT",
            metadata: "creation-metadata-unreported");
        CanonicalState branchAfterInsert = State(
            [("rows", "1,2,3,10001")],
            schema: creation.SchemaFingerprint,
            metadata: "post-insert-auto-increment=10002",
            continuationToken: "10001");
        CanonicalState referenceAfterInsert = State(
            [("rows", "1,2,3,4")],
            schema: creation.SchemaFingerprint,
            metadata: "post-insert-auto-increment=5",
            continuationToken: "4");

        var scenario = new BranchScenario(
            "matrixone-27092-auto-increment",
            BranchCapabilityProfile.Create("MatrixOne"),
            boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "continuation",
                    Success(branchAfterInsert),
                    Success(referenceAfterInsert),
                    OperationClass: TraceOperationClass.GenericMutation),
            ],
            ExpectedFailingRelationId: "BC.continuation-state",
            CreationEvidence: CreationEvidenceKind.Values | CreationEvidenceKind.Schema);

        return new HistoricalIssueCase(
            "MatrixOne",
            27092,
            "CLONE does not preserve AUTO_INCREMENT state",
            "https://github.com/matrixorigin/matrixone/issues/27092",
            "open; kind/bug; severity/s0",
            scenario,
            "The report establishes equal cloned rows and divergent first post-clone generated IDs; it does not report exhaustive pre-insert destination metadata.");
    }

    private static HistoricalIssueCase MatrixOneHistoricalIdentity()
    {
        var declared = new BranchBoundary("snapshot-parent-object:287580", 0);
        var wrongCurrent = new BranchBoundary("current-same-name-object:287581", 0);
        var boundaries = new Dictionary<string, BranchBoundary>(StringComparer.Ordinal)
        {
            ["data"] = declared,
            ["metadata"] = wrongCurrent,
            ["dependencies"] = wrongCurrent,
        };
        CanonicalState branchCreation = State(
            [("parent_t", "1:snapshot-row")],
            schema: "parent_t(id,val)",
            metadata: "branch-parent-id=287581",
            boundaries: boundaries);
        CanonicalState referenceCreation = State(
            [("parent_t", "1:snapshot-row")],
            schema: "parent_t(id,val)",
            metadata: "snapshot-parent-id=287580");

        var scenario = new BranchScenario(
            "matrixone-26120-historical-identity",
            BranchCapabilityProfile.Create(
                "MatrixOne",
                supportsHistoricalFork: true,
                sourceBoundaryComponents: ["data", "metadata", "dependencies"]),
            declared,
            branchCreation,
            referenceCreation,
            [
                new TraceFrame(
                    "data-branch-diff",
                    new ObserverObservation(OutcomeClass.Rejected, null, "cannot find current same-name table id at historical timestamp"),
                    Success(null),
                    OperationClass: TraceOperationClass.BranchSpecificHistory),
            ],
            ExpectedFailingRelationId: "BC.temporal-boundary",
            CreationEvidence: CreationEvidenceKind.Values);

        return new HistoricalIssueCase(
            "MatrixOne",
            26120,
            "DATA BRANCH from a snapshot links children to a recreated same-name table",
            "https://github.com/matrixorigin/matrixone/issues/26120",
            "closed/completed; kind/bug",
            scenario,
            "The issue explicitly reports correct historical branch data but wrong parent identity in branch metadata and the protection snapshot.");
    }

    private static HistoricalIssueCase YugabyteOidCollision()
    {
        var boundary = new BranchBoundary("ysql-source", 0);
        CanonicalState creation = State(
            [("objects", "validated"), ("data", "validated")],
            schema: "validated-db-objects",
            metadata: "creation-metadata-unreported");

        var scenario = new BranchScenario(
            "yugabyte-29335-oid-collision",
            BranchCapabilityProfile.Create("YugabyteDB"),
            boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "continuation",
                    new ObserverObservation(OutcomeClass.Rejected, null, "timed out creating a new table/index after clone reuse"),
                    Success(State([("create-object", "success")], "ddl", "ordinary")),
                    OperationClass: TraceOperationClass.GenericMutation),
            ],
            ExpectedFailingRelationId: "BC.continuation-state",
            CreationEvidence: CreationEvidenceKind.Values | CreationEvidenceKind.Schema);

        return new HistoricalIssueCase(
            "YugabyteDB",
            29335,
            "Create table fails in cloned database [oid collision issue]",
            "https://github.com/yugabyte/yugabyte-db/issues/29335",
            "closed/completed; kind/bug; priority/high; GA blocker",
            scenario,
            "The stress test validates cloned DB objects and data, deletes the source, performs more DML/DDL, and fails only when creating later objects.");
    }

    private static HistoricalIssueCase YugabyteVectorRestart()
    {
        var boundary = new BranchBoundary("ysql-source", 0);
        CanonicalState creation = State(
            [("vector-index", "present")],
            schema: "vector-index",
            metadata: "index-state-unreported-at-creation");

        var scenario = new BranchScenario(
            "yugabyte-32057-vector-restart",
            BranchCapabilityProfile.Create("YugabyteDB", supportsRestart: true),
            boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "restart",
                    new ObserverObservation(OutcomeClass.Crash, null, "tserver tablet peers enter a crash loop after master leader restart"),
                    Success(creation),
                    OperationClass: TraceOperationClass.Restart),
            ],
            ExpectedFailingRelationId: "BC.recovery",
            CreationEvidence: CreationEvidenceKind.None);

        return new HistoricalIssueCase(
            "YugabyteDB",
            32057,
            "clone: Regression to cloning a DB with a vector index",
            "https://github.com/yugabyte/yugabyte-db/issues/32057",
            "closed/not-planned; kind/bug; priority/highest",
            scenario,
            "The clone incorrectly leaves vector indexes PREPARING; the latent state becomes catastrophic when the master leader restarts.");
    }

    private static HistoricalIssueCase DoltCloneDrop()
    {
        var boundary = new BranchBoundary("dolt-clone-source", 0);
        CanonicalState creation = State([], "clone-created", "creation-state-unreported");
        var scenario = new BranchScenario(
            "dolt-7106-clone-drop",
            BranchCapabilityProfile.Create("Dolt", supportsDelete: true),
            boundary,
            creation,
            creation,
            [
                new TraceFrame(
                    "delete-branch",
                    new ObserverObservation(OutcomeClass.Crash, null, "database not found followed by provider/store close panic"),
                    Success(null),
                    OperationClass: TraceOperationClass.BranchSpecificLifecycle),
            ],
            ExpectedFailingRelationId: "BC.lifecycle",
            CreationEvidence: CreationEvidenceKind.None);

        return new HistoricalIssueCase(
            "Dolt",
            7106,
            "Attempting to drop a database which was created with dolt_clone fails",
            "https://github.com/dolthub/dolt/issues/7106",
            "closed/completed; bug; customer issue",
            scenario,
            "The clone call succeeds; the later legal DROP exposes inconsistent clone/provider lifecycle state and can panic.");
    }

    private static HistoricalIssueCase NeonOldLsnBoundary()
    {
        var declared = new BranchBoundary("timeline-main", 0x3000000);
        var newer = new BranchBoundary("timeline-main", 0x3DFA4FD);
        var boundaries = new Dictionary<string, BranchBoundary>(StringComparer.Ordinal)
        {
            ["disk-consistent-lsn"] = declared,
            ["prev-record-lsn"] = newer,
        };
        CanonicalState branchCreation = State([], "timeline", "timeline-metadata", boundaries: boundaries);
        CanonicalState referenceCreation = State([], "timeline", "timeline-metadata");

        var scenario = new BranchScenario(
            "neon-506-old-lsn-boundary",
            BranchCapabilityProfile.Create(
                "Neon",
                supportsHistoricalFork: true,
                supportsRestart: true,
                sourceBoundaryComponents: ["disk-consistent-lsn", "prev-record-lsn"]),
            declared,
            branchCreation,
            referenceCreation,
            [
                new TraceFrame(
                    "restart",
                    new ObserverObservation(OutcomeClass.Crash, null, "compute fails at startup with invalid redo record"),
                    Success(referenceCreation),
                    OperationClass: TraceOperationClass.Restart),
            ],
            ExpectedFailingRelationId: "BC.temporal-boundary",
            CreationEvidence: CreationEvidenceKind.None);

        return new HistoricalIssueCase(
            "Neon",
            506,
            "prev_record_lsn for branching with old lsn",
            "https://github.com/neondatabase/neon/issues/506",
            "closed/completed",
            scenario,
            "The issue records disk_consistent_lsn at the requested old boundary while prev_record_lsn comes from a newer source state; compute startup then fails.");
    }

    private static HistoricalIssueCase SlateDbReaderDependency()
    {
        var boundary = new BranchBoundary("parent-manifest", 0);
        CanonicalState readable = State(
            [("readable-keys", "500/500")],
            schema: "kv",
            metadata: "zero-copy-clone");
        var branchObservers = new Dictionary<string, ObserverObservation>(StringComparer.Ordinal)
        {
            ["Db"] = Success(readable),
            ["DbReader"] = new ObserverObservation(OutcomeClass.NotFound, null, "parent-resident SST resolved under clone root"),
        };
        var referenceObservers = new Dictionary<string, ObserverObservation>(StringComparer.Ordinal)
        {
            ["Db"] = Success(readable),
            ["DbReader"] = Success(readable),
        };
        var scenario = new BranchScenario(
            "slatedb-1902-reader-dependency",
            BranchCapabilityProfile.Create("SlateDB", equivalentObservers: ["Db", "DbReader"]),
            boundary,
            readable,
            readable,
            [
                new TraceFrame(
                    "primary-read",
                    Success(readable),
                    Success(readable),
                    OperationClass: TraceOperationClass.GenericRead),
                new TraceFrame(
                    "observe",
                    Success(readable),
                    Success(readable),
                    branchObservers,
                    referenceObservers,
                    TraceOperationClass.ObserverRead),
            ],
            ExpectedFailingRelationId: "BC.observer-dependency",
            CreationEvidence: CreationEvidenceKind.Values);

        return new HistoricalIssueCase(
            "SlateDB",
            1902,
            "DbReader cannot read a zero-copy clone",
            "https://github.com/slatedb/slatedb/issues/1902",
            "closed/completed; bug",
            scenario,
            "The same fresh clone reads 500/500 keys through Db while DbReader resolves parent-resident SSTs under the child root and returns NotFound.");
    }

    private static CanonicalState State(
        IEnumerable<(string Key, string Value)> values,
        string schema,
        string metadata,
        string? continuationToken = null,
        IReadOnlyDictionary<string, BranchBoundary>? boundaries = null)
        => CanonicalState.Create(
            values.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value)),
            schema,
            metadata,
            continuationToken,
            boundaries);

    private static ObserverObservation Success(CanonicalState? state)
        => new(OutcomeClass.Success, state);
}
