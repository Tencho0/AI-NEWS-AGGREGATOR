-- 0007_publishing: nw_PublishRecord for per-destination publish outcomes
-- (docs/02-functional-spec.md §6, docs/05-integrations/umbraco.md, ADR-0007). Single batch, no GO.

-- One row per publish attempt outcome. Destination is 'umbraco' in Phase 5; Facebook (Phase 6)
-- adds a destination value, not a schema change. A terminal rejection is written with
-- Attempts = the configured cap, so the attempt gate treats it as exhausted immediately.
CREATE TABLE dbo.nw_PublishRecord (
    Id          bigint         NOT NULL IDENTITY(1,1) PRIMARY KEY,
    DraftId     bigint         NOT NULL REFERENCES dbo.nw_Draft(Id),
    Destination nvarchar(20)   NOT NULL,           -- 'umbraco' | 'facebook' (Phase 6)
    ExternalId  nvarchar(100)  NULL,               -- Umbraco content key / FB post id
    ExternalUrl nvarchar(2000) NULL,               -- live article URL (also feeds Phase 6)
    Status      nvarchar(20)   NOT NULL,           -- 'Succeeded' | 'Failed'
    Error       nvarchar(max)  NULL,               -- set on Failed
    Attempts    int            NOT NULL DEFAULT 1, -- attempt weight of this row (see above)
    AtUtc       datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

-- At most one successful publish per draft per destination (idempotency backstop; the
-- endpoint's externalRef check is the primary guard).
CREATE UNIQUE INDEX UX_nw_PublishRecord_Draft_Destination_Succeeded
    ON dbo.nw_PublishRecord (DraftId, Destination) WHERE Status = 'Succeeded';

CREATE INDEX IX_nw_PublishRecord_DraftId ON dbo.nw_PublishRecord (DraftId);
