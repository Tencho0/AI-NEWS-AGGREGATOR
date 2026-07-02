-- 0003_analysis: nw_ArticleAnalysis + nw_CostLedger + analysis retry counter
-- (docs/04-technical-spec.md, docs/adr/0010). Single batch, no GO.

CREATE TABLE dbo.nw_ArticleAnalysis (
    ArticleId    bigint         NOT NULL PRIMARY KEY REFERENCES dbo.nw_SourceArticle(Id),
    Summary      nvarchar(max)  NULL,
    Category     nvarchar(100)  NULL,
    RegionScore  float          NOT NULL DEFAULT 0,   -- 0..1 relevance to Southwest Bulgaria
    EntitiesJson nvarchar(max)  NULL,                 -- JSON array of people/orgs/places
    Language     nvarchar(10)   NULL,                 -- ISO 639-1 of the source article
    Relevant     bit            NOT NULL DEFAULT 1,
    Provider     nvarchar(50)   NOT NULL,
    Model        nvarchar(100)  NOT NULL,
    TokensIn     int            NOT NULL DEFAULT 0,
    TokensOut    int            NOT NULL DEFAULT 0,
    Cost         decimal(12,6)  NOT NULL DEFAULT 0,
    CreatedAtUtc datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Every AI call. Doubles as the free-tier request-quota ledger (ADR-0010):
-- daily budgets are enforced by counting today's rows per stage.
CREATE TABLE dbo.nw_CostLedger (
    Id           bigint         NOT NULL IDENTITY(1,1) PRIMARY KEY,
    Stage        nvarchar(50)   NOT NULL,
    Provider     nvarchar(50)   NOT NULL,
    Model        nvarchar(100)  NOT NULL,
    RequestCount int            NOT NULL DEFAULT 1,
    TokensIn     int            NOT NULL DEFAULT 0,
    TokensOut    int            NOT NULL DEFAULT 0,
    Cost         decimal(12,6)  NOT NULL DEFAULT 0,
    AtUtc        datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_nw_CostLedger_AtUtc_Stage ON dbo.nw_CostLedger (AtUtc, Stage);

ALTER TABLE dbo.nw_SourceArticle ADD AnalysisAttempts int NOT NULL DEFAULT 0;
