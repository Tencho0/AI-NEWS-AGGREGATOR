-- 0001_initial: nw_Config, nw_Source, nw_Log (docs/04-technical-spec.md)
-- Single batch, no GO separators. Forward-only, backward-compatible.

CREATE TABLE dbo.nw_Config (
    [Key]        nvarchar(200)  NOT NULL PRIMARY KEY,
    [Value]      nvarchar(max)  NULL,
    UpdatedAtUtc datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.nw_Source (
    Id               int            NOT NULL IDENTITY(1,1) PRIMARY KEY,
    Name             nvarchar(200)  NOT NULL,
    Kind             nvarchar(20)   NOT NULL,            -- rss | sitemap | html
    Url              nvarchar(2000) NOT NULL,
    ParserHint       nvarchar(max)  NULL,
    IntervalMinutes  int            NOT NULL DEFAULT 10,
    Enabled          bit            NOT NULL DEFAULT 1,
    PolitenessDelaySeconds int      NOT NULL DEFAULT 10,
    LastCrawledAtUtc datetime2      NULL,
    LastError        nvarchar(max)  NULL,
    ConsecutiveFailures int         NOT NULL DEFAULT 0,
    CreatedAtUtc     datetime2      NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Shaped for Serilog.Sinks.MSSqlServer standard columns so the warning+ SQL sink
-- (docs/07-operations.md) can be wired later without a schema change.
CREATE TABLE dbo.nw_Log (
    Id              bigint         NOT NULL IDENTITY(1,1) PRIMARY KEY,
    [Message]       nvarchar(max)  NULL,
    MessageTemplate nvarchar(max)  NULL,
    [Level]         nvarchar(16)   NULL,
    [TimeStamp]     datetime2      NOT NULL DEFAULT SYSUTCDATETIME(),
    Exception       nvarchar(max)  NULL,
    Properties      nvarchar(max)  NULL
);
