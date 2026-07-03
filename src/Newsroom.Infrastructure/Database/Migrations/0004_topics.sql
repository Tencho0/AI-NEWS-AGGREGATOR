-- 0004_topics: nw_Topic + nw_TopicArticle for trend detection
-- (docs/02-functional-spec.md §3, docs/adr/0010). Single batch, no GO.

CREATE TABLE dbo.nw_Topic (
    Id              int            NOT NULL IDENTITY(1,1) PRIMARY KEY,
    Label           nvarchar(300)  NOT NULL,   -- concise Bulgarian name of the story
    Status          nvarchar(20)   NOT NULL DEFAULT 'Emerging',
    Score           float          NOT NULL DEFAULT 0,
    FirstSeenAtUtc  datetime2      NOT NULL DEFAULT SYSUTCDATETIME(),
    LastScoredAtUtc datetime2      NULL,
    MutedUntilUtc   datetime2      NULL        -- editor /mute; muted topics still collect articles
);

CREATE INDEX IX_nw_Topic_Status ON dbo.nw_Topic (Status);

CREATE TABLE dbo.nw_TopicArticle (
    TopicId    int       NOT NULL REFERENCES dbo.nw_Topic(Id),
    ArticleId  bigint    NOT NULL REFERENCES dbo.nw_SourceArticle(Id),
    AddedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (TopicId, ArticleId)
);

-- v1 rule: an article belongs to exactly one topic, which makes "unassigned" a simple NOT EXISTS.
CREATE UNIQUE INDEX UX_nw_TopicArticle_ArticleId ON dbo.nw_TopicArticle (ArticleId);