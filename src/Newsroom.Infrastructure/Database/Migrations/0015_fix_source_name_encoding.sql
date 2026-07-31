-- 0015_fix_source_name_encoding: restore the Cyrillic source names stored as CP1252 mojibake
-- Single batch, no GO separators. Forward-only, backward-compatible, idempotent.
--
-- Cause: tools/seed-sources.sql was UTF-8 *without* a BOM and was applied with sqlcmd without
-- -f 65001, so sqlcmd decoded the file using the client ANSI codepage (1252). Every N'...'
-- literal already held mojibake by the time SQL Server received it ('БТА' arrived as six
-- characters starting U+00D0 U+2018), and nvarchar stored those characters verbatim -- the
-- daily digest then rendered them faithfully. Url is ASCII, so it was never corrupted and is
-- the safe key to repair on.
--
-- This script is an embedded resource read by MigrationRunner through StreamReader (UTF-8),
-- never through sqlcmd, so the literals below are safe. It deliberately never spells out a
-- mojibake literal of its own: the string to replace is read back out of the database, which
-- keeps the file immune to the very bug it fixes (guarded by EmbeddedMigrationsTests).

DECLARE @seed TABLE (Url nvarchar(2000) NOT NULL, Name nvarchar(200) NOT NULL);
INSERT INTO @seed (Url, Name) VALUES
    (N'https://www.bta.bg/bg/rss/free',    N'БТА (свободна лента)'),
    (N'https://www.mediapool.bg/rss',      N'Mediapool'),
    (N'https://struma.bg/feed',            N'Струма'),
    (N'https://toppresa.com/feed',         N'Топ Преса'),
    (N'https://infomreja.bg/rss',          N'ИнфоМрежа'),
    (N'https://www.blagoevgrad24.bg/rss/', N'Благоевград24');

-- Every comparison against a stored name is binary. The mojibake contains C1 control characters
-- (U+0081 and U+0090 in this data) and a linguistic collation may treat those as ignorable, which
-- would make a corrupted name compare equal to the correct one and silently skip the repair.
DECLARE @repairs TABLE (
    Id      int           IDENTITY(1,1) PRIMARY KEY,
    OldName nvarchar(200) NOT NULL,
    NewName nvarchar(200) NOT NULL);

INSERT INTO @repairs (OldName, NewName)
SELECT s.Name, c.Name
FROM dbo.nw_Source s
JOIN @seed c ON c.Url = s.Url
WHERE s.Name COLLATE Latin1_General_BIN2 <> c.Name COLLATE Latin1_General_BIN2;

-- nw_Draft.SourcesJson denormalises the source name at draft time, so the corrupted spelling is
-- frozen into every draft already written and shows up in the review card's Източници list.
-- Rewrite those first, while nw_Source still holds the spelling they were written with.
DECLARE @id int, @old nvarchar(200), @new nvarchar(200), @escaped nvarchar(1400);
DECLARE @backslash nchar(1) = NCHAR(0x005C);
SELECT @id = MIN(Id) FROM @repairs;
WHILE @id IS NOT NULL
BEGIN
    SELECT @old = OldName, @new = NewName FROM @repairs WHERE Id = @id;

    UPDATE dbo.nw_Draft
    SET SourcesJson = REPLACE(
            SourcesJson COLLATE Latin1_General_BIN2,
            @old COLLATE Latin1_General_BIN2,
            @new)
    WHERE SourcesJson IS NOT NULL
      AND CHARINDEX(
            @old COLLATE Latin1_General_BIN2,
            SourcesJson COLLATE Latin1_General_BIN2) > 0;

    -- The pass above only finds names that survive JSON serialisation unchanged. System.Text.Json
    -- escapes C1 control characters, so a name holding one was frozen into SourcesJson with a
    -- six-character backslash-u escape in its place and is not byte-identical to nw_Source.Name.
    -- Those characters exist because CP1252 leaves exactly five bytes unmapped (0x81, 0x8D, 0x8F,
    -- 0x90, 0x9D) and the decode passes them through as the matching code points, so rebuilding
    -- the escaped spelling means substituting those five and no more. Finding them needs a binary
    -- collation for the same ignorable-character reason as above.
    SET @escaped = @old COLLATE Latin1_General_BIN2;
    SET @escaped = REPLACE(@escaped COLLATE Latin1_General_BIN2,
        NCHAR(0x0081) COLLATE Latin1_General_BIN2, @backslash + N'u0081');
    SET @escaped = REPLACE(@escaped COLLATE Latin1_General_BIN2,
        NCHAR(0x008D) COLLATE Latin1_General_BIN2, @backslash + N'u008d');
    SET @escaped = REPLACE(@escaped COLLATE Latin1_General_BIN2,
        NCHAR(0x008F) COLLATE Latin1_General_BIN2, @backslash + N'u008f');
    SET @escaped = REPLACE(@escaped COLLATE Latin1_General_BIN2,
        NCHAR(0x0090) COLLATE Latin1_General_BIN2, @backslash + N'u0090');
    SET @escaped = REPLACE(@escaped COLLATE Latin1_General_BIN2,
        NCHAR(0x009D) COLLATE Latin1_General_BIN2, @backslash + N'u009d');

    -- Deliberately the database's default (case-insensitive) collation here: the escaped spelling
    -- is the raw one with every C1 character replaced by ASCII, so nothing ignorable is left to
    -- trip over, and case insensitivity matches the escape in whichever hex case it was written.
    IF @escaped COLLATE Latin1_General_BIN2 <> @old COLLATE Latin1_General_BIN2
        UPDATE dbo.nw_Draft
        SET SourcesJson = REPLACE(SourcesJson, @escaped, @new)
        WHERE SourcesJson IS NOT NULL
          AND CHARINDEX(@escaped, SourcesJson) > 0;

    SELECT @id = MIN(Id) FROM @repairs WHERE Id > @id;
END

UPDATE s
SET Name = c.Name
FROM dbo.nw_Source s
JOIN @seed c ON c.Url = s.Url
WHERE s.Name COLLATE Latin1_General_BIN2 <> c.Name COLLATE Latin1_General_BIN2;
