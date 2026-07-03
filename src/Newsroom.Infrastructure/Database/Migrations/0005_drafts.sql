-- 0005_drafts: nw_Draft + nw_DraftImage for article draft generation
-- (docs/02-functional-spec.md §4, docs/05-integrations/ai-generation.md). Single batch, no GO.

CREATE TABLE dbo.nw_Draft (
    Id                bigint         NOT NULL IDENTITY(1,1) PRIMARY KEY,
    TopicId           int            NOT NULL REFERENCES dbo.nw_Topic(Id),
    Version           int            NOT NULL DEFAULT 1,          -- regeneration = new version, old kept
    Status            nvarchar(30)   NOT NULL DEFAULT 'Generating',
    Headline          nvarchar(300)  NULL,
    Subtitle          nvarchar(500)  NULL,
    BodyMarkdown      nvarchar(max)  NULL,
    Category          nvarchar(100)  NULL,
    Region            nvarchar(100)  NULL,
    TagsJson          nvarchar(max)  NULL,                        -- JSON array of Bulgarian tags
    SeoTitle          nvarchar(200)  NULL,
    SeoDescription    nvarchar(300)  NULL,
    SourcesJson       nvarchar(max)  NULL,                        -- JSON array of {url, sourceName}
    FlaggedClaimsJson nvarchar(max)  NULL,                        -- generation + self-check flags for the editor
    Confidence        float          NULL,                        -- model's own 0..1 estimate
    ImageAltTextBg    nvarchar(500)  NULL,
    PromptVersion     nvarchar(30)   NOT NULL DEFAULT 'draft-v1', -- prompts are versioned artifacts
    Provider          nvarchar(50)   NULL,
    Model             nvarchar(100)  NULL,
    TokensIn          int            NOT NULL DEFAULT 0,
    TokensOut         int            NOT NULL DEFAULT 0,
    Cost              decimal(12,6)  NOT NULL DEFAULT 0,
    Error             nvarchar(max)  NULL,                        -- set on GenerationFailed
    CreatedAtUtc      datetime2      NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc      datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_nw_Draft_Status ON dbo.nw_Draft (Status);
CREATE INDEX IX_nw_Draft_TopicId ON dbo.nw_Draft (TopicId);

CREATE TABLE dbo.nw_DraftImage (
    Id           bigint         NOT NULL IDENTITY(1,1) PRIMARY KEY,
    DraftId      bigint         NOT NULL REFERENCES dbo.nw_Draft(Id),
    Ordinal      int            NOT NULL,           -- review-surface cycling order
    SourceKind   nvarchar(20)   NOT NULL,           -- 'stock' | 'library' | 'ai' | 'editor-upload' (ADR-0009)
    Url          nvarchar(2000) NOT NULL,
    ThumbUrl     nvarchar(2000) NULL,
    ProviderName nvarchar(50)   NULL,
    Attribution  nvarchar(500)  NULL,               -- shown in caption/credit per licence
    AltTextBg    nvarchar(500)  NULL,
    Selected     bit            NOT NULL DEFAULT 0, -- editor's pick (Phase 4)
    CreatedAtUtc datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_nw_DraftImage_DraftId ON dbo.nw_DraftImage (DraftId);

-- Poison protection: topics whose generation keeps failing stop being selected.
ALTER TABLE dbo.nw_Topic ADD DraftAttempts int NOT NULL DEFAULT 0;
