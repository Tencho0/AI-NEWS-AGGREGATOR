-- 0002_source_articles: nw_SourceArticle + conditional-GET/health columns on nw_Source
-- (docs/04-technical-spec.md, docs/05-integrations/scraping.md). Single batch, no GO.

CREATE TABLE dbo.nw_SourceArticle (
    Id             bigint         NOT NULL IDENTITY(1,1) PRIMARY KEY,
    SourceId       int            NOT NULL REFERENCES dbo.nw_Source(Id),
    Url            nvarchar(2000) NOT NULL,
    UrlHash        char(64)       NOT NULL,   -- SHA-256 hex of canonical Url (unique key)
    Title          nvarchar(500)  NOT NULL,
    Author         nvarchar(200)  NULL,
    PublishedAtUtc datetime2      NULL,
    ExtractedText  nvarchar(max)  NULL,       -- internal analysis copy; pruned per retention policy
    ContentHash    char(64)       NOT NULL,   -- wire-copy detection across sources
    Status         nvarchar(20)   NOT NULL DEFAULT 'New',
    FirstSeenAtUtc datetime2      NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc   datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE UNIQUE INDEX UX_nw_SourceArticle_UrlHash ON dbo.nw_SourceArticle (UrlHash);
CREATE INDEX IX_nw_SourceArticle_Status ON dbo.nw_SourceArticle (Status);
CREATE INDEX IX_nw_SourceArticle_ContentHash ON dbo.nw_SourceArticle (ContentHash);
CREATE INDEX IX_nw_SourceArticle_SourceId ON dbo.nw_SourceArticle (SourceId);

ALTER TABLE dbo.nw_Source ADD
    Etag               nvarchar(200) NULL,
    LastModifiedHeader nvarchar(100) NULL,
    LastSuccessAtUtc   datetime2     NULL;
